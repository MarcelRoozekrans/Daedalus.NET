using Thalos;
using ZeroAlloc.Mediator;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Daedalus's <see cref="IAgentNotificationPublisher"/>, in place of Thalos' <c>NullAgentNotificationPublisher</c>:
///     adds the usage of each <see cref="TurnCompletedNotification"/> and <see cref="TurnFailedNotification"/> to the
///     <see cref="NodeTurnUsageScope"/> open on the publishing flow, so <see cref="FailedNodeUsageRunner"/> can record a
///     failed node's spend. Every other notification, and every turn outside a workflow node dispatch, is ignored.
/// </summary>
/// <remarks>
///     Thalos calls this inline on the turn's hot path and requires it to be non-blocking and thread-safe. It takes one
///     short lock inside <see cref="NodeTurnUsageScope.Add"/>, does no I/O and never throws.
/// </remarks>
internal sealed class TurnUsageNotificationPublisher : IAgentNotificationPublisher
{
    /// <inheritdoc />
    public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken ct)
        where TNotification : INotification
    {
        if (NodeTurnUsageScope.Current is not { } scope)
        {
            return ValueTask.CompletedTask;
        }

        switch (notification)
        {
            case TurnCompletedNotification completed:
                scope.Add(completed.Usage);
                break;
            case TurnFailedNotification failed:
                scope.Add(failed.Usage);
                break;
        }

        return ValueTask.CompletedTask;
    }
}
