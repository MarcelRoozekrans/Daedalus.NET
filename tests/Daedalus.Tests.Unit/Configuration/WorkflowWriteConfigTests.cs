using AwesomeAssertions.Execution;
using Daedalus.Agents;
using Daedalus.Agents.Git;
using Daedalus.Agents.Security;
using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Sandbox;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Phase 2.5 task B2: the repositories a run may target, the workspace data root, the commit author and the write
///     grants, bound from the shipped <c>appsettings.json</c> files and validated at registration by
///     <c>ValidateWorkflowWriteConfig</c>. Every rejection goes through the internal <c>AddDaedalusAgents(options, ...)</c>
///     seam over the shipped Api configuration with one field changed, so each row fails on its own rule and nothing else.
/// </summary>
public sealed class WorkflowWriteConfigTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private const string CliAppSettingsFileName = "Daedalus.Cli.appsettings.json";

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

    private static (ServiceCollection Services, DaedalusAgentsOptions Options, IConfiguration Configuration, IHostEnvironment Environment) LoadShippedApi() =>
        LoadShipped(ApiAppSettingsFileName);

    private static (ServiceCollection Services, DaedalusAgentsOptions Options, IConfiguration Configuration, IHostEnvironment Environment) LoadShipped(string fileName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var configuration = Load(fileName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        return (services, Bind(configuration), configuration, environment);
    }

    [Fact]
    public void The_shipped_Api_config_binds_the_sandbox_repository_and_the_implement_write_grant()
    {
        var workflow = Bind(Load(ApiAppSettingsFileName)).Workflow;

        workflow.Repositories.Should().ContainSingle().Which.Name.Should().Be("sandbox");
        workflow.WriteGrants.Should().ContainSingle(g => g.Process == "manufacture" && g.Node == "implement");
    }

    /// <summary>The owner's answers to step 1 of task B2, as the shipped Api file carries them.</summary>
    [Fact]
    public void The_shipped_Api_config_carries_the_owner_answers()
    {
        var workflow = Bind(Load(ApiAppSettingsFileName)).Workflow;
        var sandbox = workflow.Repositories.Should().ContainSingle().Subject;

        using (new AssertionScope())
        {
            sandbox.Remote.Should().Be("https://github.com/MarcelRoozekrans/daedalus-sandbox.git");
            sandbox.DefaultBranch.Should().Be("main");
            sandbox.Solution.Should().Be("Sandbox.sln");
            workflow.DataRoot.Should().BeEmpty("the owner left it blank, so the default applies");
            workflow.CommitAuthor.Name.Should().Be("Daedalus");
            workflow.CommitAuthor.Email.Should().Be("daedalus@roozekrans.nl");
        }
    }

    /// <summary>
    ///     Phase 2.6: the Api's runs are sandboxed, so its implement grant lists no extensions and may write any file a
    ///     run needs (S6 allows that only with the sandbox on). Red: restore <c>"AllowedExtensions": [".cs", ".md"]</c>
    ///     in the Api file, or turn its sandbox off.
    /// </summary>
    [Fact]
    public void The_shipped_Api_grant_lists_no_extensions_under_the_sandbox()
    {
        var workflow = Bind(Load(ApiAppSettingsFileName)).Workflow;

        var grant = workflow.WriteGrants.Should().ContainSingle(g => g.Process == "manufacture" && g.Node == "implement").Subject;
        grant.AllowedExtensions.Should().BeNull();
        workflow.Sandbox.Enabled.Should().BeTrue();
    }

    /// <summary>
    ///     Ruling R29, kept for the host with no sandbox: the Cli's grant stays exactly <c>.cs</c> and <c>.md</c>, so it
    ///     cannot drift to a wider list. Red: add an extension to, or drop the list from, the Cli file.
    /// </summary>
    [Fact]
    public void The_shipped_Cli_grant_allows_implement_to_write_exactly_cs_and_md()
    {
        var workflow = Bind(Load(CliAppSettingsFileName)).Workflow;

        var grant = workflow.WriteGrants.Should().ContainSingle(g => g.Process == "manufacture" && g.Node == "implement").Subject;
        grant.AllowedExtensions.Should().Equal(".cs", ".md");
        workflow.Sandbox.Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("name-uppercase", "*Repositories:0:Name*")]
    [InlineData("name-trailing-newline", "*Repositories:0:Name*")]
    [InlineData("name-duplicated", "*Repositories:1:Name*more than once*")]
    [InlineData("remote-blank", "*Repositories:0:Remote*")]
    [InlineData("solution-rooted", "*Repositories:0:Solution*")]
    [InlineData("solution-dot-dot", "*Repositories:0:Solution*")]
    [InlineData("solution-blank", "*Repositories:0:Solution must name*")]
    [InlineData("solution-percent", "*Repositories:0:Solution*must not contain '%'*")]
    [InlineData("data-root-percent", "*Workflow:DataRoot*contains '%'*")]
    [InlineData("roslyn-ready-timeout-zero", "*Workflow:RoslynReadyTimeout*")]
    [InlineData("data-root-relative", "*Workflow:DataRoot*")]
    [InlineData("grant-process-blank", "*WriteGrants:0:Process*")]
    [InlineData("grant-node-blank", "*WriteGrants:0:Node*")]
    [InlineData("grant-duplicated", "*WriteGrants:1 grants 'manufacture'/'implement' more than once*")]
    [InlineData("grant-no-extensions", "*WriteGrants:0:AllowedExtensions must list*")]
    [InlineData("grant-extension-without-dot", "*WriteGrants:0:AllowedExtensions entry 'cs'*")]
    [InlineData("grant-extension-dot-only", "*WriteGrants:0:AllowedExtensions entry '.'*")]
    [InlineData("grant-extension-path", "*WriteGrants:0:AllowedExtensions entry './x'*")]
    [InlineData("grant-extension-trailing-newline", "*WriteGrants:0:AllowedExtensions entry*")]
    [InlineData("author-name-blank", "*CommitAuthor:Name*")]
    [InlineData("author-email-blank", "*CommitAuthor:Email*")]
    public void A_malformed_write_config_fails_registration_naming_the_key(string rule, string expectedMessage)
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        var workflow = options.Workflow;
        var sandbox = workflow.Repositories.Should().ContainSingle().Subject;
        var grant = workflow.WriteGrants.Should().ContainSingle().Subject;

        switch (rule)
        {
            case "name-uppercase":
                sandbox.Name = "Sandbox";
                break;
            case "name-trailing-newline":
                sandbox.Name = "sandbox\n";
                break;
            case "name-duplicated":
                workflow.Repositories.Add(new RepositoryConfig { Name = "sandbox", Remote = sandbox.Remote });
                break;
            case "remote-blank":
                sandbox.Remote = " ";
                break;
            case "solution-rooted":
                sandbox.Solution = "/src/Sandbox.sln";
                break;
            case "solution-dot-dot":
                sandbox.Solution = "../other/Other.sln";
                break;
            case "solution-blank":
                sandbox.Solution = " ";
                break;
            case "solution-percent":
                sandbox.Solution = "%TEMP%/Sandbox.sln";
                break;
            case "data-root-percent":
                workflow.DataRoot = Path.Combine(Path.GetTempPath(), "%USERNAME%", "workflow-data");
                break;
            case "roslyn-ready-timeout-zero":
                workflow.RoslynReadyTimeout = TimeSpan.Zero;
                break;
            case "grant-duplicated":
                workflow.WriteGrants.Add(new WriteGrantConfig { Process = grant.Process, Node = grant.Node, AllowedExtensions = [".md"] });
                break;
            case "data-root-relative":
                workflow.DataRoot = "workflow-data";
                break;
            case "grant-process-blank":
                grant.Process = "";
                break;
            case "grant-node-blank":
                grant.Node = "";
                break;
            case "grant-no-extensions":
                grant.AllowedExtensions = [];
                break;
            case "grant-extension-without-dot":
                grant.AllowedExtensions = [".cs", "cs"];
                break;
            case "grant-extension-dot-only":
                grant.AllowedExtensions = [".cs", "."];
                break;
            case "grant-extension-path":
                grant.AllowedExtensions = [".cs", "./x"];
                break;
            case "grant-extension-trailing-newline":
                grant.AllowedExtensions = [".cs", ".cs\n"];
                break;
            case "author-name-blank":
                workflow.CommitAuthor.Name = "";
                break;
            case "author-email-blank":
                workflow.CommitAuthor.Email = "";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, "no such rule");
        }

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedMessage);
    }

    /// <summary>
    ///     Task B9: <c>csharp-write</c> admits a granted workflow turn, so on a workflow host it may reach only run-scoped
    ///     MCP servers; bound to a host-scoped one, a run would apply code actions to the host's own solution. The row
    ///     points the shipped Api host at an <c>.mcp.json</c> whose <c>roslyn</c> is host-scoped. Red: remove the
    ///     <c>ValidateCSharpWriteBindings</c> call.
    /// </summary>
    [Fact]
    public void A_csharp_write_binding_that_reaches_a_host_scoped_server_fails_registration_with_the_engine_on()
    {
        var act = RegisterWithCSharpWrite("host-scoped", "roslyn__apply_*");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*binds 'roslyn__apply_*' to csharp-write, which reaches MCP server 'roslyn'*not run-scoped*");
    }

    /// <summary>
    ///     Fix round 1: Thalos's glob lets <c>*</c> span the <c>__</c> separator, so a pattern that does not start with a
    ///     literal <c>&lt;source&gt;__</c> can reach any source, whatever <c>.mcp.json</c> says. It is refused even over
    ///     a run-scoped <c>roslyn</c>. Red for every row: treat the text before the first <c>__</c>, or the whole
    ///     pattern, as a glob over the <c>.mcp.json</c> names, the check before this round; <c>*apply*</c> and
    ///     <c>roslyn_*</c> then register. Red for the <c>ros?yn__apply_*</c> row: accept any prefix before <c>__</c>
    ///     without checking it is a valid source name.
    /// </summary>
    [Theory]
    [InlineData("*apply*")]
    [InlineData("roslyn_*")]
    [InlineData("ros*")]
    [InlineData("*")]
    [InlineData("ros?yn__apply_*")]
    [InlineData("__apply_*")]
    public void A_csharp_write_pattern_without_a_literal_source_prefix_fails_registration(string pattern)
    {
        var act = RegisterWithCSharpWrite("run-scoped", pattern);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*binds '{pattern}' to csharp-write, but the pattern does not start with a literal '<source>__'*");
    }

    /// <summary>
    ///     The positive rows: a literal <c>roslyn__</c> prefix over a run-scoped <c>roslyn</c> registers, whatever
    ///     follows it. Red: refuse every glob character anywhere in the pattern; the <c>roslyn__*</c> row then fails.
    /// </summary>
    [Theory]
    [InlineData("roslyn__apply_*")]
    [InlineData("roslyn__*")]
    public void A_csharp_write_pattern_with_a_literal_run_scoped_source_prefix_registers(string pattern)
    {
        var act = RegisterWithCSharpWrite("run-scoped", pattern);

        act.Should().NotThrow();
    }

    /// <summary>
    ///     Fix round 2: a literal <c>roslyn__</c> prefix also matches the tools of a source named <c>roslyn_</c>, which
    ///     are <c>roslyn___tool</c>. A host-scoped <c>roslyn_</c> beside a run-scoped <c>roslyn</c> must therefore be
    ///     refused. Red: drop the <c>name + "_"</c> branch from <c>CSharpWriteBindingCheck.SourcesReachedBy</c>; the host
    ///     then registers.
    /// </summary>
    [Fact]
    public void A_host_scoped_source_named_with_a_trailing_underscore_is_reached_and_refused_at_registration()
    {
        var act = RegisterWithCSharpWrite("roslyn-underscore", "roslyn__*");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*binds 'roslyn__*' to csharp-write, which reaches MCP server 'roslyn_'*not run-scoped*");
    }

    /// <summary>
    ///     The same reach at host start, over a built source named <c>roslyn_</c> that is not run-scoped. Red: drop the
    ///     <c>name + "_"</c> branch; the start check then passes.
    /// </summary>
    [Fact]
    public async Task A_built_source_named_with_a_trailing_underscore_is_refused_at_host_start()
    {
        var source = Substitute.For<IToolSource>();
        source.Name.Returns("roslyn_");
        var check = new CSharpWriteBindingCheck([source], ["roslyn__*"]);

        var act = () => check.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binds 'roslyn__*' to csharp-write, which reaches tool source 'roslyn_'*");
    }

    /// <summary>
    ///     A source that is not in <c>.mcp.json</c> at all, such as the local <c>git</c> source, is invisible at
    ///     registration, so the check at host start refuses it against the sources the container built. Red: skip
    ///     sources that are not MCP servers in the start check, or remove its registration; the host then starts.
    /// </summary>
    [Fact]
    public async Task A_csharp_write_pattern_reaching_a_local_tool_source_fails_at_host_start()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        using var mcp = new TempMcpConfig("run-scoped");
        options.McpConfigPath = mcp.Path;
        options.ToolPolicies.Add(new ToolPolicyConfig { Pattern = "git__*", Policy = "csharp-write" });
        services.AddDaedalusAgents(options, configuration, environment);
        await using var sp = services.BuildServiceProvider();

        var check = sp.GetServices<IHostedService>().OfType<CSharpWriteBindingCheck>().Should().ContainSingle().Subject;
        var act = () => check.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binds 'git__*' to csharp-write, which reaches tool source 'git'*not a run-scoped MCP server*");
    }

    /// <summary>
    ///     The control for the start check in local mode: the shipped bindings over a run-scoped <c>roslyn</c>, a
    ///     <see cref="RunScopedMcpToolSource"/>, pass it. Red: treat every source as not run-scoped. The sandbox-mode
    ///     control is <c>SandboxConfigTests.The_shipped_api_host_in_sandbox_mode_passes_the_csharp_write_start_check</c>.
    /// </summary>
    [Fact]
    public async Task The_shipped_csharp_write_binding_passes_the_host_start_check()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        UseLocalMode(options);
        using var mcp = new TempMcpConfig("run-scoped");
        options.McpConfigPath = mcp.Path;
        services.AddDaedalusAgents(options, configuration, environment);
        await using var sp = services.BuildServiceProvider();

        var check = sp.GetServices<IHostedService>().OfType<CSharpWriteBindingCheck>().Should().ContainSingle().Subject;
        var act = () => check.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    ///     Fix round 1: the <c>workspace__*</c> tools compare a protected path exactly, ignoring case only, against the
    ///     canonical worktree-relative path, so a non-canonical <c>StandingInstructionsPath</c> that boot validation
    ///     accepts must be canonicalised, or implement could rewrite the worktree's <c>AGENT.md</c>. Red: add the
    ///     configured value as it is. Task B11 refuses a <c>..</c> segment at boot, so <c>docs/../AGENT.md</c> left this
    ///     theory for the registration refusals, and a repeated separator took its place.
    /// </summary>
    [Theory]
    [InlineData("./AGENT.md")]
    [InlineData(".//AGENT.md")]
    public async Task A_non_canonical_standing_instructions_path_is_protected_in_its_canonical_form(string configured)
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        UseLocalMode(options);
        options.Workflow.StandingInstructionsPath = configured;
        services.AddDaedalusAgents(options, configuration, environment);
        await using var sp = services.BuildServiceProvider();

        IEnumerable<string> expected = [.. SandboxOptions.DefaultProtectedPaths, "AGENT.md"];
        sp.GetRequiredService<RunWorkspaceToolOptions>().ProtectedPaths.Should().Equal(expected);
    }

    private static Action RegisterWithCSharpWrite(string mcpShape, string pattern)
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Workflow.Enabled.Should().BeTrue("this test is about the workflow host");
        var mcp = new TempMcpConfig(mcpShape);
        options.McpConfigPath = mcp.Path;
        options.ToolPolicies.Clear();
        options.ToolPolicies.Add(new ToolPolicyConfig { Pattern = pattern, Policy = "csharp-write" });
        return () =>
        {
            using (mcp)
            {
                services.AddDaedalusAgents(options, configuration, environment);
            }
        };
    }

    /// <summary>
    ///     A binding that reaches no configured MCP server matches no tool, as on a test host whose <c>.mcp.json</c>
    ///     declares none. Red: refuse every csharp-write binding whose source is not a run-scoped server.
    /// </summary>
    [Theory]
    [InlineData("no-roslyn")]
    [InlineData("no-file")]
    public void A_csharp_write_binding_that_reaches_no_mcp_server_registers(string mcpShape)
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        using var mcp = new TempMcpConfig(mcpShape);
        options.McpConfigPath = mcp.Path;

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    /// <summary>
    ///     The control for the rows above: the same host over a run-scoped <c>roslyn</c> registers. Red: make the check
    ///     ignore <c>RunScoped</c>, so every server counts as host-scoped.
    /// </summary>
    [Fact]
    public void A_csharp_write_binding_to_a_run_scoped_source_registers()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        using var mcp = new TempMcpConfig("run-scoped");
        options.McpConfigPath = mcp.Path;

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    /// <summary>
    ///     With the engine off there is no workflow caller, and <c>csharp-write</c> admits exactly what <c>developer</c>
    ///     does, so the Cli's host-scoped <c>roslyn</c> may keep it. Red: run the check whatever <c>Enabled</c> says.
    /// </summary>
    [Fact]
    public void A_csharp_write_binding_to_a_host_scoped_source_registers_with_the_engine_off()
    {
        var (services, options, configuration, environment) = LoadShipped(CliAppSettingsFileName);
        options.Workflow.Enabled.Should().BeFalse("this test is about the engine-off host");
        options.ToolPolicies.Should().Contain(b => b.Pattern == "roslyn__apply_*" && b.Policy == "csharp-write");
        using var mcp = new TempMcpConfig("host-scoped");
        options.McpConfigPath = mcp.Path;

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    /// <summary>
    ///     The Cli ships the same write grant with the engine off. Validation must not depend on <c>Enabled</c>, or a
    ///     malformed Cli grant would ship unreviewed and fail only once someone turned the engine on.
    /// </summary>
    [Fact]
    public void The_shipped_Cli_config_registers()
    {
        var (services, options, configuration, environment) = LoadShipped(CliAppSettingsFileName);

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_malformed_grant_fails_registration_with_the_engine_off()
    {
        var (services, options, configuration, environment) = LoadShipped(CliAppSettingsFileName);
        options.Workflow.Enabled.Should().BeFalse("this test is about the engine-off host");
        var grant = options.Workflow.WriteGrants.Should().ContainSingle().Subject;
        grant.AllowedExtensions = [.. grant.AllowedExtensions!, "./x"];

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*WriteGrants:0:AllowedExtensions entry './x'*");
    }

    /// <summary>The control for every row above: the shipped Api configuration itself registers cleanly.</summary>
    [Fact]
    public void The_shipped_Api_config_registers()
    {
        var (services, options, configuration, environment) = LoadShippedApi();

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    /// <summary>
    ///     The commit author is only required once a repository can be committed to: Daedalus.Cli ships write grants and no
    ///     repositories, and must still register.
    /// </summary>
    [Fact]
    public void A_host_with_no_repositories_needs_no_commit_author()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Workflow.Repositories.Clear();
        options.Workflow.CommitAuthor.Name = "";
        options.Workflow.CommitAuthor.Email = "";

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    [Fact]
    public void An_absolute_data_root_is_accepted_and_used_as_given()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        var absolute = Path.Combine(Path.GetTempPath(), "daedalus-workflow-data");
        options.Workflow.DataRoot = absolute;

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
        DaedalusAgentsServiceCollectionExtensions.ResolveDataRoot(absolute).Should().Be(absolute);
    }

    [Fact]
    public void A_blank_data_root_resolves_under_local_application_data()
    {
        DaedalusAgentsServiceCollectionExtensions.ResolveDataRoot("").Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Daedalus", "workflow-data"));
    }

    [Fact]
    public async Task With_the_engine_on_the_host_resolves_git_worktree_workspaces_with_GitHub_credentials()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        UseLocalMode(options);
        services.AddDaedalusAgents(options, configuration, environment);
        await using var sp = services.BuildServiceProvider();

        // GetService, not GetRequiredService, so a missing registration fails the assertion rather than throwing. The
        // scope reports every miss, because one call registers both the provider and the workspace git.
        using (new AssertionScope())
        {
            sp.GetService<IGitCredentialSource>().Should().BeOfType<GitHubGitCredentialSource>();
            sp.GetService<IRunWorkspaceProvider>().Should().BeOfType<GitWorktreeWorkspaceProvider>();
            sp.GetService<IRunWorkspaceGit>().Should().BeOfType<GitCliRunWorkspaceGit>();
        }
    }

    /// <summary>
    ///     Task B13: open-pull-request is registered once, as a singleton, so the resolver and the dispatcher, which both
    ///     receive every registered host action, see the same instance.
    /// </summary>
    [Fact]
    public async Task With_the_engine_on_open_pull_request_is_one_singleton_host_action()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        services.AddDaedalusAgents(options, configuration, environment);

        services.Where(d => d.ServiceType == typeof(IWorkflowHostAction))
            .Should().ContainSingle()
            .Which.Should().Match<ServiceDescriptor>(d =>
                d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType == typeof(OpenPullRequestAction));

        await using var sp = services.BuildServiceProvider();
        var first = sp.GetServices<IWorkflowHostAction>().Should().ContainSingle().Subject;
        first.Name.Should().Be("open-pull-request");
        sp.GetServices<IWorkflowHostAction>().Single().Should().BeSameAs(first);
    }

    [Fact]
    public void With_the_engine_off_the_host_registers_no_workspaces_and_no_git_credentials()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Workflow.Enabled = false;
        services.AddDaedalusAgents(options, configuration, environment);
        using var sp = services.BuildServiceProvider();

        using (new AssertionScope())
        {
            sp.GetService<IGitCredentialSource>().Should().BeNull();
            sp.GetService<IRunWorkspaceProvider>().Should().BeNull();
        }
    }

    /// <summary>
    ///     Turns the shipped Api options into local mode, phase 2.5's wiring: the sandbox off, and the implement grant given
    ///     the extension list S6 then requires.
    /// </summary>
    private static void UseLocalMode(DaedalusAgentsOptions options)
    {
        options.Workflow.Sandbox.Enabled = false;
        options.Workflow.WriteGrants.Should().ContainSingle().Subject.AllowedExtensions = [".cs", ".md"];
    }

    /// <summary>A temporary <c>.mcp.json</c> in one of four shapes, deleted on dispose.</summary>
    private sealed class TempMcpConfig : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("daedalus-mcp-").FullName;

        public TempMcpConfig(string shape)
        {
            Path = System.IO.Path.Combine(_directory, ".mcp.json");
            var roslyn = shape switch
            {
                "host-scoped" => """{ "roslyn": { "command": "dnx", "args": ["RoslynCodeLens.Mcp", "--", "C:/host/App.sln"] } }""",
                "run-scoped" => """{ "roslyn": { "command": "dnx", "args": ["RoslynCodeLens.Mcp", "--", "C:/host/App.sln"], "runScoped": { "args": ["RoslynCodeLens.Mcp", "--", "${run.workspace.solution}"] } } }""",
                "no-roslyn" => """{ "context7": { "type": "http", "url": "https://mcp.context7.com/mcp" } }""",
                "roslyn-underscore" => """{ "roslyn": { "command": "dnx", "args": ["RoslynCodeLens.Mcp", "--", "C:/host/App.sln"], "runScoped": { "args": ["RoslynCodeLens.Mcp", "--", "${run.workspace.solution}"] } }, "roslyn_": { "command": "dnx", "args": ["RoslynCodeLens.Mcp", "--", "C:/host/App.sln"] } }""",
                "no-file" => null,
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "no such shape"),
            };
            if (roslyn is not null)
            {
                File.WriteAllText(Path, $$"""{ "mcpServers": {{roslyn}} }""");
            }
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
