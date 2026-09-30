using System.Collections.Concurrent;
using System.Text.Json;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Stands in for the model. Each workflow turn answers the way its shipped skill instructs, through the
///     outcome tool the engine offered: implement reports <c>changed</c> with a <c>learnings</c> entry, each
///     review lens pass reports evidence and <c>approved</c>, and retrospect proposes the pinned text plus one
///     learned line. It edits no file. Publish has no arm: in v6 it is a host action, and no agent turn runs
///     there. It records each node's task text and agent. The implement turn reports <c>implementUsage</c> as its token
///     usage, the way a provider reports it, and every other turn reports none. Moved out of
///     <c>ManufactureSeamEndToEndTests</c> by task B15, whose <c>RunViewUsageTests</c> walk the same process.
/// </summary>
internal sealed class ScriptedManufactureRuntime(TurnUsage implementUsage = default) : IAgentRuntime
{
    /// <summary>The line retrospect proposes adding, and the learning implement reports.</summary>
    public const string LearnedLine = "Integration tests need Docker running.";

    private static readonly string[] Lenses = ["correctness", "falsifiability", "mechanism"];

    private readonly ConcurrentDictionary<SessionId, AgentId> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentId> _agents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, int> _lensPasses = new();

    public IReadOnlyDictionary<string, AgentId> AgentsByNode => _agents;

    public IReadOnlyCollection<string> Nodes => [.. _tasks.Keys];

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

    public ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
    {
        if (request.Caller is not WorkflowCaller caller || request.RequiredOutcome is null)
        {
            return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Failure(
                AgentError.Validation("ScriptedManufactureRuntime only answers workflow turns that require an outcome.")));
        }

        var run = caller.Run;
        var node = run.CurrentNode;
        _tasks[node] = request.Text;
        if (_sessions.TryGetValue(request.SessionId, out var agent))
        {
            _agents[node] = agent;
        }

        var tool = request.RequiredOutcome.ToolName;
        IReadOnlyList<ToolCallSummary> calls = node switch
        {
            "implement" => [Outcome(tool, "changed", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ReviewHandoff.SummaryKey] = "filtered cancelled tasks",
                [ReviewHandoff.FilesTouchedKey] = "src/Daedalus.Infrastructure/Persistence/TaskRepository.cs",
                [ReviewHandoff.RationaleKey] = "the claim query ignored status",
                [ReviewHandoff.LearningsKey] = new[] { LearnedLine },
            })],
            "review" => ReviewPass(tool, Lenses[_lensPasses.AddOrUpdate(run.Id, 0, (_, n) => n + 1) % Lenses.Length]),
            "retrospect" => [Outcome(tool, ReviewHandoff.RetrospectProposedOutcome, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ReviewHandoff.ProposedStandingInstructionsKey] = Pinned(run) + LearnedLine + "\n",
            })],
            _ => [],
        };

        // Only implement reports usage; every other turn reports none, as the seam suite scripted them before.
        var usage = string.Equals(node, "implement", StringComparison.Ordinal) ? implementUsage : default;
        return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(
            new AgentTurnResult(TurnId.New(), request.SessionId, $"{node} done", usage, calls, TimeSpan.FromMilliseconds(5))));
    }

    private static string Pinned(WorkflowRun run) =>
        run.Manifest is not null && run.Manifest.Documents.TryGetValue(ManufactureRunStarter.StandingInstructionsDocument, out var text)
            ? text
            : "";

    private static ToolCallSummary[] ReviewPass(string tool, string lens) =>
    [
        new ToolCallSummary(
            ToolCallId.New(),
            DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
            JsonSerializer.Serialize(new
            {
                lens,
                verdict = "approved",
                @checked = """["TaskRepository.ClaimNextAsync now filters cancelled rows"]""",
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
