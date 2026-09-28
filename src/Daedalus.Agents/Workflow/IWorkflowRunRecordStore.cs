using Daedalus.Domain.Entities;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The host's append-only log of what it observed during a workflow run: every workspace write the tool
///     authorizer allowed, and every review lens's evidence. See <see cref="WorkflowRunRecord"/>.
/// </summary>
/// <remarks>
///     There is deliberately no update and no delete. A record is evidence of what happened, and a store that could
///     rewrite it would make every record only as trustworthy as the least careful caller.
///     <c>WorkflowRunRecordStoreTests</c> pins this interface's method set so adding either fails a test.
/// </remarks>
public interface IWorkflowRunRecordStore
{
    /// <summary>
    ///     Appends <paramref name="record"/>. A database failure is thrown, not returned: a caller that cannot record
    ///     what it is about to allow decides for itself what that means, and the audit denies the call.
    /// </summary>
    ValueTask AppendAsync(WorkflowRunRecord record, CancellationToken ct);

    /// <summary>
    ///     Lists the records of run <paramref name="runId"/>, only those of <paramref name="kind"/> when it is not
    ///     null, ordered by <see cref="WorkflowRunRecord.Seq"/> and then by <see cref="WorkflowRunRecord.Id"/>, which
    ///     is append order among records written at the same sequence number.
    /// </summary>
    ValueTask<IReadOnlyList<WorkflowRunRecord>> ListAsync(Guid runId, string? kind, CancellationToken ct);
}
