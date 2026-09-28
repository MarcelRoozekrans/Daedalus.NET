using AwesomeAssertions.Execution;
using Daedalus.Agents;
using Daedalus.Agents.Git;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos.Git.Workspaces;
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
    ///     Ruling R29: the grant reads the same on both hosts, so neither can drift to a wider list. Leaving the key out
    ///     binds an empty list, which fails this too.
    /// </summary>
    [Theory]
    [InlineData(ApiAppSettingsFileName)]
    [InlineData(CliAppSettingsFileName)]
    public void Both_shipped_files_allow_implement_to_write_exactly_cs_and_md(string fileName)
    {
        var workflow = Bind(Load(fileName)).Workflow;

        var grant = workflow.WriteGrants.Should().ContainSingle(g => g.Process == "manufacture" && g.Node == "implement").Subject;
        grant.AllowedExtensions.Should().Equal(".cs", ".md");
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
                var duplicate = new WriteGrantConfig { Process = grant.Process, Node = grant.Node };
                duplicate.AllowedExtensions.Add(".md");
                workflow.WriteGrants.Add(duplicate);
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
                grant.AllowedExtensions.Clear();
                break;
            case "grant-extension-without-dot":
                grant.AllowedExtensions.Add("cs");
                break;
            case "grant-extension-dot-only":
                grant.AllowedExtensions.Add(".");
                break;
            case "grant-extension-path":
                grant.AllowedExtensions.Add("./x");
                break;
            case "grant-extension-trailing-newline":
                grant.AllowedExtensions.Add(".cs\n");
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
    ///     MCP servers; bound to a host-scoped one, a run would apply code actions to the host's own solution. Each row
    ///     points the shipped Api host at an <c>.mcp.json</c> whose <c>roslyn</c> is host-scoped. Red for every row: remove
    ///     the <c>ValidateCSharpWriteBindings</c> call. Red for the <c>ros*</c> and <c>*</c> rows: compare the source
    ///     name ordinally instead of as a glob.
    /// </summary>
    [Theory]
    [InlineData("roslyn__apply_*")]
    [InlineData("ros*")]
    [InlineData("*")]
    public void A_csharp_write_binding_that_reaches_a_host_scoped_server_fails_registration_with_the_engine_on(string pattern)
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Workflow.Enabled.Should().BeTrue("this test is about the workflow host");
        using var mcp = new TempMcpConfig("host-scoped");
        options.McpConfigPath = mcp.Path;
        options.ToolPolicies.Clear();
        options.ToolPolicies.Add(new ToolPolicyConfig { Pattern = pattern, Policy = "csharp-write" });

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*binds '{pattern}' to csharp-write, which reaches MCP server 'roslyn'*not run-scoped*");
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
        options.Workflow.WriteGrants.Should().ContainSingle().Subject.AllowedExtensions.Add("./x");

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
