using ZeroAlloc.Results;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Mutates a scheduled run's delivery. Kept deliberately separate from the read-only
///     <see cref="IScheduleDiagnostics"/>: the diagnostics seam is also handed to agent tools
///     (<c>daedalus__list_schedules</c>, <c>daedalus__why_did_a_run_fail</c>), and this phase refused on
///     purpose to give an agent the ability to requeue or cancel a delivery. Only the web page — a human
///     clicking a button — reaches this interface.
/// </summary>
public interface IScheduleDeliveryActions
{
    /// <summary>
    ///     Requeues the dead-lettered outbox message produced by the given execution, so the ordinary outbox
    ///     poller picks it up again for redelivery.
    /// </summary>
    /// <param name="executionId">The scheduled run execution whose delivery should be retried.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Success once the message is requeued. Failure — with a reason, never an exception — if no
    ///     dead-lettered message could be found for this execution, or if the requeue itself failed.
    /// </returns>
    Task<Result> RequeueAsync(Guid executionId, CancellationToken ct);
}
