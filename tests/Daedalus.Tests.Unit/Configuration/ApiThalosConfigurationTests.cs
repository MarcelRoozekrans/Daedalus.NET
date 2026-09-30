using AI.Sentinel;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Mcp;
using Thalos.Memory.RagNet;
using Thalos.Skills;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Guards the shipped API configuration: <c>.mcp.json</c> flows into this test's output through the Daedalus.Api
///     project reference, and <c>appsettings.json</c> is linked explicitly as <c>Daedalus.Api.appsettings.json</c> (Console
///     and Web ship one too and would otherwise race for the plain name), so the file that is deployed is the file under test.
/// </summary>
public sealed class ApiThalosConfigurationTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private const string ConsoleAppSettingsFileName = "Daedalus.Console.appsettings.json";

    private const string CliAppSettingsFileName = "Daedalus.Cli.appsettings.json";

    private static IConfiguration LoadApiConfiguration() => Load(ApiAppSettingsFileName);

    private static IConfiguration LoadConsoleConfiguration() => Load(ConsoleAppSettingsFileName);

    private static IConfiguration Load(string fileName) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: false)
            .Build();

    private static ServiceProvider BuildWithApiConfiguration()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusAgents(LoadApiConfiguration(), environment);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Appsettings_declares_the_daedalus_architect_agent_with_a_stable_ulid()
    {
        await using var sp = BuildWithApiConfiguration();

        // The catalog also carries the RepoDigest workflow's scout and writer agents; this test only guards
        // the human-facing Daedalus Architect entry and its stable id.
        var agent = sp.GetRequiredService<IAgentCatalog>().Agents.Should().ContainSingle(a => a.Name == "Daedalus Architect").Subject;

        // Sessions reference this id — changing it orphans them. Update the test only together with a data migration.
        agent.Id.Should().Be(AgentId.Parse("01M05YCM7DPKRG9X04870B2JYH", null));
        agent.Name.Should().Be("Daedalus Architect");
        // repoaction__* is on the Architect and nowhere else: it is the interactive agent, so a human is present
        // for every comment, label or close. The unattended scout must never carry it.
        agent.Tools.Should().Equal("roslyn__*", "daedalus__*", "memory__*", "skills__*", "context7__*", "repoaction__*", "manufacture__*");
        agent.Skills.Should().Equal("*");
        agent.Instructions.Should().Contain("roslyn__").And.Contain("daedalus__").And.Contain("memory__").And.Contain("skills__");
    }

    [Fact]
    public async Task Appsettings_binds_anthropic_defaults_tool_policies_and_sentinel_actions()
    {
        await using var sp = BuildWithApiConfiguration();

        sp.GetRequiredService<IChatClientProvider>().DefaultModel.Should().Be("claude-sonnet-5");

        var policies = sp.GetRequiredService<IOptions<ThalosOptions>>().Value.ToolPolicies;
        policies.Select(p => (p.ToolPattern, p.PolicyName)).Should().Equal(
            ("roslyn__apply_*", "csharp-write"),
            ("workspace__write_*", "workspace-write"),
            ("workspace__edit_*", "workspace-write"),
            ("roslyn__change_signature", "csharp-write"),
            ("roslyn__set_active_solution", "developer"),
            ("roslyn__rename_*", "developer"),
            ("roslyn__load_solution", "developer"),
            ("roslyn__unload_solution", "developer"),
            ("roslyn__trust_solution", "developer"),
            ("roslyn__revoke_trust", "developer"),
            ("roslyn__list_trusted_paths", "developer"),
            ("roslyn__rebuild_solution", "developer"),
            ("roslyn__start_background_task", "developer"),
            ("roslyn__get_task_status", "developer"),
            ("roslyn__list_running_tasks", "developer"),
            ("roslyn__find_breaking_changes", "developer"),
            ("repoaction__*", "developer"),
            ("git__*", "developer"),
            ("manufacture__*", "developer"));

        var sentinel = sp.GetRequiredService<SentinelOptions>();
        sentinel.OnCritical.Should().Be(SentinelAction.Quarantine);
        sentinel.OnHigh.Should().Be(SentinelAction.Alert);
        sentinel.OnMedium.Should().Be(SentinelAction.Log);
        sentinel.OnLow.Should().Be(SentinelAction.Log);
    }

    /// <summary>
    ///     Phase 2.5, task B5: the shipped appsettings, in both hosts, binds each write pattern of a run's worktree to
    ///     <c>workspace-write</c> exactly once, and none of them to <c>developer</c>, which a workflow caller can never
    ///     pass, so a leftover one would silently stop <c>implement</c> from writing while every grant looked configured.
    ///     Task B9: <c>roslyn__apply_*</c> is bound to <c>csharp-write</c> alone, which also requires the grant to include
    ///     <c>.cs</c>; a <c>workspace-write</c> binding in its place would let a grant without <c>.cs</c> apply code
    ///     actions. The raw file is read rather than a composed host, so the Cli file is covered too.
    /// </summary>
    [Theory]
    [InlineData(ApiAppSettingsFileName)]
    [InlineData(CliAppSettingsFileName)]
    public void The_shipped_appsettings_binds_each_workspace_write_pattern(string fileName)
    {
        var bindings = Load(fileName).GetSection("Thalos:ToolPolicies").GetChildren()
            .Select(c => (Pattern: c["Pattern"], Policy: c["Policy"]))
            .ToList();

        foreach (var pattern in new[] { "workspace__write_*", "workspace__edit_*" })
        {
            // ContainSingle is also what rules out a second, developer binding: the authorizer evaluates every
            // matching binding, so a leftover one would deny a granted workflow caller.
            bindings.Where(b => string.Equals(b.Pattern, pattern, StringComparison.Ordinal)).Should().ContainSingle(
                    $"{fileName} must bind {pattern} exactly once")
                .Which.Policy.Should().Be("workspace-write");
        }

        // Red: rebinding roslyn__apply_* to workspace-write, which does not check for .cs, or leaving it on developer.
        bindings.Where(b => string.Equals(b.Pattern, "roslyn__apply_*", StringComparison.Ordinal)).Should().ContainSingle(
                $"{fileName} must bind roslyn__apply_* exactly once")
            .Which.Policy.Should().Be("csharp-write", "a code action writes .cs documents, so the grant must include .cs");

        bindings.Should().Contain(("roslyn__set_active_solution", "developer"),
            "switching the loaded solution is an operator action a workflow turn must never take");
    }

    [Fact]
    public async Task Crash_recovery_hosted_service_is_registered()
    {
        await using var sp = BuildWithApiConfiguration();

        sp.GetServices<IHostedService>().Should().ContainSingle(s => s.GetType().Name == "AgentSessionCrashRecovery");
    }

    [Fact]
    public async Task Api_host_owns_the_ragnet_schema_and_creates_it_on_start()
    {
        await using var sp = BuildWithApiConfiguration();

        sp.GetRequiredService<RagNetMemoryOptions>().EnsureSchemaOnStartup.Should().BeTrue();
        sp.GetServices<IHostedService>().Should().ContainSingle(s => s.GetType().Name == "RagNetMemorySchemaInitializer");
    }

    /// <summary>
    ///     The console worker writes Ralph learnings into the same database as the API. If the two hosts disagreed on the
    ///     shared owner, Ralph would write memories nobody recalls; if they disagreed on the vector width, the second host
    ///     to touch <c>rag_chunks</c> would fail. Both files therefore declare the same block, pinned here.
    /// </summary>
    [Fact]
    public void Console_and_api_agree_on_the_shared_memory_settings()
    {
        var api = LoadApiConfiguration().GetSection(MemoryConfig.SectionName);
        var console = LoadConsoleConfiguration().GetSection(MemoryConfig.SectionName);

        console.Exists().Should().BeTrue("the Ralph worker binds Thalos:Memory through AddDaedalusMemory");
        foreach (var key in new[] { "SharedOwnerId", "VectorDimensions", "RalphRecall:TopK", "RalphRecall:MinScore" })
        {
            console[key].Should().Be(api[key], "Thalos:Memory:{0} must match between the API and console hosts", key);
        }
    }

    [Fact]
    public void Console_host_does_not_create_the_ragnet_schema()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusMemory(LoadConsoleConfiguration());
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<RagNetMemoryOptions>().EnsureSchemaOnStartup.Should().BeFalse(
            "the API host creates rag_chunks; concurrent CREATE from both hosts can fail on the pg catalog");
        sp.GetServices<IHostedService>().Should().NotContain(s => s.GetType().Name == "RagNetMemorySchemaInitializer");
    }

    /// <summary>
    ///     <c>UseRagNetMemory</c> is last-call-wins, so a host that called both registrations would silently take the
    ///     later <c>EnsureSchemaOnStartup</c> — on the API that means nobody creates <c>rag_chunks</c> and every memory
    ///     stays <c>index_pending</c> without anything failing. The registration refuses instead.
    /// </summary>
    [Fact]
    public void Registering_memory_twice_throws_instead_of_silently_disabling_schema_creation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusMemory(LoadConsoleConfiguration());

        var second = () => services.AddDaedalusMemory(LoadConsoleConfiguration());

        second.Should().Throw<InvalidOperationException>().WithMessage("*mutually exclusive*");
    }

    [Fact]
    public void Mcp_config_is_copied_next_to_the_api_and_parses()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ".mcp.json");
        File.Exists(path).Should().BeTrue(".mcp.json must be CopyToOutputDirectory=PreserveNewest in Daedalus.Api.csproj");

        var servers = McpConfigFile.Parse(File.ReadAllText(path));

        servers.Keys.Should().BeEquivalentTo("roslyn", "context7");
        servers["roslyn"].EffectiveType.Should().Be("stdio");
        servers["roslyn"].ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(2));
        servers["context7"].EffectiveType.Should().Be("http");
    }

    /// <summary>
    ///     Phase 2.5, task B9: roslyn is run-scoped, so a workflow turn is served by its run's own server over its
    ///     worktree. The values are the Roslyn staleness spike's: <c>list_solutions</c> reports ready once the solution is
    ///     loaded, and <c>rebuild_solution</c> picks up files a run has added. context7 stays host-wide: it reads no
    ///     workspace.
    /// </summary>
    [Fact]
    public void Mcp_config_makes_roslyn_run_scoped_over_the_runs_own_solution()
    {
        var servers = McpConfigFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ".mcp.json")));

        // Red: remove the runScoped block; RunScoped is then null.
        var runScoped = servers["roslyn"].RunScoped;
        runScoped.Should().NotBeNull("a workflow turn must never be served by the host's own solution");
        // Red: point the run's server at the host solution, i.e. drop the placeholder from args.
        runScoped!.Args.Should().Contain("${run.workspace.solution}");
        // Red: set readyTool to another tool, or remove it.
        runScoped.ReadyTool.Should().Be("list_solutions");
        // Red: set reload to "none".
        runScoped.Reload.Should().Be("tool:rebuild_solution");
        // Red: add a runScoped block to context7.
        servers["context7"].RunScoped.Should().BeNull();
    }

    /// <summary>
    ///     Phase 2.5, task B9: the composed Api host registers the <c>workspace__*</c> tools with the union of its write
    ///     grants as the ceiling, the standing-instructions file protected, and the readiness gate before task nodes.
    /// </summary>
    [Fact]
    public async Task The_api_host_registers_the_workspace_tools_and_the_readiness_gate()
    {
        await using var sp = BuildWithApiConfiguration();

        var options = sp.GetRequiredService<RunWorkspaceToolOptions>();
        // Red: omit the ProtectedPaths.Add in UseRunWorkspaceTools' configure.
        options.ProtectedPaths.Should().Contain("AGENT.md");
        // Red: pass an empty set, or one built from something other than WriteGrants, as the ceiling.
        options.AllowedWriteExtensions.Should().BeEquivalentTo(".cs", ".md");
        // Red: remove the gate's registration.
        sp.GetServices<IWorkflowDispatchGate>().Should().ContainSingle().Which.Should().BeOfType<RunToolServersReadyGate>();
        // Red: remove the runScoped block from .mcp.json and rebind roslyn__apply_* to developer, since with csharp-write
        // bound the boot guard refuses the host first; nothing then registers the readiness the gate waits on.
        sp.GetService<IRunToolServerReadiness>().Should().NotBeNull();
    }

    /// <summary>
    ///     The starter skills flow into this test's output through the Daedalus.Api project reference (a Content item,
    ///     like .mcp.json), which is exactly how they reach the API's content root at runtime. A broken copy would
    ///     otherwise only show up as an agent that quietly has no procedures.
    /// </summary>
    [Fact]
    public void Starter_skills_are_copied_next_to_the_api()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "skills");

        Directory.Exists(root).Should().BeTrue("skills/**/SKILL.md must be a Content item in Daedalus.Api.csproj");
        foreach (var name in new[] { "daedalus-migrations", "thalos-release" })
        {
            var path = Path.Combine(root, name, "SKILL.md");
            File.Exists(path).Should().BeTrue();
            var text = File.ReadAllText(path);
            text.Should().StartWith("---").And.Contain($"name: {name}").And.Contain("description:");
        }
    }

    [Fact]
    public void Appsettings_points_the_skill_roots_at_the_shipped_folder()
    {
        LoadApiConfiguration().GetSection("Thalos:Skills:Roots").Get<string[]>().Should().Equal("skills");
    }

    [Fact]
    public async Task A_relative_skills_root_falls_back_to_the_assembly_directory()
    {
        // Regression, caught by the AppHost smoke run. Under `dotnet run` (and therefore under Aspire) the content root
        // is the *project* directory, but the skills folder is only ever copied to the *output* directory. Resolving
        // against the content root alone killed every development host at startup while the tests and the container
        // image stayed green - in a published app the content root IS the output directory, and the test hosts had been
        // pointed at their own output. The Content copy puts the folder next to the assembly in both layouts, so that
        // is the fallback.
        var contentRootWithoutSkills = Directory.CreateTempSubdirectory("daedalus-no-skills-").FullName;
        var expected = Path.Combine(AppContext.BaseDirectory, "skills");
        Directory.Exists(expected).Should().BeTrue("the Content item copies skills next to this test assembly");
        try
        {
            var env = Substitute.For<IHostEnvironment>();
            env.ContentRootPath.Returns(contentRootWithoutSkills);
            env.EnvironmentName.Returns("Development");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
            services.AddDaedalusAgents(LoadApiConfiguration(), env);
            await using var sp = services.BuildServiceProvider();

            sp.GetRequiredService<IOptions<SkillOptions>>().Value.Roots
                .Should().ContainSingle().Which.Should().Be(expected);
        }
        finally
        {
            Directory.Delete(contentRootWithoutSkills, recursive: true);
        }
    }
}
