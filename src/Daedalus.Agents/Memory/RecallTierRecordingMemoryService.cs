using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Memory;
using Thalos.Runtime;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Memory;

/// <summary>
///     Wraps <see cref="IMemoryService"/> so that every recall made by a workflow-run turn leaves the
///     <see cref="MemoryRecallTier"/> that answered it in <see cref="WorkflowRecallTierLog"/>, where that run's
///     next transition picks it up and writes it into the run record.
/// </summary>
/// <remarks>
///     <b>Decorating the service, not one of its callers, is what makes this complete.</b> A turn can recall
///     two ways — <c>MemoryContextProvider</c>'s auto-recall before the first model call, and an explicit
///     <c>memory__recall</c> tool call — and Thalos resolves both from the same DI-registered
///     <see cref="IMemoryService"/>. Wrapping either caller alone would record some turns and silently miss
///     others, which is the same "you cannot tell silence from knowledge" defect design section 5.4 is about,
///     moved one level up.
///     <para>
///     <b>Nothing but the recording is added.</b> Every member forwards, including
///     <see cref="RecallAsync"/>'s own result, unchanged — a failed recall records nothing and is passed
///     through as the failure it is, because a recall that did not happen has no tier to claim.
///     </para>
///     <para>
///     <b>How a recall is attributed to a run.</b> <see cref="TurnScope.Current"/>'s caller is the
///     <c>ISecurityContext</c> the turn executes as, and <see cref="WorkflowCaller"/> is the only one
///     that carries a <c>WorkflowRun</c>. A recall from a chat turn, a scheduled run or outside a turn
///     altogether matches nothing here and is left alone.
///     </para>
///     <para>
///     <b>The <c>currentCaller</c> parameter is a seam, and what it does and does not cover is worth being
///     exact about.</b> <c>TurnScope.Begin</c> is internal to Thalos, so no test outside that assembly can
///     establish a turn scope, and without a seam the link from "a recall happened" to "the tier was recorded"
///     would have no executable test at all — which is a worse trade than one parameter whose default is the
///     production expression. What the seam therefore leaves unverified is exactly the default lambda below:
///     that <see cref="TurnScope.Current"/> is the ambient source production reads. Everything downstream of
///     it — the <see cref="WorkflowCaller"/> match, the run attribution, the tier value — is covered.
///     </para>
/// </remarks>
internal sealed class RecallTierRecordingMemoryService(
    IMemoryService inner,
    WorkflowRecallTierLog tiers,
    Func<ISecurityContext?>? currentCaller = null) : IMemoryService
{
    private readonly IMemoryService _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly WorkflowRecallTierLog _tiers = tiers ?? throw new ArgumentNullException(nameof(tiers));
    private readonly Func<ISecurityContext?> _currentCaller = currentCaller ?? (static () => TurnScope.Current?.Caller);

    /// <inheritdoc />
    public ValueTask<Result<MemoryRecord, AgentError>> RememberAsync(RememberRequest request, CancellationToken ct) =>
        _inner.RememberAsync(request, ct);

    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecallResult, AgentError>> RecallAsync(
        string query, MemoryScope scope, RecallOptions options, CancellationToken ct)
    {
        var result = await _inner.RecallAsync(query, scope, options, ct).ConfigureAwait(false);

        if (result.IsSuccess && _currentCaller() is WorkflowCaller caller)
        {
            _tiers.Record(caller.Run.Id, result.Value.Tier);
        }

        return result;
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> ForgetAsync(MemoryId id, MemoryScope scope, bool hard, CancellationToken ct) =>
        _inner.ForgetAsync(id, scope, hard, ct);

    /// <inheritdoc />
    public ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct) =>
        _inner.ListAsync(query, ct);

    /// <inheritdoc />
    public ValueTask<Result<ReindexReport, AgentError>> ReindexAsync(ReindexOptions options, CancellationToken ct) =>
        _inner.ReindexAsync(options, ct);
}
