using System.Text.RegularExpressions;
using AwesomeAssertions.Execution;
using Daedalus.Agents;
using Daedalus.Agents.Security;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Sandbox;
using Thalos.Sandbox.Docker;
using Thalos.Tools;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Phase 2.6 task B3: <c>Thalos:Workflow:Sandbox</c>, the S6 boot check and the wiring of both run modes, over the
///     shipped <c>appsettings.json</c> files. A host is composed through the internal <c>AddDaedalusAgents(options, ...)</c>
///     seam and only built, never started, so no test here needs a Docker engine: the Docker runtime builds its client
///     without connecting, and only the reconcile hosted service, which these tests never start, asks the engine anything.
/// </summary>
public sealed partial class SandboxConfigTests : IDisposable
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private const string CliAppSettingsFileName = "Daedalus.Cli.appsettings.json";

    /// <summary>The Api's own <c>.mcp.json</c>, under the name no other project's copy can take in this output.</summary>
    private static readonly string ApiMcpConfigPath = Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.mcp.json");

    /// <summary>A data root of this test's own, so building a provider never touches the user's real one.</summary>
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("daedalus-sandbox-config-").FullName;

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    /// <summary>
    ///     S6: a grant with no extension list lets a run write project, props and targets files, which MSBuild evaluates,
    ///     so without the sandbox it would run a run's code on the host. The exact S6 message is asserted, so the red is
    ///     the missing refusal and nothing else. Red: delete the S6 throw in <c>ValidateWorkflowWriteConfig</c>, leaving
    ///     the null-list <c>continue</c>; the host then registers and nothing is thrown.
    /// </summary>
    [Fact]
    public void A_grant_allowing_any_extension_fails_boot_without_the_sandbox()
    {
        var (services, options, configuration, environment) = LoadShipped(ApiAppSettingsFileName);
        options.Workflow.Sandbox.Enabled = false;
        options.Workflow.WriteGrants.Should().ContainSingle().Which.AllowedExtensions.Should().BeNull();

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage(
            "Thalos:Workflow:WriteGrants entry 'manufacture/implement' allows every extension, which is only safe inside a " +
            "run sandbox; set Thalos:Workflow:Sandbox:Enabled or list AllowedExtensions.");
    }

    /// <summary>
    ///     Probe for ruling R48: the JSON provider keeps an explicit empty <c>AllowedExtensions</c> apart from a missing
    ///     one, an empty array being the key with an empty value and a missing key having none, although the binder maps
    ///     both to <see langword="null"/>. The raw check depends on that difference. Red: remove <c>[]</c> from the first
    ///     grant in the probe document; its key then reads <see langword="null"/> like the second's.
    /// </summary>
    [Fact]
    public void The_json_provider_keeps_an_empty_extension_list_apart_from_a_missing_one()
    {
        var configuration = LoadJson("""
            { "Thalos": { "Workflow": { "WriteGrants": [
                { "Process": "manufacture", "Node": "implement", "AllowedExtensions": [] },
                { "Process": "manufacture", "Node": "review" } ] } } }
            """);

        using (new AssertionScope())
        {
            configuration["Thalos:Workflow:WriteGrants:0:AllowedExtensions"].Should().Be("");
            configuration["Thalos:Workflow:WriteGrants:1:AllowedExtensions"].Should().BeNull();
            Bind(configuration).Workflow.WriteGrants.Select(g => g.AllowedExtensions).Should().AllSatisfy(e => e.Should().BeNull());
        }
    }

    /// <summary>
    ///     Ruling R48: <c>"AllowedExtensions": []</c> binds as null, which under the sandbox means any extension; an
    ///     explicit empty list must instead fail closed, whatever the sandbox says. Layered over the shipped, sandboxed Api
    ///     file through the JSON provider, as a deploy would write it. Red: delete the R48 check in
    ///     <c>ValidateWorkflowWriteConfig</c>; the host then registers, the grant writing any extension.
    /// </summary>
    [Fact]
    public void An_explicitly_empty_extension_list_fails_boot_even_under_the_sandbox()
    {
        // Two JSON sources on one builder, as a host stacks its files. Not AddConfiguration: a chained configuration
        // reads an empty value as absent, which would hide the very key this test is about.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ApiAppSettingsFileName, optional: false)
            .AddJsonStream(JsonStream("""{ "Thalos": { "Workflow": { "WriteGrants": [ { "AllowedExtensions": [] } ] } } }"""))
            .Build();
        var (services, options, _, environment) = LoadShipped(ApiAppSettingsFileName);
        options = Bind(configuration);
        options.McpConfigPath = ApiMcpConfigPath;
        options.Workflow.DataRoot = _dataRoot;
        options.Workflow.Sandbox.Enabled.Should().BeTrue();
        options.Workflow.WriteGrants.Should().ContainSingle().Which.AllowedExtensions.Should().BeNull();

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage(
            "Thalos:Workflow:WriteGrants:0:AllowedExtensions is present but lists no extensions. An empty list would make " +
            "nothing writable, so it is refused*remove the key to allow any extension*");
    }

    /// <summary>
    ///     Inside a run sandbox every write runs as the sandbox's own caller under the host-wide ceiling, so a grant cannot
    ///     narrow its own node (Thalos issue #251), and differing lists would silently all get the widest. Sandbox mode
    ///     therefore refuses them. Red: delete the uniformity check in <c>ValidateSandboxConfig</c>; the host then
    ///     registers with implement at any extension and review at <c>.md</c>.
    /// </summary>
    [Fact]
    public void Sandboxed_write_grants_with_different_extension_lists_fail_boot()
    {
        var (services, options, configuration, environment) = LoadShipped(ApiAppSettingsFileName);
        options.Workflow.WriteGrants.Add(new WriteGrantConfig { Process = "manufacture", Node = "review", AllowedExtensions = [".md"] });

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage(
            "Thalos:Workflow:WriteGrants entries 'manufacture/implement' and 'manufacture/review' list different " +
            "AllowedExtensions, but per-node narrowing is not available inside a run sandbox (Thalos issue #251)*");
    }

    /// <summary>
    ///     The controls for <see cref="Sandboxed_write_grants_with_different_extension_lists_fail_boot"/>: grants that all
    ///     list no extensions, or all list the same set compared as the ceiling compares it, ignoring case and order,
    ///     register. Red: compare the lists case-sensitively, or by order; the second row then fails.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sandboxed_write_grants_with_the_same_extension_lists_register(bool listed)
    {
        var (services, options, configuration, environment) = LoadShipped(ApiAppSettingsFileName);
        var implement = options.Workflow.WriteGrants.Should().ContainSingle().Subject;
        var review = new WriteGrantConfig { Process = "manufacture", Node = "review" };
        if (listed)
        {
            implement.AllowedExtensions = [".cs", ".md"];
            review.AllowedExtensions = [".MD", ".CS"];
        }

        options.Workflow.WriteGrants.Add(review);

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    /// <summary>
    ///     The same grant with the engine off still fails: the Cli ships its grant with the engine off, and a grant that
    ///     only fails once someone turns the engine on is a grant nobody reviewed. Red: check rule 1 only with the engine on.
    /// </summary>
    [Fact]
    public void A_grant_allowing_any_extension_fails_boot_without_the_sandbox_even_with_the_engine_off()
    {
        var (services, options, configuration, environment) = LoadShipped(CliAppSettingsFileName);
        options.Workflow.Enabled.Should().BeFalse("this test is about the engine-off host");
        options.Workflow.WriteGrants.Should().ContainSingle().Subject.AllowedExtensions = null;

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'manufacture/implement' allows every extension*");
    }

    /// <summary>
    ///     The Api runs every run sandboxed from the image the AppHost builds, and its implement grant lists no extensions.
    ///     Red: restore <c>"AllowedExtensions": [".cs", ".md"]</c> on the grant, or turn the sandbox off, in the Api file.
    /// </summary>
    [Fact]
    public void The_shipped_api_config_enables_the_sandbox_and_lets_implement_write_any_extension()
    {
        var workflow = Bind(Load(ApiAppSettingsFileName)).Workflow;

        using (new AssertionScope())
        {
            workflow.Sandbox.Enabled.Should().BeTrue();
            workflow.Sandbox.Image.Should().Be("daedalus-sandbox:dev");
            workflow.WriteGrants.Should().ContainSingle(g => g.Process == "manufacture" && g.Node == "implement")
                .Which.AllowedExtensions.Should().BeNull();
        }
    }

    /// <summary>
    ///     The Cli has no run sandbox, so it stays in local mode with the <c>.cs</c> and <c>.md</c> list, and registers.
    ///     Red: set <c>Sandbox:Enabled</c> in the Cli file, or drop its grant's list; the second fails the registration.
    /// </summary>
    [Fact]
    public void The_shipped_cli_config_stays_in_local_mode_with_cs_and_md()
    {
        var (services, options, configuration, environment) = LoadShipped(CliAppSettingsFileName);

        using (new AssertionScope())
        {
            options.Workflow.Sandbox.Enabled.Should().BeFalse();
            options.Workflow.WriteGrants.Should().ContainSingle().Which.AllowedExtensions.Should().Equal(".cs", ".md");
        }

        var act = () => services.AddDaedalusAgents(options, configuration, environment);
        act.Should().NotThrow();
    }

    /// <summary>
    ///     The gateway and the egress proxy stand between every run and the network, so the shipped file pins each to one
    ///     immutable image: <c>name:tag@sha256:</c> and 64 hex digits. Red: replace either value with its tag alone.
    /// </summary>
    [Fact]
    public void Shipped_infrastructure_images_are_pinned_by_digest()
    {
        var docker = Bind(Load(ApiAppSettingsFileName)).Workflow.Sandbox.Docker;

        using (new AssertionScope())
        {
            docker.GatewayImage.Should().MatchRegex(DigestPinned().ToString());
            docker.EgressImage.Should().MatchRegex(DigestPinned().ToString());
        }
    }

    /// <summary>
    ///     Ruling R44: Thalos adds <see cref="SandboxOptions.DefaultProtectedPaths"/> to every sandboxed run whatever the
    ///     host lists, so <c>.git/</c> and <c>.github/</c> can no longer be configured away, and Daedalus adds the
    ///     standing-instructions file. Resolved from the built Api host. Red: drop the standing-instructions path from
    ///     <c>ExtraProtectedPaths</c>.
    /// </summary>
    [Fact]
    public async Task Protected_paths_always_include_dot_git_and_dot_github_and_the_standing_instructions_file()
    {
        await using var sp = BuildShippedApi();

        var options = sp.GetRequiredService<SandboxOptions>();
        string[] effective = [.. SandboxOptions.DefaultProtectedPaths, .. options.ProtectedPaths];

        effective.Should().Contain([".git/", ".github/", "AGENT.md"]);
    }

    /// <summary>
    ///     Sandbox mode: the run-scoped <c>roslyn</c> entry is rewritten to remote, and the <c>workspace</c> and
    ///     <c>sandbox</c> tools are served by each run's sandbox, never on the host. Red: skip McpServersLoader's rewrite;
    ///     <c>roslyn</c> is then a <see cref="RunScopedMcpToolSource"/>, and building the provider refuses the local
    ///     run-scoped server.
    /// </summary>
    [Fact]
    public async Task In_sandbox_mode_roslyn_is_remote_and_workspace_and_sandbox_are_remote_sources()
    {
        await using var sp = BuildShippedApi();

        var sources = sp.GetServices<IToolSource>().ToDictionary(s => s.Name, StringComparer.Ordinal);
        using (new AssertionScope())
        {
            sources.Should().ContainKey("roslyn").WhoseValue.Should().BeOfType<RemoteRunToolSource>();
            sources.Should().ContainKey("workspace").WhoseValue.Should().BeOfType<RemoteRunToolSource>();
            sources.Should().ContainKey("sandbox").WhoseValue.Should().BeOfType<RemoteRunToolSource>();
            sp.GetService<IRunWorkspaceProvider>().Should().BeOfType<SandboxRunWorkspaceProvider>();
        }
    }

    /// <summary>
    ///     Publish commits and pushes in the sandbox's trusted publish worktree, so sandbox mode still registers the git
    ///     the publish action needs, and the host's every workflow host action resolves. Red: drop the
    ///     <see cref="IRunWorkspaceGit"/> registration from <c>ConfigureSandboxMode</c>; resolving the host actions
    ///     then fails on <c>OpenPullRequestAction</c>.
    /// </summary>
    [Fact]
    public async Task In_sandbox_mode_publish_has_its_git_and_every_host_action_resolves()
    {
        await using var sp = BuildShippedApi();

        sp.GetService<IRunWorkspaceGit>().Should().BeOfType<GitCliRunWorkspaceGit>();
        sp.GetServices<Thalos.Workflow.IWorkflowHostAction>().Should().ContainSingle().Which.Name.Should().Be("open-pull-request");
    }

    /// <summary>
    ///     The publish git works in the sandbox's publish mirror and worktrees, under <c>&lt;DataRoot&gt;/publish</c>, the
    ///     root <c>UseSandboxRunWorkspaces</c> keeps them in. Observed through the git's own construction, which isolates
    ///     its git configuration in <c>.git-isolation</c> under its data root; the git is resolved first, so nothing else
    ///     has built a git over either root. Red: root the registration at <c>&lt;DataRoot&gt;</c> itself; the isolation
    ///     directory then appears there and not under <c>publish</c>.
    /// </summary>
    [Fact]
    public async Task In_sandbox_mode_the_publish_git_is_rooted_at_the_publish_data_root()
    {
        await using var sp = BuildShippedApi();

        _ = sp.GetRequiredService<IRunWorkspaceGit>();

        using (new AssertionScope())
        {
            Directory.Exists(Path.Combine(_dataRoot, "publish", ".git-isolation")).Should().BeTrue();
            Directory.Exists(Path.Combine(_dataRoot, ".git-isolation")).Should().BeFalse();
        }
    }

    /// <summary>
    ///     <see cref="SandboxConfig.ProtectedPaths"/> extras reach both modes: the sandbox's own list in sandbox mode, and
    ///     the in-process workspace tools' list in local mode. Red: drop <c>workflow.Sandbox.ProtectedPaths</c> from
    ///     <c>ExtraProtectedPaths</c>; neither list then holds the extra.
    /// </summary>
    [Fact]
    public async Task Configured_protected_path_extras_reach_both_modes()
    {
        var (sandboxServices, sandboxOptions, sandboxConfiguration, sandboxEnvironment) = LoadShipped(ApiAppSettingsFileName);
        sandboxOptions.Workflow.Sandbox.ProtectedPaths.Add("deploy/");
        sandboxServices.AddDaedalusAgents(sandboxOptions, sandboxConfiguration, sandboxEnvironment);
        await using var sandboxed = sandboxServices.BuildServiceProvider();

        var (localServices, localOptions, localConfiguration, localEnvironment) = LoadShipped(ApiAppSettingsFileName);
        UseLocalMode(localOptions);
        localOptions.Workflow.Sandbox.ProtectedPaths.Add("deploy/");
        localServices.AddDaedalusAgents(localOptions, localConfiguration, localEnvironment);
        await using var local = localServices.BuildServiceProvider();

        using (new AssertionScope())
        {
            sandboxed.GetRequiredService<SandboxOptions>().ProtectedPaths.Should().Contain("deploy/");
            local.GetRequiredService<RunWorkspaceToolOptions>().ProtectedPaths.Should().Contain("deploy/");
        }
    }

    /// <summary>
    ///     Local mode is phase 2.5's wiring, unchanged except for the shared protected set: a git worktree provider, the
    ///     <c>workspace__*</c> tools in-process, <c>roslyn</c> started per run on the host, and no sandbox at all. Red:
    ///     wire sandbox mode whatever <c>Sandbox:Enabled</c> says.
    /// </summary>
    [Fact]
    public async Task In_local_mode_the_providers_are_todays()
    {
        var (services, options, configuration, environment) = LoadShipped(ApiAppSettingsFileName);
        UseLocalMode(options);
        services.AddDaedalusAgents(options, configuration, environment);
        await using var sp = services.BuildServiceProvider();

        var sources = sp.GetServices<IToolSource>().ToDictionary(s => s.Name, StringComparer.Ordinal);
        using (new AssertionScope())
        {
            sp.GetService<IRunWorkspaceProvider>().Should().BeOfType<GitWorktreeWorkspaceProvider>();
            sources.Should().ContainKey("roslyn").WhoseValue.Should().BeOfType<RunScopedMcpToolSource>();
            sources.Should().ContainKey("workspace").WhoseValue.Should().NotBeOfType<RemoteRunToolSource>();
            sources.Should().NotContainKey("sandbox");
            sp.GetService<SandboxOptions>().Should().BeNull();
        }
    }

    /// <summary>
    ///     Ruling R44, local mode: the in-process workspace tools protect the sandbox's set, Thalos's defaults and the
    ///     standing-instructions file, so <c>.github/</c> is protected in local mode too. Red: add only the
    ///     standing-instructions path to <c>RunWorkspaceToolOptions.ProtectedPaths</c>, as phase 2.5 did.
    /// </summary>
    [Fact]
    public async Task In_local_mode_the_workspace_tools_protect_the_sandbox_defaults_too()
    {
        var (services, options, configuration, environment) = LoadShipped(ApiAppSettingsFileName);
        UseLocalMode(options);
        services.AddDaedalusAgents(options, configuration, environment);
        await using var sp = services.BuildServiceProvider();

        IEnumerable<string> expected = [.. SandboxOptions.DefaultProtectedPaths, "AGENT.md"];
        sp.GetRequiredService<RunWorkspaceToolOptions>().ProtectedPaths.Should().Equal(expected);
    }

    /// <summary>
    ///     Only the two workflow roles may build or test a run: <c>implementer</c> reaches <c>sandbox__build</c> and
    ///     <c>sandbox__test</c>, <c>reviewer</c> only <c>sandbox__test</c>, and no other agent reaches either. Each
    ///     agent's patterns are matched with <see cref="Glob"/>, the matcher the host's tool catalog uses, and an empty
    ///     list counts as <c>*</c>, as the agent mapping treats it, so <c>sand*</c> or <c>*</c> is caught as surely as
    ///     a literal name. Red: add <c>sand*</c> to the architect's tools; separately, remove <c>sandbox__test</c> from
    ///     the reviewer's.
    /// </summary>
    [Fact]
    public void No_chat_agent_lists_a_sandbox_tool()
    {
        var options = Bind(Load(ApiAppSettingsFileName));
        string[] sandboxTools = ["sandbox__build", "sandbox__test"];

        var reach = options.Agents.ToDictionary(
            a => a.Name,
            a => sandboxTools.Where(tool => (a.Tools.Count == 0 ? ["*"] : a.Tools).Any(p => Glob.IsMatch(p, tool))).ToList(),
            StringComparer.Ordinal);

        using (new AssertionScope())
        {
            reach.Where(r => r.Value.Count > 0).Select(r => r.Key).Should().BeEquivalentTo(["implementer", "reviewer"]);
            reach["implementer"].Should().BeEquivalentTo(["sandbox__build", "sandbox__test"]);
            reach["reviewer"].Should().BeEquivalentTo(["sandbox__test"]);
        }
    }

    /// <summary>
    ///     No <c>Thalos:ToolPolicies</c> line binds <c>sandbox__*</c>, because the boundary is the source itself: a call
    ///     with no run claim, as a chat or scheduled turn makes, is refused by <see cref="RemoteRunToolSource"/> and never
    ///     reaches a sandbox or the host. Asserted here rather than assumed. Red: skip <c>UseSandboxRunWorkspaces</c> in
    ///     sandbox mode; no <c>sandbox</c> source is then registered.
    /// </summary>
    [Fact]
    public async Task A_sandbox_call_without_a_run_claim_is_refused()
    {
        await using var sp = BuildShippedApi();
        var sandbox = sp.GetServices<IToolSource>().Should().ContainSingle(s => s.Name == "sandbox").Subject;

        var tools = await sandbox.GetToolsAsync(CancellationToken.None);
        tools.IsSuccess.Should().BeTrue();
        var build = tools.Value.OfType<AIFunction>().Should().ContainSingle(t => t.Name == "build").Subject;
        var result = await build.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal), CancellationToken.None);

        result?.ToString().Should().Be("error: 'sandbox' tools are only available inside a workflow run.");
    }

    /// <summary>
    ///     Carry from Part A: in sandbox mode <c>roslyn</c> is a <see cref="RemoteRunToolSource"/> made from the host
    ///     MCP server, which sends a run's call only to that run's sandbox and, like <see cref="RunScopedMcpToolSource"/>,
    ///     serves a caller with no run claim from the host server; so the shipped <c>csharp-write</c> bindings pass the
    ///     host-start check. Red: accept only <see cref="RunScopedMcpToolSource"/> in
    ///     <see cref="CSharpWriteBindingCheck"/>; the sandboxed Api then fails to start.
    /// </summary>
    [Fact]
    public async Task The_shipped_api_host_in_sandbox_mode_passes_the_csharp_write_start_check()
    {
        await using var sp = BuildShippedApi();

        var check = sp.GetServices<IHostedService>().OfType<CSharpWriteBindingCheck>().Should().ContainSingle().Subject;
        var act = () => check.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    ///     Rule 2: a sandbox needs its run image and both infrastructure images, refused here in Daedalus's words before
    ///     Thalos's own registration checks. Red for each row: delete that check; Thalos then throws its own
    ///     <see cref="ArgumentException"/> instead, or for a blank run image nothing does until the first run.
    /// </summary>
    [Theory]
    [InlineData("image", "Thalos:Workflow:Sandbox:Image must not be blank*")]
    [InlineData("gateway", "Thalos:Workflow:Sandbox:Docker:GatewayImage and *must not be blank*")]
    [InlineData("egress", "Thalos:Workflow:Sandbox:Docker:GatewayImage and *must not be blank*")]
    public void An_enabled_sandbox_without_its_images_fails_boot(string missing, string expectedMessage)
    {
        var (services, options, configuration, environment) = LoadShipped(ApiAppSettingsFileName);
        var sandbox = options.Workflow.Sandbox;
        switch (missing)
        {
            case "image":
                sandbox.Image = " ";
                break;
            case "gateway":
                sandbox.Docker.GatewayImage = "";
                break;
            case "egress":
                sandbox.Docker.EgressImage = "";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(missing), missing, "no such image");
        }

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedMessage);
    }

    /// <summary>
    ///     Ruling R33: a sandboxed run's Roslyn server is started on its repository's solution inside the container, so
    ///     with the sandbox on every repository must name one. Red: delete the R33 check in <c>ValidateSandboxConfig</c>;
    ///     the host then registers.
    /// </summary>
    [Fact]
    public void A_sandboxed_repository_without_a_solution_fails_boot()
    {
        var (services, options, configuration, environment) = LoadShipped(ApiAppSettingsFileName);
        options.Workflow.Repositories.Should().ContainSingle().Subject.Solution = null;

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage(
            "Thalos:Workflow:Repositories entry 'sandbox' must name its Solution while the sandbox is enabled: a sandboxed " +
            "run's Roslyn server is started on that solution*");
    }

    /// <summary>
    ///     The control for <see cref="A_sandboxed_repository_without_a_solution_fails_boot"/>: in local mode a repository
    ///     may still leave its solution out. Red: check R33 whatever <c>Sandbox:Enabled</c> says.
    /// </summary>
    [Fact]
    public void A_local_mode_repository_without_a_solution_registers()
    {
        var (services, options, configuration, environment) = LoadShipped(ApiAppSettingsFileName);
        UseLocalMode(options);
        options.Workflow.Repositories.Should().ContainSingle().Subject.Solution = null;

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    /// <summary>
    ///     Ruling R45: Thalos names the gateway and egress containers <c>thalos-sandbox-gateway</c> and
    ///     <c>thalos-sandbox-egress</c> by default, so two hosts on different networks of one engine would take over each
    ///     other's. Daedalus derives both from the network. Asserted on the callback the wiring passes to
    ///     <c>UseDockerSandboxRuntime</c>, because the runtime keeps its options to itself. Red: drop the two name
    ///     assignments from <c>ConfigureDockerSandbox</c>.
    /// </summary>
    [Fact]
    public void The_infrastructure_containers_are_named_after_the_network()
    {
        var docker = Bind(Load(ApiAppSettingsFileName)).Workflow.Sandbox.Docker;
        docker.Network = "daedalus-b8-two";
        var options = new DockerSandboxOptions();

        DaedalusAgentsServiceCollectionExtensions.ConfigureDockerSandbox(options, docker);

        using (new AssertionScope())
        {
            options.InternalNetwork.Should().Be("daedalus-b8-two");
            options.GatewayContainerName.Should().Be("daedalus-b8-two-gateway");
            options.EgressContainerName.Should().Be("daedalus-b8-two-egress");
            options.Validate().IsSuccess.Should().BeTrue();
        }
    }

    private ServiceProvider BuildShippedApi()
    {
        var (services, options, configuration, environment) = LoadShipped(ApiAppSettingsFileName);
        options.Workflow.Sandbox.Enabled.Should().BeTrue("these tests are about the shipped, sandboxed Api");
        services.AddDaedalusAgents(options, configuration, environment);
        return services.BuildServiceProvider();
    }

    /// <summary>Local mode, phase 2.5's wiring: the sandbox off, and the implement grant given the list S6 then requires.</summary>
    private static void UseLocalMode(DaedalusAgentsOptions options)
    {
        options.Workflow.Sandbox.Enabled = false;
        options.Workflow.WriteGrants.Should().ContainSingle().Subject.AllowedExtensions = [".cs", ".md"];
    }

    private (ServiceCollection Services, DaedalusAgentsOptions Options, IConfiguration Configuration, IHostEnvironment Environment) LoadShipped(string fileName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var configuration = Load(fileName);
        var options = Bind(configuration);
        options.McpConfigPath = ApiMcpConfigPath;
        options.Workflow.DataRoot = _dataRoot;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        return (services, options, configuration, environment);
    }

    private static IConfiguration LoadJson(string json) => new ConfigurationBuilder().AddJsonStream(JsonStream(json)).Build();

    private static MemoryStream JsonStream(string json) => new(System.Text.Encoding.UTF8.GetBytes(json));

    private static IConfiguration Load(string fileName) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: false)
            .Build();

    private static DaedalusAgentsOptions Bind(IConfiguration configuration)
    {
        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        return options;
    }

    [GeneratedRegex(@"^[a-z0-9./_-]+:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DigestPinned();
}
