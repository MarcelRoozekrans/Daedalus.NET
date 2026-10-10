namespace Daedalus.Agents.Workflow;

/// <summary>
///     The default <see cref="IWorkflowRunCanceller"/>, registered whenever <c>Thalos:Workflow:Enabled</c> is
///     <see langword="false"/>. No run can start on such a host, so there is nothing to cancel: it reports not cancelled.
/// </summary>
public sealed class DisabledWorkflowRunCanceller : IWorkflowRunCanceller
{
    /// <inheritdoc />
    public ValueTask<bool> CancelAsync(Guid runId, string reason, CancellationToken ct) => new(false);
}
