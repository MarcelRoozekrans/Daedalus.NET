using System.Security.Claims;
using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Security;
using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Phase 2.5, task B5: the <c>workspace-write</c> boundary for a run's worktree, pinned against a real, workflow-enabled
///     <c>Daedalus.Api</c> host booted from the shipped <c>appsettings.json</c>. Every run comes from the registered
///     <see cref="IManufactureRunStarter"/>, so its manifest and starter are exactly what production writes (ruling R15);
///     every workflow caller comes from <see cref="WorkflowNodeDispatcherFactory.CreateCallerResolver"/>, the dispatcher's
///     own resolver; and every decision comes from the host's own <see cref="IToolAuthorizer"/>.
/// </summary>
/// <remarks>
///     <para>
///     A test moves a copy of a started run to another node with <c>with { CurrentNode = node }</c>. The copy exists only
///     in the test and is never written; nothing else about the run changes.
///     </para>
///     <para>
///     The tests that need a tool's own answer use the host's own <see cref="WorkspaceTools"/>, over its registered
///     <see cref="RunWorkspaceToolOptions"/> (task B9). The extension test boots a host whose config adds a second
///     grant, on <c>review</c>, for <c>.props</c> and <c>.json</c>, so the real ceiling, the union of the grants,
///     admits both, and what narrows implement's writes to <c>.cs</c> and <c>.md</c> is its own grant claim.
///     <see cref="Daedalus.Tests.Integration.Workflow.WorkspaceExtensionBoundaryTests"/> covers the shipped ceiling.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class WorkspaceWriteBoundaryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly RunPrincipal Admin = new("u-admin", ["admin"]);

    private ScratchWorkflowHost _host = null!;

    private static JsonElement PathArgs => JsonSerializer.SerializeToElement(new { path = "src/A.cs", content = "x" });

    public async Task InitializeAsync() => _host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>
    ///     Red: drop the grant from the resolver, so every caller is built with <c>grant: null</c>.
    /// </summary>
    [Theory]
    [InlineData("workspace__write_file")]
    [InlineData("workspace__edit_file")]
    public async Task Implement_of_an_admin_started_run_may_write_its_workspace(string tool) =>
        (await Authorize(_host, Caller(_host, await RunAt(_host, "implement", Admin)), tool)).Allowed.Should().BeTrue();

    /// <summary>
    ///     Task B9: Roslyn is run-scoped, so a granted implement turn may apply code actions, in its own run's worktree.
    ///     Its local-mode grant, <c>.cs</c> and <c>.md</c> from <c>ApiWebApplicationFactory</c>, includes <c>.cs</c>. Red: leave <c>roslyn__apply_*</c> on <c>developer</c>.
    /// </summary>
    [Fact]
    public async Task A_granted_implement_turn_whose_grant_includes_cs_may_apply_code_actions() =>
        (await Authorize(_host, Caller(_host, await RunAt(_host, "implement", Admin)), "roslyn__apply_code_action")).Allowed.Should().BeTrue();

    /// <summary>
    ///     RoslynCodeLens writes <c>.cs</c> documents with no extension check, so a grant without <c>.cs</c> must not reach
    ///     <c>roslyn__apply_*</c>, even though it passes <c>workspace-write</c>. Red: bind <c>roslyn__apply_*</c> to
    ///     <c>workspace-write</c>; the first assertion then passes the call.
    /// </summary>
    [Fact]
    public async Task A_granted_implement_turn_whose_grant_lacks_cs_may_not_apply_code_actions()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture, Substitute.For<IAgentRuntime>(), settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Thalos:Workflow:WriteGrants:0:AllowedExtensions:0"] = ".md",
                ["Thalos:Workflow:WriteGrants:0:AllowedExtensions:1"] = ".txt",
            });
        var caller = Caller(host, await RunAt(host, "implement", Admin));

        (await Authorize(host, caller, "roslyn__apply_code_action")).Allowed.Should().BeFalse();
        // The control: the same caller still holds its workspace grant, so the refusal above is the missing .cs.
        (await Authorize(host, caller, "workspace__write_file")).Allowed.Should().BeTrue();
    }

    /// <summary>
    ///     A test that sets one entry of the implement grant's list gets exactly that list, not the factory's local-mode
    ///     default merged in by index. Red: have <c>ApiWebApplicationFactory</c> pre-set its <c>.cs</c> and <c>.md</c>
    ///     whatever the caller sets; the list is then <c>.txt</c> and <c>.md</c>.
    /// </summary>
    [Fact]
    public async Task A_test_that_sets_one_extension_gets_exactly_that_list()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture, Substitute.For<IAgentRuntime>(), settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Thalos:Workflow:WriteGrants:0:AllowedExtensions:0"] = ".txt",
            });

        host.Factory.Services.GetRequiredService<WorkflowConfig>().WriteGrants
            .Should().ContainSingle().Which.AllowedExtensions.Should().Equal(".txt");
    }

    /// <summary>
    ///     Both nodes are task nodes pinned in the manifest and started by an admin, so only the grant's node comparison
    ///     tells them from <c>implement</c>. Red: drop the <c>Node</c> comparison in <see cref="WorkspaceWriteGrant.GrantFor"/>.
    /// </summary>
    [Theory]
    [InlineData("review")]
    [InlineData("retrospect")]
    public async Task Other_nodes_of_the_same_run_may_not(string node) =>
        (await Authorize(_host, Caller(_host, await RunAt(_host, node, Admin)), "workspace__write_file")).Allowed.Should().BeFalse();

    /// <summary>A run started before 0.11.0 has a manifest but no starter. Red: treat a null starter as qualifying.</summary>
    [Fact]
    public async Task A_run_with_no_starter_may_not()
    {
        var run = (await RunAt(_host, "implement", Admin)) with { StartedBy = null };

        (await Authorize(_host, Caller(_host, run), "workspace__edit_file")).Allowed.Should().BeFalse();
    }

    /// <summary>Red: drop the starter-role check in <see cref="WorkspaceWriteGrant.GrantFor"/>.</summary>
    [Fact]
    public async Task A_run_started_by_a_non_developer_may_not() =>
        (await Authorize(_host, Caller(_host, await RunAt(_host, "implement", new RunPrincipal("u", ["analyst"]))), "workspace__write_file"))
            .Allowed.Should().BeFalse();

    /// <summary>
    ///     A node that is not a task node has no manifest pin. Red: drop the manifest check, since <c>gate</c> is given
    ///     a grant of its own here.
    /// </summary>
    [Fact]
    public async Task A_configured_node_that_is_not_pinned_in_the_manifest_may_not()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture, Substitute.For<IAgentRuntime>(), settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Thalos:Workflow:WriteGrants:1:Process"] = "manufacture",
                ["Thalos:Workflow:WriteGrants:1:Node"] = "gate",
                ["Thalos:Workflow:WriteGrants:1:AllowedExtensions:0"] = ".cs",
            });

        (await Authorize(host, Caller(host, await RunAt(host, "gate", Admin)), "workspace__write_file")).Allowed.Should().BeFalse();
    }

    /// <summary>
    ///     Red for the node row: drop the <c>Node</c> comparison. Red for the process row: drop the <c>Process</c>
    ///     comparison.
    /// </summary>
    [Theory]
    [InlineData("Thalos:Workflow:WriteGrants:0:Node", "review")]
    [InlineData("Thalos:Workflow:WriteGrants:0:Process", "other")]
    public async Task A_grant_for_another_process_or_node_grants_nothing(string key, string value)
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture, Substitute.For<IAgentRuntime>(), settings: new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value });

        (await Authorize(host, Caller(host, await RunAt(host, "implement", Admin)), "workspace__write_file")).Allowed.Should().BeFalse();
    }

    /// <summary>Red: remove the <c>roslyn__set_active_solution</c> binding.</summary>
    [Fact]
    public async Task Set_active_solution_is_denied_to_a_workflow_caller_even_with_the_grant() =>
        (await Authorize(_host, Caller(_host, await RunAt(_host, "implement", Admin)), "roslyn__set_active_solution")).Allowed.Should().BeFalse();

    /// <summary>Red: add <c>reader</c> to <see cref="WorkspaceWritePolicy"/>.</summary>
    [Fact]
    public async Task A_scheduled_reader_may_not_write() =>
        (await Authorize(_host, await ScheduledPrincipalFromShippedConfig(_host), "workspace__write_file")).Allowed.Should().BeFalse();

    /// <summary>
    ///     A chat turn has no run. A developer's token that carries <c>thalos.run_id</c> of a real run, whose worktree
    ///     exists, must not reach it: <see cref="ClaimsSecurityContext"/> drops the claim, so the tool finds no workspace.
    ///     The authorizer is not what stops this: <c>developer</c> passes <c>workspace-write</c>, as it passes the
    ///     <c>developer</c> binding on <c>roslyn__apply_*</c>.
    /// </summary>
    [Fact]
    public async Task A_chat_turn_carrying_a_forged_run_claim_cannot_write_that_runs_worktree()
    {
        var run = await RunAt(_host, "implement", Admin);
        var chat = new ClaimsSecurityContext(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "u-chat"),
                new Claim("roles", "developer"),
                new Claim("roles", "admin"),
                new Claim(RunWorkspaceClaims.RunId, run.Id.ToString()),
                new Claim(RunWorkspaceClaims.WriteExtensions, ".cs"),
            ],
            authenticationType: "Bearer")));
        var target = Path.Combine(await WorktreeOf(_host, run), "src", "A.cs");

        var result = await Tools(_host).WriteFile(chat, "src/A.cs", "class Pwned {}", CancellationToken.None);

        // Red: remove the thalos.* strip in ClaimsSecurityContext; the forged claim then routes the write into the
        // run's worktree and this is "wrote ...".
        result.Should().Be("error: this turn has no run workspace");
        // Red: same change; the seeded file is overwritten.
        (await File.ReadAllTextAsync(target)).Should().Be("class A {}");
    }

    /// <summary>
    ///     The shipped grant allows implement <c>.cs</c> and <c>.md</c>. This host also grants <c>review</c>
    ///     <c>.props</c> and <c>.json</c>, so the host's real ceiling admits both, and only implement's own grant claim
    ///     refuses them.
    /// </summary>
    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("src/appsettings.json")]
    public async Task A_granted_run_cannot_write_an_extension_outside_its_grant(string path)
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture, Substitute.For<IAgentRuntime>(), settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Thalos:Workflow:WriteGrants:1:Process"] = "manufacture",
                ["Thalos:Workflow:WriteGrants:1:Node"] = "review",
                ["Thalos:Workflow:WriteGrants:1:AllowedExtensions:0"] = ".props",
                ["Thalos:Workflow:WriteGrants:1:AllowedExtensions:1"] = ".json",
            });
        var run = await RunAt(host, "implement", Admin);
        var caller = Caller(host, run);
        var tools = Tools(host);
        var root = await WorktreeOf(host, run);
        host.Factory.Services.GetRequiredService<RunWorkspaceToolOptions>().AllowedWriteExtensions
            .Should().Contain([".props", ".json"], "otherwise the ceiling, not the claim, refuses the write");

        var refused = await tools.WriteFile(caller, path, "<Project />", CancellationToken.None);
        var allowed = await tools.WriteFile(caller, "src/B.cs", "class B {}", CancellationToken.None);

        // Red: drop the write-extension claim from WorkflowCaller; the ceiling alone then applies and allows it.
        File.Exists(Path.Combine(root, path)).Should().BeFalse(refused);
        // Red: build the claim from an empty or wrong list, which refuses .cs as well; this proves the refusal above
        // is the extension and not a caller that can write nothing.
        File.Exists(Path.Combine(root, "src", "B.cs")).Should().BeTrue(allowed);
    }

    private static async Task<WorkflowRun> RunAt(ScratchWorkflowHost host, string node, RunPrincipal startedBy)
    {
        var starter = host.Factory.Services.GetRequiredService<IManufactureRunStarter>();
        var started = await starter.StartAsync(
            new ManufactureStartRequest("Tighten a guard.", ScratchWorkflowHost.Repository, startedBy), CancellationToken.None);
        started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error : null);
        var run = await host.Store.FindAsync(started.Value, CancellationToken.None);
        return run! with { CurrentNode = node };
    }

    private static ISecurityContext Caller(ScratchWorkflowHost host, WorkflowRun run) =>
        WorkflowNodeDispatcherFactory.CreateCallerResolver(host.Factory.Services)(run);

    private static async Task<ToolAuthorizationDecision> Authorize(ScratchWorkflowHost host, ISecurityContext caller, string tool) =>
        await host.Factory.Services.GetRequiredService<IToolAuthorizer>().AuthorizeAsync(caller, tool, PathArgs, CancellationToken.None);

    /// <summary>
    ///     The caller of the shipped <c>daily-digest</c> schedule, built the way the scheduler builds it: the principal
    ///     and roles <see cref="ScheduleReconciler"/> wrote onto the schedule row at host start, handed to the real
    ///     <see cref="SubagentRunExecutor"/>, whose request to the runner is captured.
    /// </summary>
    private static async Task<ISecurityContext> ScheduledPrincipalFromShippedConfig(ScratchWorkflowHost host)
    {
        await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(host.ConnectionString));
        var schedule = await db.ScheduledRuns.AsNoTracking().SingleAsync(s => s.Name == "daily-digest");
        schedule.Roles.Should().NotBeEmpty("otherwise the scheduled caller fails the policy for the wrong reason");

        SubagentRunRequest? captured = null;
        var runner = Substitute.For<ISubagentRunner>();
        runner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(Result<AgentTurnResult, AgentError>.Failure(AgentError.Validation("captured")));
        var executor = new SubagentRunExecutor(
            runner,
            host.Factory.Services.GetRequiredService<IAgentCatalog>(),
            host.Factory.Services.GetRequiredService<IOptions<DetachedRunOptions>>(),
            NullLogger<SubagentRunExecutor>.Instance);

        await executor.RunAsync("scout", "digest", schedule.PrincipalId, schedule.Roles, CancellationToken.None);

        captured.Should().NotBeNull();
        return captured!.Caller;
    }

    /// <summary>The host's own <c>workspace__*</c> tools, over its registered <see cref="RunWorkspaceToolOptions"/>.</summary>
    private static WorkspaceTools Tools(ScratchWorkflowHost host) => ActivatorUtilities.CreateInstance<WorkspaceTools>(host.Factory.Services);

    private static async Task<string> WorktreeOf(ScratchWorkflowHost host, WorkflowRun run)
    {
        var workspace = await host.Factory.Services.GetRequiredService<IRunWorkspaceProvider>().FindAsync(run.Id, CancellationToken.None);
        workspace.Should().NotBeNull("the starter creates the run's worktree");
        return workspace!.Root;
    }
}
