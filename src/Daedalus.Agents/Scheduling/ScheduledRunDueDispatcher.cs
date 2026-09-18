using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     Starts one execution per occurrence. Idempotency is the store's unique key, not this type's memory —
///     see <see cref="ScheduledRunExecutionStore.TryBeginAsync"/>. Supersedes the mediator-based dispatcher plan B
///     Task 5 sketched: this solution has no mediator, so there is nothing here to route through one.
/// </summary>
public sealed class ScheduledRunDueDispatcher(ScheduledRunExecutionStore store) : IOutboxDispatcher<ScheduledRunDue>
{
    /// <inheritdoc />
    public async ValueTask DispatchAsync(ScheduledRunDue message, CancellationToken ct) =>
        await store.TryBeginAsync(message, ct).ConfigureAwait(false);
}
