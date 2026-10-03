using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using Thalos.Workspaces;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B5, end to end over the real REST surface: <c>POST /api/workflow-runs/{id}/resume</c>'s
///     <c>applyStandingInstructions</c> flag, and <c>GET /api/workflow-runs/{id}</c>'s <c>StandingInstructionsDiff</c>.
///     Since task B11 the file is the run's own, in its worktree.
/// </summary>
/// <remarks>
///     <b>Seeding: a run parked directly at <c>gate</c>, not driven through the full manufacture graph.</b>
///     <c>SquadHandoffEndToEndTests</c> (task B4) proves the model's proposal reaches <c>WorkflowRun.Variables</c>
///     uncut; that plumbing is not this task's to re-prove. What B5 owns is what happens once a run is already
///     sitting at the gate — so each test here starts a small, throwaway two-node process
///     (<c>gate</c> → terminal <c>publish</c>) directly at <c>gate</c>, through
///     <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,System.Threading.CancellationToken)"/>, with the
///     proposal (or its absence) set directly as an opening variable.
///     <para>
///     <b>The run has a real worktree.</b> Each test boots a <see cref="ScratchWorkflowHost"/> over a
///     <see cref="LocalGitRemote"/>, creates the run's worktree through the host's own
///     <see cref="IRunWorkspaceProvider"/> under a fresh id, and starts the run under that same id, so the writer finds
///     the worktree the way it would for a run <c>ManufactureRunStarter</c> started. A test that pins standing
///     instructions seeds the remote's <c>AGENT.md</c> with exactly that text and pins it in the run's manifest; a
///     test that pins nothing leaves the file out, so the pinned and current text are both <c>""</c>. Nothing is
///     written under the Api project's content root any more.
///     </para>
///     <para>
///     <b>The host parks the run.</b> The run is started after the host boots, because its worktree comes from the
///     host's provider, so the host's own outbox poller dispatches <c>gate</c>; a second, standalone dispatcher would
///     race it. The poll is cut to a quarter of a second and each seed waits for <see cref="WorkflowStatus.Awaiting"/>.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class StandingInstructionsResumeEndpointTests(PostgresFixture fixture)
{
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromSeconds(30);

    private const string ProcessName = "b5-resume-test";

    private const string Signal = "human_approval";

    private const string Yaml = """
        process: b5-resume-test
        version: 1
        nodes:
          gate:
            await: human_approval
            next: publish
          publish:
            terminal: succeeded
        """;

    [Fact]
    public async Task Resuming_without_the_flag_succeeds_and_leaves_the_file_untouched()
    {
        await WithHostAsync(pinned: null, async host =>
        {
            var runId = await SeedParkedRunAsync(host, "no-flag", proposal: "Some proposal.");

            using var client = host.Client("a-developer", "developer");
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume", new { signal = Signal, payload = (string?)null });

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            File.Exists(AgentMd(host, runId)).Should().BeFalse(
                "applyStandingInstructions defaults to false, so a resume without it must never touch the file");

            var run = await host.Store.FindAsync(runId, CancellationToken.None);
            run!.CurrentNode.Should().Be("publish", "the gate's 'next' edge must still resolve when the flag is unset");

            // Falsifiable: the controller passing a constant principal instead of RunPrincipals.From(caller, ...)
            // records that constant here instead of the HTTP caller.
            run.LastResume!.By.Id.Should().Be("a-developer", "HeaderTestAuthHandler puts X-Test-User in the sub claim");
        });
    }

    [Fact]
    public async Task Cancelling_a_parked_run_leaves_the_file_untouched()
    {
        await WithHostAsync(pinned: null, async host =>
        {
            var runId = await SeedParkedRunAsync(host, "cancel", proposal: "Some proposal.");

            using var client = host.Client("a-developer", "developer");
            var response = await client.PostAsJsonAsync($"/api/workflow-runs/{runId}/cancel", new { reason = "test" });

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            File.Exists(AgentMd(host, runId)).Should().BeFalse("cancel must never write the standing-instructions file");
        });
    }

    [Fact]
    public async Task Resuming_with_the_flag_but_no_proposal_is_refused_and_the_run_stays_awaiting()
    {
        await WithHostAsync(pinned: null, async host =>
        {
            var runId = await SeedParkedRunAsync(host, "no-proposal", proposal: null);

            using var client = host.Client("a-developer", "developer");
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "retrospect reported no proposal, so there is nothing for a human resume to apply");

            var run = await host.Store.FindAsync(runId, CancellationToken.None);
            run!.Status.Should().Be(WorkflowStatus.Awaiting,
                "a refused apply must never reach the engine's own resume — the run is left exactly as found");
            File.Exists(AgentMd(host, runId)).Should().BeFalse();
        });
    }

    /// <summary>
    ///     Fix round 1, Important 1: the gate's own status/signal check runs before <c>ApplyAsync</c>, not only
    ///     afterward inside the engine's own resume — otherwise a wrong signal would still write the file and
    ///     only then be refused.
    /// </summary>
    [Fact]
    public async Task Resuming_with_the_flag_and_a_wrong_signal_is_refused_before_writing()
    {
        await WithHostAsync(pinned: null, async host =>
        {
            var runId = await SeedParkedRunAsync(host, "wrong-signal", proposal: "Some proposal.");

            using var client = host.Client("a-developer", "developer");
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = "not_the_real_signal", payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "a wrong signal must be refused before the engine ever sees it");
            File.Exists(AgentMd(host, runId)).Should().BeFalse(
                "a wrong signal must be refused before any write, not written and then refused");
        });
    }

    /// <summary>
    ///     Fix round 1, Important 1, the other half: a run that is no longer <c>Awaiting</c> at all — here,
    ///     already cancelled — must be refused the same way, before <c>ApplyAsync</c> ever runs.
    /// </summary>
    [Fact]
    public async Task Resuming_with_the_flag_against_an_already_cancelled_run_is_refused_before_writing()
    {
        await WithHostAsync(pinned: null, async host =>
        {
            var runId = await SeedParkedRunAsync(host, "already-cancelled", proposal: "Some proposal.");
            await CancelDirectlyAsync(host.ConnectionString, runId);

            using var client = host.Client("a-developer", "developer");
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "a run that is no longer awaiting must be refused before the engine ever sees it");
            File.Exists(AgentMd(host, runId)).Should().BeFalse(
                "an already-cancelled run must be refused before any write, not written and then refused");
        });
    }

    /// <summary>
    ///     Task B11: the proposal is written to the run's worktree, against the text the remote's <c>AGENT.md</c> held
    ///     and the run pinned, and the host's own content root is left exactly as it was. Red, per assertion: resolve the
    ///     path against the content root again, and the first fails, with 409, because no file there holds the pinned
    ///     text; drop the move of the temp file onto the target, and the second fails; write the proposal to the
    ///     content root's <c>AGENT.md</c> as well, and the third fails.
    /// </summary>
    [Fact]
    public async Task Resuming_with_the_flag_and_a_proposal_writes_it_to_the_worktree_and_succeeds()
    {
        const string pinned = "Run dotnet test.\n";
        await WithHostAsync(pinned, async host =>
        {
            const string proposal = "Run dotnet test.\nIntegration needs Docker.";
            var contentRootAgentMd = Path.Combine(
                host.Factory.Services.GetRequiredService<IHostEnvironment>().ContentRootPath, "AGENT.md");
            var before = Snapshot(contentRootAgentMd);
            var runId = await SeedParkedRunAsync(host, "with-proposal", proposal, pinned);

            using var client = host.Client("a-developer", "developer");
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await File.ReadAllTextAsync(AgentMd(host, runId))).Should().Be(proposal);
            Snapshot(contentRootAgentMd).Should().Be(before, "the host's own AGENT.md must be byte-identical");
        });
    }

    /// <summary>
    ///     Task B11: the staleness refusal still holds in the worktree. The run pinned the remote's text, and a human then
    ///     edits the worktree's copy, so the apply must answer 409 and keep the edit. Red, per assertion: drop the
    ///     pinned-text comparison in the writer, and the first fails with 204; write the proposal before comparing, and
    ///     the second fails, with the edit overwritten behind a 409.
    /// </summary>
    [Fact]
    public async Task Resuming_with_the_flag_after_the_worktree_file_was_edited_is_refused_with_409()
    {
        const string pinned = "Run dotnet test.\n";
        await WithHostAsync(pinned, async host =>
        {
            var runId = await SeedParkedRunAsync(host, "stale", "Run dotnet test.\nIntegration needs Docker.", pinned);
            const string humanEdit = "Run dotnet test.\nA human added this line.\n";
            await File.WriteAllTextAsync(AgentMd(host, runId), humanEdit);

            using var client = host.Client("a-developer", "developer");
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await File.ReadAllTextAsync(AgentMd(host, runId))).Should().Be(humanEdit);
        });
    }

    /// <summary>
    ///     Ruling R57: in sandbox mode the writer's handoff runs the publish-side protected-path check (S5) inside this
    ///     request, and a refused patch is a policy refusal: 422 problem details naming the refused path, with the run
    ///     left awaiting and nothing written. The host's handoff is replaced by one that refuses as Thalos's applier does,
    ///     with <see cref="AgentErrorCode.Validation"/>. Red: map <see cref="ResumeRefusal.PublishRefused"/> to 500 in the
    ///     controller, and the status fails; classify the refusal as <see cref="ResumeRefusal.WriteFailed"/> in the
    ///     writer, and it fails too. Red for the run: call the engine's resume before the write; the run then leaves
    ///     <see cref="WorkflowStatus.Awaiting"/>.
    /// </summary>
    [Fact]
    public async Task Resuming_with_the_flag_when_the_publish_side_check_refuses_the_patch_is_a_422()
    {
        var handoff = Substitute.For<IRunWorkspaceHandoff>();
        handoff.CheckoutForPublishAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
            Result<RunWorkspace, AgentError>.Failure(
                AgentError.Validation("the change touches protected path '.github/workflows/ci.yml'; publish refused"))));
        await WithHostAsync(pinned: null, async host =>
        {
            var runId = await SeedParkedRunAsync(host, "publish-refused", proposal: "Some proposal.");

            using var client = host.Client("a-developer", "developer");
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail.Should().Contain("'.github/workflows/ci.yml'");
            (await host.Store.FindAsync(runId, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Awaiting);
            File.Exists(AgentMd(host, runId)).Should().BeFalse();
        }, services =>
        {
            services.RemoveAll<IRunWorkspaceHandoff>();
            services.AddSingleton(handoff);
        });
    }

    [Fact]
    public async Task Get_on_the_parked_run_shows_the_proposed_diff()
    {
        await WithHostAsync(pinned: null, async host =>
        {
            const string proposal = "Run dotnet test.\nIntegration needs Docker.";
            var runId = await SeedParkedRunAsync(host, "diff", proposal);

            using var client = host.Client("a-developer", "developer");
            var response = await client.GetAsync($"/api/workflow-runs/{runId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<WorkflowRunView>();
            body.Should().NotBeNull();
            body!.StandingInstructionsDiff.Should().Contain("+Integration needs Docker.");
        });
    }

    /// <summary>The run's copy of the standing-instructions file, at the shipped relative <c>AGENT.md</c> in its worktree.</summary>
    private static string AgentMd(ScratchWorkflowHost host, Guid runId) => Path.Combine(host.RunsRoot, runId.ToString(), "AGENT.md");

    /// <summary>The file's bytes as Base64, or <c>absent</c> when it does not exist, for a before-and-after comparison.</summary>
    private static string Snapshot(string path) => File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : "absent";

    /// <summary>
    ///     Creates a worktree through <paramref name="host"/>'s own <see cref="IRunWorkspaceProvider"/> under a fresh id,
    ///     then starts <c>b5-resume-test</c> under that id directly at <c>gate</c>, with <paramref name="proposal"/> — or
    ///     its absence — set as <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/> and, when
    ///     <paramref name="pinned"/> is given, that text pinned as the run's standing instructions. The host's poller
    ///     dispatches <c>gate</c>, and this waits until the run is parked at <see cref="WorkflowStatus.Awaiting"/>.
    /// </summary>
    private static async Task<Guid> SeedParkedRunAsync(
        ScratchWorkflowHost host, string correlationKey, string? proposal, string? pinned = null)
    {
        var definitions = new OrmProcessDefinitionStore(new WorkflowOrmOptions { ConnectionString = host.ConnectionString });
        if (await definitions.GetActiveVersionAsync(ProcessName, CancellationToken.None) is null)
        {
            var definition = ProcessLoader.Load(Yaml);
            definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : null);
            (await definitions.UpsertAndActivateAsync(definition.Value, Yaml, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        var runId = Guid.NewGuid();
        var workspace = await host.Factory.Services.GetRequiredService<IRunWorkspaceProvider>().CreateAsync(
            new RunWorkspaceRequest(runId, ScratchWorkflowHost.Repository, host.Remote.Url, "main", $"manufacture/{runId}", null),
            CancellationToken.None);
        workspace.IsSuccess.Should().BeTrue(workspace.IsFailure ? workspace.Error.Message : null);

        IReadOnlyDictionary<string, object?>? initialVariables = proposal is null
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.ProposedStandingInstructionsKey] = proposal };

        var started = await host.Store.StartAsync(
            new WorkflowStartRequest
            {
                Process = ProcessName,
                Version = 1,
                CorrelationKey = $"{correlationKey}:{runId}",
                StartNode = "gate",
                RunId = runId,
                InitialVariables = initialVariables,
                StartedBy = TestPrincipals.Starter,
                Manifest = pinned is null
                    ? null
                    : new RunManifest
                    {
                        Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal),
                        Documents = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [ManufactureRunStarter.StandingInstructionsDocument] = pinned,
                        },
                    },
            },
            CancellationToken.None);
        started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error : null);
        started.Value.Should().Be(runId, "the run must be the one whose worktree was just created");

        var deadline = DateTime.UtcNow + ParkTimeout;
        WorkflowRun? parked;
        do
        {
            parked = await host.Store.FindAsync(runId, CancellationToken.None);
            if (parked is { Status: WorkflowStatus.Awaiting })
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        while (DateTime.UtcNow < deadline);

        parked!.Status.Should().Be(WorkflowStatus.Awaiting, "otherwise the rest of this test proves nothing");
        parked.AwaitingSignal.Should().Be(Signal);
        if (pinned is not null)
        {
            (await File.ReadAllTextAsync(AgentMd(host, runId))).Should().Be(
                pinned, "the worktree's file must hold the pinned text, or the staleness check refuses every apply");
        }

        return runId;
    }

    /// <summary>
    ///     Cancels <paramref name="runId"/> through the same standalone store <see cref="SeedParkedRunAsync"/>
    ///     seeds with, so a test can drive a run past <see cref="WorkflowStatus.Awaiting"/> before ever touching
    ///     the REST endpoint.
    /// </summary>
    private static async Task CancelDirectlyAsync(string connectionString, Guid runId)
    {
        var options = new WorkflowOrmOptions { ConnectionString = connectionString };
        var definitions = new OrmProcessDefinitionStore(options);
        var store = new OrmWorkflowStore(options, definitions);

        await store.CancelAsync(runId, "cancelled before the resume attempt", CancellationToken.None);

        var cancelled = await store.FindAsync(runId, CancellationToken.None);
        cancelled!.Status.Should().Be(WorkflowStatus.Cancelled, "otherwise the rest of this test proves nothing");
    }

    /// <summary>
    ///     Boots a <see cref="ScratchWorkflowHost"/> whose remote's <c>main</c> holds <paramref name="pinned"/> as
    ///     <c>AGENT.md</c>, or no such file when it is <see langword="null"/>, with a quarter-second outbox poll, runs
    ///     <paramref name="body"/>, and tears the host down. <paramref name="configure"/> runs after the host's own
    ///     registrations, to replace one.
    /// </summary>
    private async Task WithHostAsync(string? pinned, Func<ScratchWorkflowHost, Task> body, Action<IServiceCollection>? configure = null)
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            Substitute.For<IAgentRuntime>(),
            seed: pinned is null ? [] : [("AGENT.md", pinned)],
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
                configure?.Invoke(services);
            });

        await body(host);
    }
}
