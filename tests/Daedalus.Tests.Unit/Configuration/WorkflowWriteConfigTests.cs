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

    private static (ServiceCollection Services, DaedalusAgentsOptions Options, IConfiguration Configuration, IHostEnvironment Environment) LoadShippedApi()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var configuration = Load(ApiAppSettingsFileName);
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
    [InlineData("data-root-relative", "*Workflow:DataRoot*")]
    [InlineData("grant-process-blank", "*WriteGrants:0:Process*")]
    [InlineData("grant-node-blank", "*WriteGrants:0:Node*")]
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
    public void With_the_engine_on_the_host_resolves_git_worktree_workspaces_with_GitHub_credentials()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        services.AddDaedalusAgents(options, configuration, environment);
        using var sp = services.BuildServiceProvider();

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
}
