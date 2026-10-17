using System.Collections.Concurrent;
using System.Text.Json;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Runtime;
using Thalos.Workflow;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Stands in for the model. Each workflow turn answers the way its shipped skill instructs, through the
///     outcome tool the engine offered: implement reports <c>changed</c> with a <c>learnings</c> entry, each
///     review lens pass reports evidence and <c>approved</c>, and retrospect proposes the pinned text plus one
///     learned line. It edits no file. Publish has no arm: in v6 it is a host action, and no agent turn runs
///     there. It records each node's task text, agent and token ceiling. The implement turn reports <c>implementUsage</c> as its token
///     usage, the way a provider reports it, and every other turn reports none. With <c>rejectReview</c> every review
///     visit rejects, its first lens pass reporting one finding and the outcome <c>rejected</c>, so the loop runs to review's cap. With
///     <c>respondToReview</c> every implement visit after the first also reports a <c>review_response</c>, a one-entry array
///     naming the visit, from <see cref="ResponseFor"/>. Moved out of
///     <c>ManufactureSeamEndToEndTests</c> by task B15, whose <c>RunViewUsageTests</c> walk the same process.
///     <para>
///     Phase 2.15: with <c>failNode</c>, every turn of that node fails with <see cref="BudgetStop"/> after spending
///     <c>failUsage</c>, as a turn its token ceiling stopped does. Like Thalos' <c>ThalosAgentRuntime</c>, which this
///     replaces, it publishes each turn's <see cref="TurnCompletedNotification"/> or <see cref="TurnFailedNotification"/>,
///     with the turn's usage, through <see cref="Notifications"/> before it returns the turn's result.
///     <see cref="ApiWebApplicationFactory"/> points that at the host's own <see cref="IAgentNotificationPublisher"/>.
///     With <c>failLensPass</c> set to n, only the nth review lens pass of a run fails that way, after spending
///     <c>failUsage</c>, and each review lens pass before it reports <c>reviewUsage</c>.
///     </para>
/// </summary>
internal sealed class ScriptedManufactureRuntime(
    TurnUsage implementUsage = default, bool rejectReview = false, bool respondToReview = false, string? failNode = null,
    TurnUsage failUsage = default, TurnUsage reviewUsage = default, int failLensPass = 0)
    : IAgentRuntime
{
    /// <summary>The error a <c>failNode</c> turn fails with: its token ceiling stopped it.</summary>
    public static readonly AgentError BudgetStop = AgentError.SubagentBudgetExceeded(150_000, 4, 155_138);

    /// <summary>The line retrospect proposes adding, and the learning implement reports.</summary>
    public const string LearnedLine = "Integration tests need Docker running.";

    /// <summary>The evidence every review lens pass reports as checked: the review text a telemetry test must not find.</summary>
    public const string ReviewEvidence = "TaskRepository.ClaimNextAsync now filters cancelled rows";

    /// <summary>The lens that rejects when the runtime is built with <c>rejectReview</c>.</summary>
    public const string RejectingLens = "correctness";

    private const string RejectionFindings =
        """[{"file":"src/Daedalus.Infrastructure/Persistence/TaskRepository.cs","line":42,"scenario":"cancelled rows are still claimed"}]""";

    private static readonly string[] Lenses = ["correctness", "falsifiability", "mechanism"];

    private readonly ConcurrentDictionary<SessionId, AgentId> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentId> _agents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int?> _maxTotalTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, int> _lensPasses = new();
    private readonly ConcurrentDictionary<Guid, int> _implementVisits = new();
    private readonly ConcurrentDictionary<Guid, int> _reviewTurns = new();

    /// <summary>The one <c>review_response</c> entry implement reports on its <paramref name="visit"/>th visit, from the second.</summary>
    public static string ResponseFor(int visit) =>
        $"addressed: src/Daedalus.Infrastructure/Persistence/TaskRepository.cs:42 - implement visit {visit} filters cancelled rows";

    public IReadOnlyDictionary<string, AgentId> AgentsByNode => _agents;

    public IReadOnlyCollection<string> Nodes => [.. _tasks.Keys];

    /// <summary>
    ///     Phase 2.15: the token ceiling each node's latest turn carried, as Thalos' <c>SubagentRunner</c> forwards the
    ///     stamped budget in <see cref="AgentTurnRequest.MaxTotalTokens"/>.
    /// </summary>
    public IReadOnlyDictionary<string, int?> MaxTotalTokensByNode => _maxTotalTokens;

    /// <summary>Where each turn's notification is published: the host's own publisher once the host resolved this runtime.</summary>
    public IAgentNotificationPublisher Notifications { get; set; } = NullAgentNotificationPublisher.Instance;

    public string TaskFor(string node) => _tasks.TryGetValue(node, out var task) ? task : "";

    public ValueTask<Result<SessionId, AgentError>> CreateSessionAsync(AgentId agentId, ISecurityContext caller, CancellationToken ct = default)
    {
        var session = new SessionId(Guid.NewGuid());
        _sessions[session] = agentId;
        return ValueTask.FromResult(Result<SessionId, AgentError>.Success(session));
    }

    public ValueTask<UnitResult<AgentError>> CloseSessionAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct = default)
    {
        _sessions.TryRemove(sessionId, out _);
        return ValueTask.FromResult(UnitResult<AgentError>.Success());
    }

    public IAsyncEnumerable<AgentEvent> RunTurnStreamingAsync(AgentTurnRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("The workflow path runs buffered turns only.");

    public async ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
    {
        if (request.Caller is not WorkflowCaller caller || request.RequiredOutcome is null)
        {
            return Result<AgentTurnResult, AgentError>.Failure(
                AgentError.Validation("ScriptedManufactureRuntime only answers workflow turns that require an outcome."));
        }

        var run = caller.Run;
        var node = run.CurrentNode;
        _tasks[node] = request.Text;
        _maxTotalTokens[node] = request.MaxTotalTokens;
        if (_sessions.TryGetValue(request.SessionId, out var agent))
        {
            _agents[node] = agent;
        }

        var turnId = TurnId.New();
        var isReview = string.Equals(node, "review", StringComparison.Ordinal);
        var failsHere = string.Equals(node, failNode, StringComparison.Ordinal)
            || (isReview && failLensPass > 0 && _reviewTurns.AddOrUpdate(run.Id, 1, (_, n) => n + 1) == failLensPass);
        if (failsHere)
        {
            await Notifications.PublishAsync(
                new TurnFailedNotification(request.SessionId, turnId, BudgetStop, DateTimeOffset.UtcNow, failUsage), CancellationToken.None);
            return Result<AgentTurnResult, AgentError>.Failure(BudgetStop);
        }

        var tool = request.RequiredOutcome.ToolName;
        IReadOnlyList<ToolCallSummary> calls = node switch
        {
            "implement" => [Outcome(tool, "changed", ImplementVariables(run.Id))],
            "review" when rejectReview => RejectPass(tool),
            "review" => ReviewPass(tool, Lenses[_lensPasses.AddOrUpdate(run.Id, 0, (_, n) => n + 1) % Lenses.Length]),
            "retrospect" => [Outcome(tool, ReviewHandoff.RetrospectProposedOutcome, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ReviewHandoff.ProposedStandingInstructionsKey] = Pinned(run) + LearnedLine + "\n",
            })],
            _ => [],
        };

        // Only implement and a review lens pass report usage; every other turn reports none, as the seam suite scripted them
        // before. reviewUsage is none unless a test sets it.
        var usage = node switch
        {
            "implement" => implementUsage,
            "review" => reviewUsage,
            _ => default,
        };
        var elapsed = TimeSpan.FromMilliseconds(5);
        await Notifications.PublishAsync(
            new TurnCompletedNotification(request.SessionId, turnId, usage, elapsed, DateTimeOffset.UtcNow), CancellationToken.None);
        return Result<AgentTurnResult, AgentError>.Success(new AgentTurnResult(turnId, request.SessionId, $"{node} done", usage, calls, elapsed));
    }

    private Dictionary<string, object?> ImplementVariables(Guid runId)
    {
        var visit = _implementVisits.AddOrUpdate(runId, 1, (_, n) => n + 1);
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ReviewHandoff.SummaryKey] = "filtered cancelled tasks",
            [ReviewHandoff.FilesTouchedKey] = "src/Daedalus.Infrastructure/Persistence/TaskRepository.cs",
            [ReviewHandoff.RationaleKey] = "the claim query ignored status",
            [ReviewHandoff.LearningsKey] = new[] { LearnedLine },
        };
        if (respondToReview && visit > 1)
        {
            variables[ReviewHandoff.ReviewResponseKey] = new[] { ResponseFor(visit) };
        }

        return variables;
    }

    private static string Pinned(WorkflowRun run) =>
        run.Manifest is not null && run.Manifest.Documents.TryGetValue(ManufactureRunStarter.StandingInstructionsDocument, out var text)
            ? text
            : "";

    private static ToolCallSummary[] RejectPass(string tool) =>
    [
        new ToolCallSummary(
            ToolCallId.New(),
            DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
            JsonSerializer.Serialize(new { lens = RejectingLens, verdict = "rejected", findings = RejectionFindings }),
            Succeeded: true, "Recorded", TimeSpan.FromMilliseconds(1)),
        Outcome(tool, "rejected", null),
    ];

    private static ToolCallSummary[] ReviewPass(string tool, string lens) =>
    [
        new ToolCallSummary(
            ToolCallId.New(),
            DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
            JsonSerializer.Serialize(new
            {
                lens,
                verdict = "approved",
                @checked = JsonSerializer.Serialize(new[] { ReviewEvidence }),
            }),
            Succeeded: true, "Recorded", TimeSpan.FromMilliseconds(1)),
        Outcome(tool, "approved", null),
    ];

    private static ToolCallSummary Outcome(string tool, string outcome, Dictionary<string, object?>? variables) =>
        new(
            ToolCallId.New(),
            tool,
            variables is null
                ? JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal) { [OutcomeToolSchema.ArgumentName] = outcome })
                : JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [OutcomeToolSchema.ArgumentName] = outcome,
                    ["variables"] = variables,
                }),
            Succeeded: true, "ok", TimeSpan.FromMilliseconds(1));
}
