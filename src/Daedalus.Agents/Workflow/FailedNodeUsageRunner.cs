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
///     failure with spend it hands the turns to <see cref="NodeUsageRecorder.HoldFailure"/> with the run's id, current seq,
///     node and starter, the values the completion path records with, then returns the failure unchanged. The dispatcher
///     fails the run after this returns, and the recorder appends the record only once the store has committed that
///     failure: a failure that does not commit, such as a graceful stop's <c>FailAsync</c> under the cancelled dispatch
///     token, is redelivered, and a record written here first would take the seq from the rerun's own completion through
///     the unique index on (run, seq). So every dispatch first calls <see cref="NodeUsageRecorder.DropFailure"/> for its
///     seq: a seq that still holds a failure is being run again. On success it holds nothing: the completion path records
///     the node, and recording here too would count it twice. Any other caller passes straight through.
///     <para>
///     It sits directly outside <see cref="ReviewLensRunner"/>, so it sees every lens pass's turn, including the passes a
///     review ran before the one that failed, which <see cref="ReviewLensRunner"/> drops when it returns the failure.
///     </para>
///     <para>
///     A runner that throws holds nothing. The dispatch is then redelivered and the node runs again at the same seq, and
///     that run is what is recorded.
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

        var run = caller.Run;
        usage.DropFailure(run.Id, run.CurrentSeq);

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

        usage.HoldFailure(run.Id, run.CurrentSeq, run.CurrentNode, run.StartedBy?.Id, turns);
        return result;
    }
}
