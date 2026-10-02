using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     End-to-end coverage of <c>POST</c>/<c>GET /api/workflow-runs</c> against a real, migrated Postgres database
///     with the workflow engine <b>enabled</b>, over <see cref="ScratchWorkflowHost"/>: one throwaway database, one
///     <see cref="LocalGitRemote"/> allow-listed as <see cref="ScratchWorkflowHost.Repository"/> and one temp data root
///     per test, so a test that deactivates a skill or creates a worktree can never leak into another.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StartRunEndpointTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Posting_a_blank_work_intent_returns_bad_request()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        using var client = host.Client("a-developer", "developer");

        var response = await client.PostAsJsonAsync(
            "/api/workflow-runs", new { workIntent = "   ", repository = ScratchWorkflowHost.Repository });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    ///     Red: drop the controller's blank-repository check, and the blank name reaches the starter, which answers
    ///     422 because no repository is named <c>"   "</c>.
    /// </summary>
    [Fact]
    public async Task Posting_a_blank_repository_returns_bad_request()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        using var client = host.Client("a-developer", "developer");

        var response = await client.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "anything", repository = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    ///     Controller ruling R5: the pinned nodes are read off the real, loaded, active process definition — never
    ///     a hardcoded <c>["implement", "review", "retrospect"]</c> list — so this stays correct as the process
    ///     gains or loses task nodes, with no edit to this test.
    /// </summary>
    [Fact]
    public async Task A_developer_starting_a_run_gets_every_task_node_of_the_active_process_pinned()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        var definitions = host.Factory.Services.GetRequiredService<IProcessDefinitionStore>();
        var version = await definitions.GetActiveVersionAsync("manufacture", CancellationToken.None);
        version.Should().NotBeNull("otherwise this test proves nothing about the real process");

        var definition = await definitions.GetAsync("manufacture", version!.Value, CancellationToken.None);
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : null);

        var expectedTaskNodes = definition.Value.Nodes
            .Where(n => n.Value.Agent is not null)
            .Select(n => n.Key)
            .ToList();
        expectedTaskNodes.Should().NotBeEmpty("otherwise this test passes vacuously");

        const string intent = "add a health check endpoint";
        var runId = await StartAsync(host, intent);

        var run = await host.Store.FindAsync(runId, CancellationToken.None);
        run.Should().NotBeNull("the row must exist once the endpoint has reported 201");
        run!.Variables["work_intent"].Should().Be(intent);
        run.Manifest.Should().NotBeNull();
        run.Manifest!.Nodes.Keys.Should().BeEquivalentTo(expectedTaskNodes,
            "every task node of the active process must be pinned, derived from the loaded definition");
    }

    /// <summary>
    ///     The run records the HTTP caller as its starter. Falsifiable per assertion: passing a constant
    ///     <c>new RunPrincipal("manufacture-endpoint", [])</c> from the controller fails the id assertion, and
    ///     dropping the roles, <c>new RunPrincipal(caller.Id, [])</c>, fails the roles assertion.
    /// </summary>
    [Fact]
    public async Task Post_records_the_callers_subject_and_roles_as_the_starter()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        using var client = host.Client("a-developer", "developer");

        var response = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("Tighten a guard.", ScratchWorkflowHost.Repository));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        var run = await host.Store.FindAsync(runId, CancellationToken.None);
        run!.StartedBy!.Id.Should().Be("a-developer", "HeaderTestAuthHandler puts X-Test-User in the sub claim");
        run.StartedBy.Roles.Should().Equal("developer");
    }

    /// <summary>
    ///     The run gets a worktree of the allow-listed remote on its own branch, and pins the worktree's
    ///     <c>AGENT.md</c> and the intent. Red, per assertion: name the branch anything else, and the first fails; pin
    ///     from the host content root, whose <c>AGENT.md</c> is not the seeded text, and the second fails; leave the
    ///     intent out of the documents, and the last fails.
    /// </summary>
    [Fact]
    public async Task Start_creates_a_worktree_on_the_run_branch_and_pins_its_AGENT_md_and_the_intent()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        var runId = await StartAsync(host, "Tighten a guard.");

        var root = Path.Combine(host.RunsRoot, runId.ToString());
        LocalGitRemote.Git(root, "rev-parse --abbrev-ref HEAD").Should().Be($"manufacture/{runId}");
        var run = await host.Store.FindAsync(runId, CancellationToken.None);
        run!.Manifest!.Documents.Should().ContainKey(ManufactureRunStarter.StandingInstructionsDocument)
            .WhoseValue.Should().Be("Run dotnet test.\n");
        run.Manifest.Documents.Should().ContainKey(ManufactureRunStarter.WorkIntentDocument)
            .WhoseValue.Should().Be("Tighten a guard.");
    }

    /// <summary>
    ///     Ruling R16. A name that is not allow-listed is the caller's mistake, so 400. Red, per assertion: resolve an
    ///     unknown name to <c>config.Repositories[0]</c> instead of failing, and the start succeeds, so the status
    ///     assertion fails; map <c>Invalid</c> to 422 or 500, and it fails too. With the ones before it commented out, a run row appears and
    ///     fails the count, and a worktree directory appears and fails the last.
    /// </summary>
    [Fact]
    public async Task An_unlisted_repository_is_400_and_leaves_no_run_and_no_worktree()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        using var client = host.Client("a-developer", "developer");
        var runsBefore = await CountWorkflowRunsAsync(host.ConnectionString);
        var directoriesBefore = host.RunDirectories();

        var response = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("x", "not-listed"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountWorkflowRunsAsync(host.ConnectionString)).Should().Be(runsBefore);
        host.RunDirectories().Should().BeEquivalentTo(directoriesBefore, "compared before and after, so no other test's run can decide it");
    }

    /// <summary>
    ///     A pin failure is a fixed refusal of the run starter, the 2.4 contract: 422 problem details that name the node.
    ///     Red, per assertion: map the run starter's failure to <c>Failed</c>, and the status fails; drop the message from the
    ///     problem, and the detail fails; write the row before the pin, and the count fails.
    /// </summary>
    [Fact]
    public async Task A_deactivated_node_skill_fails_the_start_and_writes_no_row()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        await DeactivateSkillAsync(host.ConnectionString, "manufacture-implement");
        var before = await CountWorkflowRunsAsync(host.ConnectionString);
        using var client = host.Client("a-developer", "developer");

        var response = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("anything", ScratchWorkflowHost.Repository));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Detail.Should().Contain("implement", "the failure must name the node whose skill is not active");

        var after = await CountWorkflowRunsAsync(host.ConnectionString);
        after.Should().Be(before, "a pin failure must not create a run row");
    }

    /// <summary>
    ///     Red, per assertion: drop the <c>RemoveAsync</c> after a failed start, and the worktree made before the pin
    ///     failed stays; map the run starter's failure to <c>Failed</c>, and the status fails.
    /// </summary>
    [Fact]
    public async Task A_pin_failure_removes_the_worktree_it_created()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        await DeactivateSkillAsync(host.ConnectionString, "manufacture-implement");
        using var client = host.Client("a-developer", "developer");
        var directoriesBefore = host.RunDirectories();

        var response = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("x", ScratchWorkflowHost.Repository));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        host.RunDirectories().Should().BeEquivalentTo(directoriesBefore, "the worktree made before the pin failed is removed");
    }

    /// <summary>
    ///     Task B4: a sandbox runtime that is down is 503 with <c>Retry-After: 30</c> and problem details, not a 500, and
    ///     it leaves no run row. The host's provider is overridden with a substitute whose create fails the way the
    ///     sandbox provider's does when the engine is unreachable, a provider error. The substitute having received the
    ///     create shows the starter resolved the override and not the host's git provider. Red, per assertion: map
    ///     <c>Unavailable</c> to 400, and the status fails; leave the header out, and the header fails; answer without
    ///     problem details, and the detail fails; resolve the host's own provider in the starter, and the substitute
    ///     receives nothing and the start succeeds with 201; start the run before the create, and the count fails.
    /// </summary>
    [Fact]
    public async Task An_unavailable_sandbox_is_a_503_with_retry_after()
    {
        var provider = Substitute.For<IRunWorkspaceProvider, IRunBaseFileReader>();
        provider.CreateAsync(Arg.Any<RunWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
                Result<RunWorkspace, AgentError>.Failure(AgentError.ProviderError("the container engine is not reachable."))));
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture, Substitute.For<IAgentRuntime>(), configureServices: WorkspaceProviderOverrides.Replace(provider));
        using var client = host.Client("a-developer", "developer");
        var runsBefore = await CountWorkflowRunsAsync(host.ConnectionString);

        var response = await client.PostAsJsonAsync(
            "/api/workflow-runs", new StartWorkflowRunRequest("Tighten a guard.", ScratchWorkflowHost.Repository));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)HttpStatusCode.ServiceUnavailable);
        problem.Detail.Should().Contain("the container engine is not reachable.");
        await provider.ReceivedWithAnyArgs(1).CreateAsync(default!, default);
        (await CountWorkflowRunsAsync(host.ConnectionString)).Should().Be(runsBefore);
    }

    /// <summary>
    ///     Task B4, local mode, the 2.5 link-escape guarantee through the real git provider's reader: a worktree that holds
    ///     a directory link out of itself, named by the standing-instructions path, fails the start instead of pinning the
    ///     text of the host file the link leads to, and the worktree is removed. The link is made after the provider
    ///     creates the worktree, by a wrapper around the host's own provider. A refusal of the path is the caller's, so
    ///     400. Red, per assertion: read the file without resolving the path through <c>WorkspacePath.Resolve</c> in the
    ///     provider's reader, and the start succeeds with 201, so the status fails; pin from the host, and the problem
    ///     detail fails with it; drop the removal after the refusal, and the workspace list assertion fails; leave no link,
    ///     and the start succeeds, so the status fails.
    /// </summary>
    [Fact]
    public async Task A_link_that_leads_out_of_the_worktree_fails_the_start_and_removes_the_worktree()
    {
        var outside = Directory.CreateTempSubdirectory("daedalus-link-target-");
        await File.WriteAllTextAsync(Path.Combine(outside.FullName, "AGENT.md"), "A host file.");
        var links = new List<IDisposable>();
        ScratchWorkflowHost? host = null;
        try
        {
            host = await ScratchWorkflowHost.StartAsync(
                fixture,
                Substitute.For<IAgentRuntime>(),
                settings: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Thalos:Workflow:StandingInstructionsPath"] = "docs/AGENT.md" },
                configureServices: WorkspaceProviderOverrides.LinkOutOfEveryWorkspace("docs", outside.FullName, links));
            using var client = host.Client("a-developer", "developer");
            var runsBefore = await CountWorkflowRunsAsync(host.ConnectionString);

            var response = await client.PostAsJsonAsync(
                "/api/workflow-runs", new StartWorkflowRunRequest("Tighten a guard.", ScratchWorkflowHost.Repository));

            links.Should().HaveCount(1, "the wrapper must have linked the worktree this start created, or the test proves nothing");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            problem!.Detail.Should().Contain("the standing instructions 'docs/AGENT.md' cannot be read");
            (await CountWorkflowRunsAsync(host.ConnectionString)).Should().Be(runsBefore);

            // Asked of the provider, not of the disk: git for Windows removes the worktree but leaves the directory
            // that holds the junction, so the directory outlives a removal that did happen. Thalos issue #252 tracks it.
            var workspaces = await host.Factory.Services.GetRequiredService<IRunWorkspaceProvider>().ListAsync(CancellationToken.None);
            workspaces.Should().BeEmpty("the worktree made before the read was refused is removed");
        }
        finally
        {
            // The links go first: deleting a directory that holds a junction is refused on Windows.
            foreach (var link in links)
            {
                link.Dispose();
            }

            if (host is not null)
            {
                await host.DisposeAsync();
            }

            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Starting_a_run_without_authentication_returns_unauthorized()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        using var client = host.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("anything", ScratchWorkflowHost.Repository));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_non_developer_cannot_start_a_run()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        using var client = host.Client("a-reader", "reader");

        var response = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("anything", ScratchWorkflowHost.Repository));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Getting_an_unknown_run_returns_not_found()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        using var client = host.Client("a-developer", "developer");

        var response = await client.GetAsync($"/api/workflow-runs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Posts <paramref name="intent"/> on <see cref="ScratchWorkflowHost.Repository"/> as <c>a-developer</c>, asserts 201 and returns the run id.</summary>
    private static async Task<Guid> StartAsync(ScratchWorkflowHost host, string intent)
    {
        using var client = host.Client("a-developer", "developer");
        var response = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest(intent, ScratchWorkflowHost.Repository));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
    }

    /// <summary>
    ///     Flips the seeded <c>manufacture-implement</c> skill's <c>IsActive</c> to <see langword="false"/>
    ///     directly against the store, the same table <c>PostgresSkillStore</c> writes — the sync that seeded it
    ///     at host start ran once, at boot, over the real <c>skills/manufacture-implement/SKILL.md</c> file; this
    ///     simulates the file having since been retired without a second sync.
    /// </summary>
    private static async Task DeactivateSkillAsync(string connectionString, string skillName)
    {
        await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
        var skill = await db.Skills.SingleAsync(s => s.Id == skillName);
        skill.Update(skill.Description, skill.Body, skill.Tags, skill.SourcePath, skill.ContentHash, isActive: false, skill.UpdatedAt);
        await db.SaveChangesAsync();
    }

    private static async Task<long> CountWorkflowRunsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM workflow_run", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
