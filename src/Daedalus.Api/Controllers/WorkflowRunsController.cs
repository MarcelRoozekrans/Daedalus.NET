using Asp.Versioning;
using Daedalus.Agents.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Thalos.Workflow;

namespace Daedalus.Api.Controllers;

/// <summary>
///     The only path an operator has to unblock a run parked at an approval gate, or to give up on one. Both
///     actions require the <c>WorkflowResume</c> authorization policy (<c>Program.cs</c>: <c>developer</c> or
///     <c>admin</c> role) — the same criterion <c>Thalos:ToolPolicies</c> binds <c>git__*</c> and
///     <c>repoaction__*</c> to, enforced here by ASP.NET Core's own role-based authorization rather than
///     <c>DefaultToolAuthorizer</c>, because these are REST endpoints, not Thalos tool calls, and
///     <c>DefaultToolAuthorizer</c> never sees a request that never names a tool.
/// </summary>
/// <remarks>
///     <b>Deliberately not a Thalos tool.</b> Resuming a gate is reachable only through this controller. No agent
///     — including one running as the run's own <c>WorkflowCaller</c> — can call it, because it is never
///     registered as a local tool source and therefore never appears in any agent's resolved tool list. An agent
///     that could resume its own approval gate would make every gate in the engine decorative.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/workflow-runs")]
[Authorize(Policy = "WorkflowResume")]
[Produces("application/json")]
public sealed class WorkflowRunsController(WorkflowRunGateway runs) : ControllerBase
{
    /// <summary>
    ///     Resumes <paramref name="id"/> if — and only if — it is parked awaiting exactly
    ///     <paramref name="request"/>'s <see cref="ResumeWorkflowRunRequest.Signal"/>. A mismatched or absent
    ///     signal fails with 409 and a message naming what the run is actually awaiting; it never silently
    ///     no-ops the run's status.
    /// </summary>
    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resume(Guid id, [FromBody] ResumeWorkflowRunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Signal))
        {
            return Problem(detail: "Signal must not be blank.", statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await runs.ResumeAsync(id, request.Signal, request.Payload, ct);
        return result.IsSuccess
            ? NoContent()
            : Problem(detail: result.Error, statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>Cancels <paramref name="id"/> for <paramref name="request"/>'s reason, before it reaches a terminal node.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelWorkflowRunRequest? request, CancellationToken ct)
    {
        var run = await runs.FindAsync(id, ct);
        if (run is null)
        {
            return NotFound();
        }

        await runs.CancelAsync(id, request?.Reason ?? "Cancelled via API", ct);
        return NoContent();
    }
}

/// <summary>Request body for <see cref="WorkflowRunsController.Resume"/>.</summary>
/// <param name="Signal">The signal the caller believes the run is parked awaiting.</param>
/// <param name="Payload">Carried into <see cref="WorkflowRun.Variables"/>["payload"] by <see cref="Thalos.Workflow.IWorkflowStore.ResumeAsync"/>.</param>
public sealed record ResumeWorkflowRunRequest(string Signal, string? Payload);

/// <summary>Request body for <see cref="WorkflowRunsController.Cancel"/>.</summary>
/// <param name="Reason">A human-readable reason recorded against the run; defaults when omitted.</param>
public sealed record CancelWorkflowRunRequest(string? Reason);
