namespace Daedalus.Agents.Workflow;

/// <summary>
///     Cancels a run as a compensating step: a run that started but could not be attached to its task (phase 2.8,
///     <c>POST /api/tasks/{id}/manufacture</c>). Never throws: it answers whether the run was cancelled, and logs why not.
/// </summary>
/// <remarks>
///     <b>Always registered.</b> Like <see cref="IManufactureRunStarter"/>, every host resolves an implementation: a host
///     with the engine off gets <see cref="DisabledWorkflowRunCanceller"/>, which cancels nothing, and
///     <c>AddDaedalusWorkflow</c> replaces it with <see cref="WorkflowRunCanceller"/> on a host with the engine on. A run
///     can only start on such a host, so a run that needs this compensation always meets the real one.
/// </remarks>
public interface IWorkflowRunCanceller
{
    /// <summary>
    ///     Cancels <paramref name="runId"/> for <paramref name="reason"/>. Returns whether it was cancelled. A no-op past a
    ///     terminal status counts as cancelled, as the store treats it.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="reason">The cancel reason the run records.</param>
    /// <param name="ct">
    ///     The caller's token. A compensating cancel deliberately does not observe it: a client that gave up during a
    ///     slow start must not leave its run running. The cancel runs under a token of its own with a short timeout.
    /// </param>
    ValueTask<bool> CancelAsync(Guid runId, string reason, CancellationToken ct);
}
