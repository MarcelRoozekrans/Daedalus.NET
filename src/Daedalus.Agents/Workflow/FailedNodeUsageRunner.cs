using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Decorates <see cref="ISubagentRunner"/> so a workflow node whose turn failed still reaches cost analytics. Phase 2.15:
///     before it, a node stopped by its token ceiling, or failed any other way after spending, recorded no usage anywhere,
///     because only a completion writes a node-usage record and <see cref="AgentError"/> carries no usage.
/// </summary>
/// <remarks>
///     For a <see cref="WorkflowCaller"/> request it opens a <see cref="NodeTurnUsageScope"/> around the runner it wraps,
///     into which <see cref="TurnUsageNotificationPublisher"/> adds each turn's usage as Thalos' runtime publishes it. On a
///     failure with spend it calls <see cref="NodeUsageRecorder.RecordFailureAsync"/> with the run's id, current seq, node
///     and starter, the values the completion path records with, then returns the failure unchanged; the dispatcher fails
///     the run after this returns. On success it records nothing: the completion path records the node, and recording here
///     too would count it twice. Any other caller passes straight through.
///     <para>
///     It sits directly outside <see cref="ReviewLensRunner"/>, so it sees every lens pass's turn, including the passes a
///     review ran before the one that failed, which <see cref="ReviewLensRunner"/> drops when it returns the failure.
///     </para>
///     <para>
///     A runner that throws is not recorded. The dispatch is then redelivered and the node runs again at the same seq, and
///     its completion's record would be refused by the unique index on (run, seq) had this written one first.
///     </para>
/// </remarks>
internal sealed class FailedNodeUsageRunner(ISubagentRunner inner, NodeUsageRecorder usage) : ISubagentRunner
{
    /// <inheritdoc />
    public async ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Caller is not WorkflowCaller caller)
        {
            return await inner.RunAsync(request, ct).ConfigureAwait(false);
        }

        Result<AgentTurnResult, AgentError> result;
        IReadOnlyList<TurnUsage> turns;
        using (var scope = NodeTurnUsageScope.Begin())
        {
            result = await inner.RunAsync(request, ct).ConfigureAwait(false);
            turns = scope.Turns;
        }

        if (result.IsSuccess || turns.Count == 0)
        {
            return result;
        }

        var run = caller.Run;
        await usage.RecordFailureAsync(run.Id, run.CurrentSeq, run.CurrentNode, run.StartedBy?.Id, turns, ct).ConfigureAwait(false);
        return result;
    }
}
