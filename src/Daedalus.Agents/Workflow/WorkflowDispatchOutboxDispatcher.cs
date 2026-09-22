using System.Text.Json;
using Thalos.Workflow;
using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Binds <see cref="WorkflowDispatch.TypeName"/> to <see cref="WorkflowNodeDispatcher"/>. Hand-written rather
///     than generated: <see cref="WorkflowDispatchMessage"/> lives in Thalos's dependency-free core package and is
///     enqueued under a hand-chosen type name, so it carries no <c>[OutboxMessage]</c> attribute and
///     ZeroAlloc.Outbox's generated writer/dispatcher route does not apply — see <c>docs/workflow.md</c>, "The
///     outbox consumer — the piece with no default".
/// </summary>
/// <remarks>
///     Implements <see cref="IOutboxTypeDispatcher"/> (the hand-written route) rather than the generated
///     <c>IOutboxDispatcher&lt;T&gt;</c> the scheduling step dispatchers use — the two interfaces are unrelated,
///     so registering this one alongside those does not affect them. <see cref="WorkflowOutboxDispatchService"/>
///     is the only thing that resolves it; see that type's remarks for why it is not registered through
///     ZeroAlloc.Outbox's own <c>AddOutbox()</c>.
/// </remarks>
internal sealed class WorkflowDispatchOutboxDispatcher(WorkflowNodeDispatcher dispatcher) : IOutboxTypeDispatcher
{
    /// <inheritdoc />
    public string TypeName => WorkflowDispatch.TypeName;

    /// <inheritdoc />
    public async ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<WorkflowDispatchMessage>(payload.Span)
            ?? throw new InvalidOperationException("Empty workflow dispatch payload.");

        await dispatcher.DispatchAsync(message, ct).ConfigureAwait(false);
    }
}
