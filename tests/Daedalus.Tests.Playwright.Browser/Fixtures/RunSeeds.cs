using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Persistence;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Playwright.Browser.Fixtures;

/// <summary>
///     Four runs through the real store, one per state the console shows, plus the records the gate reads. A scenario
///     that changes a run starts its own through <see cref="StartAsync"/>, so the four stay as seeded whatever order the
///     scenarios run in.
/// </summary>
public sealed class RunSeeds(IWorkflowStore store, IServiceScopeFactory scopes)
{
    /// <summary>The process every seeded run belongs to, as the API's run list shows it.</summary>
    public const string Process = "manufacture";

    /// <summary>The signal the human gate awaits.</summary>
    public const string HumanApproval = "human_approval";

    /// <summary>The board task the <see cref="Running"/> run is attached to; TASK-001 and TASK-002 keep no run.</summary>
    public const string LinkedTaskId = "TASK-003";

    /// <summary>The title of <see cref="LinkedTaskId"/>, which the Tasks grid rows are found by.</summary>
    public const string LinkedTaskTitle = "Follow the console run";

    /// <summary>A node result with no outcome variables, as the Integration tests build one.</summary>
    public static NodeResult Ok => new("ok", new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>A run at <c>implement</c>, seeded last so it is the newest, and attached to <see cref="LinkedTaskId"/>.</summary>
    public Guid Running { get; private set; }

    /// <summary>A run parked at the human gate, with two deferred findings, <c>mechanism-1</c> and <c>mechanism-2</c>.</summary>
    public Guid AwaitingApproval { get; private set; }

    /// <summary>The pull request the <see cref="Succeeded"/> run opened, as its <c>pr_url</c> variable.</summary>
    public const string PullRequestUrl = "https://github.com/acme/widgets/pull/42";

    /// <summary>The display name the seeded runs record for their starter, apart from the principal id.</summary>
    public const string StarterName = "e2e.starter";

    /// <summary>The board task the <see cref="Succeeded"/> run is attached to, so the Tasks grid shows its pull request.</summary>
    public const string PullRequestTaskId = "TASK-004";

    /// <summary>The title of <see cref="PullRequestTaskId"/>, which the Tasks grid rows are found by.</summary>
    public const string PullRequestTaskTitle = "Ship the widget";

    /// <summary>The board task the <see cref="AwaitingApproval"/> run is attached to, so the Tasks grid shows a parked run.</summary>
    public const string AwaitingTaskId = "TASK-005";

    /// <summary>The title of <see cref="AwaitingTaskId"/>, which the Tasks grid rows are found by.</summary>
    public const string AwaitingTaskTitle = "Approve the widget";

    /// <summary>A task from the retired loop: stored Completed, with no run.</summary>
    public const string LegacyTaskId = "TASK-006";

    /// <summary>The title of <see cref="LegacyTaskId"/>, which the Tasks grid rows are found by.</summary>
    public const string LegacyTaskTitle = "Finished by the retired loop";

    /// <summary>The seeded project with a repository URL, which no allow-list entry matches.</summary>
    public const string RepositoryProjectName = "Widgets";

    /// <summary>The repository URL of <see cref="RepositoryProjectName"/>.</summary>
    public const string RepositoryProjectUrl = "https://github.com/acme/widgets";

    /// <summary>A run that finished, with a pull request.</summary>
    public Guid Succeeded { get; private set; }

    /// <summary>A run that failed, seeded first so it is the oldest.</summary>
    public Guid Failed { get; private set; }

    /// <summary>The store the runs were written through, for a scenario that drives a change itself.</summary>
    public IWorkflowStore Store => store;

    /// <summary>Seeds the four runs, oldest first, and attaches <see cref="Succeeded"/>, <see cref="AwaitingApproval"/> and <see cref="Running"/> to their tasks.</summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        Failed = await StartAsync("e2e-failed", ct).ConfigureAwait(false);
        await store.FailAsync(Failed, "The publish step failed.", ct).ConfigureAwait(false);

        Succeeded = await StartAsync("e2e-succeeded", ct).ConfigureAwait(false);
        await CompleteAsync(
                Succeeded, new WorkflowTransition("done", WorkflowStatus.Succeeded, null, WorkflowEventKind.Completed), ct,
                new NodeResult("ok", new Dictionary<string, object?>(StringComparer.Ordinal) { ["pr_url"] = PullRequestUrl }))
            .ConfigureAwait(false);

        await AttachToTaskAsync(Succeeded, PullRequestTaskId, ct).ConfigureAwait(false);

        AwaitingApproval = await StartAsync("e2e-gate", ct).ConfigureAwait(false);
        await CompleteAsync(AwaitingApproval, new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed), ct)
            .ConfigureAwait(false);
        var reviewSeq = await CompleteAsync(
                AwaitingApproval, new WorkflowTransition("gate", WorkflowStatus.Awaiting, HumanApproval, WorkflowEventKind.Awaiting), ct)
            .ConfigureAwait(false);
        await AppendReviewEvidenceAsync(AwaitingApproval, reviewSeq, ct).ConfigureAwait(false);
        await AttachToTaskAsync(AwaitingApproval, AwaitingTaskId, ct).ConfigureAwait(false);

        Running = await StartAsync("e2e-running", ct).ConfigureAwait(false);
        await AttachToTaskAsync(Running, LinkedTaskId, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Starts a <see cref="Process"/> run at <c>implement</c> as the E2E user. <paramref name="correlationKey"/> must
    ///     be unique for all time, as the store requires. <paramref name="startedBy"/> replaces the E2E user, for a scenario
    ///     about who started the run.
    /// </summary>
    public async Task<Guid> StartAsync(string correlationKey, CancellationToken ct, RunPrincipal? startedBy = null)
    {
        var started = await store.StartAsync(
            new WorkflowStartRequest
            {
                Process = Process,
                Version = 9,
                CorrelationKey = correlationKey,
                StartNode = "implement",
                StartedBy = startedBy ?? new RunPrincipal("e2e-test-user", ["admin"]) { DisplayName = StarterName },
            },
            ct).ConfigureAwait(false);
        if (started.IsFailure)
        {
            throw new InvalidOperationException($"Seeding run '{correlationKey}' failed: {started.Error}");
        }

        return started.Value;
    }

    /// <summary>
    ///     Completes the node <paramref name="runId"/> is at with <paramref name="result"/>, else <see cref="Ok"/>, and applies
    ///     <paramref name="transition"/>, at the run's current seq. Returns that seq.
    /// </summary>
    public async Task<long> CompleteAsync(Guid runId, WorkflowTransition transition, CancellationToken ct, NodeResult? result = null)
    {
        var run = await store.FindAsync(runId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Seeded run {runId} is not in the store.");
        await store.CompleteNodeAsync(runId, run.CurrentSeq, transition, result ?? Ok, ct).ConfigureAwait(false);

        var after = await store.FindAsync(runId, ct).ConfigureAwait(false);
        if (after is null || after.Status != transition.NextStatus || !string.Equals(after.CurrentNode, transition.NextNode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Run {runId} did not move to {transition.NextNode} at {transition.NextStatus}: it is at {after?.CurrentNode} ({after?.Status}).");
        }

        return run.CurrentSeq;
    }

    /// <summary>
    ///     One review-evidence record per lens: <c>correctness</c> approves with nothing deferred, and <c>mechanism</c>
    ///     approves and defers two findings, which the gate names <c>mechanism-1</c> and <c>mechanism-2</c>.
    /// </summary>
    private async Task AppendReviewEvidenceAsync(Guid runId, long seq, CancellationToken ct)
    {
        object[] payloads =
        [
            new { lens = "correctness", verdict = ReviewEvidence.Approved, @checked = new[] { "src/Feature.cs" }, deferred = Array.Empty<object>() },
            new
            {
                lens = "mechanism",
                verdict = ReviewEvidence.Approved,
                @checked = new[] { "src/Feature.cs" },
                deferred = new[]
                {
                    new
                    {
                        file = "src/Feature.cs", line = 12, title = "Retry loop has no cap",
                        scenario = "A flaky dependency retries forever.", reason = "Needs a decision on the cap.",
                    },
                    new
                    {
                        file = "src/Other.cs", line = 40, title = "Cache never expires",
                        scenario = "A changed setting is not picked up.", reason = "Different area.",
                    },
                },
            },
        ];

        foreach (var payload in payloads)
        {
            await AppendRecordAsync(runId, seq, "review", WorkflowRunRecord.ReviewEvidenceKind, JsonSerializer.Serialize(payload), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Appends one run record of <paramref name="kind"/> at (<paramref name="runId"/>, <paramref name="seq"/>) with
    ///     <paramref name="payloadJson"/> as it is, through the real record store, as the host writes one.
    /// </summary>
    public async Task AppendRecordAsync(Guid runId, long seq, string node, string kind, string payloadJson, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var records = scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>();
        var record = WorkflowRunRecord.Create(
            runId, seq, node, kind, $"workflow:{Process}:{runId}", "e2e-test-user", payloadJson, DateTime.UtcNow);
        if (record.IsFailure)
        {
            throw new InvalidOperationException($"Seeding a {kind} record failed: {record.Error}");
        }

        await records.AppendAsync(record.Value, ct).ConfigureAwait(false);
    }

    /// <summary>Attaches <paramref name="runId"/> to the task <paramref name="taskCode"/> through the domain method, as Manufacture does.</summary>
    private async Task AttachToTaskAsync(Guid runId, string taskCode, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var task = await db.Tasks.SingleAsync(t => t.TaskId == taskCode, ct).ConfigureAwait(false);
        var attached = task.AttachRun(runId);
        if (attached.IsFailure)
        {
            throw new InvalidOperationException($"Attaching run {runId} to {taskCode} failed: {attached.Error}");
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
