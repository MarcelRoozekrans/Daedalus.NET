using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Appends one <see cref="WorkflowRunRecord.ReviewResponseKind"/> record for an <c>implement</c> visit that reported a
///     <c>review_response</c>. <see cref="ReviewHandoffWorkflowStore"/> calls it before the visit's completion is persisted.
/// </summary>
/// <remarks>
///     <b>A record that cannot be written fails the completion</b>, as a review lens's evidence fails its pass: the run
///     variable is overwritten by the next visit, so this record is the only lasting copy of the answer, and a run that lost
///     it would read on its view as a rejection implement never answered. The failure is returned, never thrown, with a
///     message the store fails the run with. Cancellation of the caller's token itself still propagates.
/// </remarks>
internal sealed partial class ReviewResponseRecorder(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReviewResponseRecorder> logger)
{
    /// <summary>The principal of every review-response record: the host wrote it from the node's report.</summary>
    public const string HostPrincipalId = NodeUsageRecorder.HostPrincipalId;

    /// <summary>
    ///     Appends the record at (<paramref name="runId"/>, <paramref name="seq"/>), its payload
    ///     <see cref="ReviewResponseRecord.ToPayloadJson"/> of <paramref name="response"/>. A failure carries the message the
    ///     run fails with.
    /// </summary>
    public async ValueTask<UnitResult<string>> RecordAsync(
        Guid runId, long seq, string node, string? startedById, object? response, CancellationToken ct)
    {
        var record = WorkflowRunRecord.Create(
            runId, seq, node, WorkflowRunRecord.ReviewResponseKind, HostPrincipalId, startedById,
            ReviewResponseRecord.ToPayloadJson(response), clock.GetUtcNow().UtcDateTime);
        if (record.IsFailure)
        {
            LogRejected(logger, runId, seq, record.Error);
            return UnitResult<string>.Failure(NotRecorded(node, record.Error));
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>()
                .AppendAsync(record.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogAppendFailed(logger, ex, runId, seq);
            return UnitResult<string>.Failure(NotRecorded(node, ex.Message));
        }

        return UnitResult<string>.Success();
    }

    private static string NotRecorded(string node, string reason) =>
        $"The review_response node '{node}' reported could not be recorded, so its completion is not accepted: {reason}";

    [LoggerMessage(Level = LogLevel.Error, Message = "The review-response record for run {RunId} at seq {Seq} was refused: {Error}")]
    private static partial void LogRejected(ILogger logger, Guid runId, long seq, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The review-response record for run {RunId} at seq {Seq} could not be written; the run fails")]
    private static partial void LogAppendFailed(ILogger logger, Exception exception, Guid runId, long seq);
}
