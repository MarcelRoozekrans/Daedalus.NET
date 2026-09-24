using Asp.Versioning;
using Daedalus.Agents.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Thalos.Workflow;

namespace Daedalus.Api.Controllers;

/// <summary>
///     Starts, resumes, cancels and reads back manufacture runs. Every action on this controller requires the
///     <c>WorkflowResume</c> authorization policy (<c>Program.cs</c>: <c>developer</c> or <c>admin</c> role) — the
///     same criterion <c>Thalos:ToolPolicies</c> binds <c>git__*</c>, <c>repoaction__*</c> and <c>manufacture__*</c>
///     to, enforced here by ASP.NET Core's own role-based authorization rather than <c>DefaultToolAuthorizer</c>,
///     because these are REST endpoints, not Thalos tool calls, and <c>DefaultToolAuthorizer</c> never sees a
///     request that never names a tool. Starting a run over REST and starting one through <c>manufacture__start</c>
///     are deliberately gated the same way, by two different mechanisms that happen to require the same role.
/// </summary>
/// <remarks>
///     <b>Resume and cancel are deliberately not Thalos tools.</b> Resuming a gate is reachable only through this
///     controller. No agent — including one running as the run's own <c>WorkflowCaller</c> — can call it, because
///     it is never registered as a local tool source and therefore never appears in any agent's resolved tool
///     list. An agent that could resume its own approval gate would make every gate in the engine decorative. The
///     <c>WorkflowResume</c> policy above is a separate layer, for a separate caller shape: a human, or a
///     scheduled run's HTTP caller, presenting roles over a real <c>ClaimsPrincipal</c>. <c>WorkflowCaller</c> is
///     never one of those — it is a Thalos <c>ISecurityContext</c> an agent turn carries internally, never
///     something that reaches this controller's authorization pipeline at all — so this policy neither denies
///     nor could deny it; the tool-source absence above is what actually stops it.
///     <para>
///     <b>Starting is different: it is also a Thalos tool.</b> <c>manufacture__start</c>
///     (<see cref="Daedalus.Agents.Tools.DaedalusManufactureTools"/>) calls the very same
///     <see cref="Daedalus.Agents.Workflow.IManufactureRunStarter"/> this controller's <see cref="Start"/> action
///     does — starting a run is not the same hazard resuming one is, so it is deliberately reachable both ways,
///     with the tool path gated at the authorizer by <c>Thalos:ToolPolicies</c> instead of by absence.
///     </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/workflow-runs")]
[Authorize(Policy = "WorkflowResume")]
[Produces("application/json")]
public sealed class WorkflowRunsController(WorkflowRunGateway runs) : ControllerBase
{
    /// <summary>
    ///     Starts a new manufacture run for <paramref name="request"/>'s <see cref="StartWorkflowRunRequest.WorkIntent"/>.
    ///     A blank intent fails with 400 before <see cref="IManufactureRunStarter.StartAsync"/> is even called. Past
    ///     that, every failure the starter reports is a 422 <em>except</em> the specific, constant message
    ///     <see cref="Daedalus.Agents.Workflow.DisabledManufactureRunStarter.DisabledMessage"/>, which means the
    ///     workflow engine is off on this host and is reported as 503 instead — that one failure is a host
    ///     configuration fact, not something about this particular request.
    /// </summary>
    /// <param name="request">The request body.</param>
    /// <param name="starter">
    ///     Injected onto the action rather than the primary constructor — see this controller's own summary for
    ///     why <see cref="IManufactureRunStarter"/> is not one of its constructor parameters:
    ///     <c>ResumeToolBoundaryTests</c> and <c>ResumeSignalMismatchTests</c> construct this controller directly
    ///     with only a <see cref="WorkflowRunGateway"/>, and those tests are pinned to stay green unchanged.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [ProducesResponseType(typeof(StartWorkflowRunResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Start(
        [FromBody] StartWorkflowRunRequest request, [FromServices] IManufactureRunStarter starter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.WorkIntent))
        {
            return Problem(detail: "WorkIntent must not be blank.", statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await starter.StartAsync(request.WorkIntent, ct);
        if (result.IsFailure)
        {
            return string.Equals(result.Error, DisabledManufactureRunStarter.DisabledMessage, StringComparison.Ordinal)
                ? Problem(detail: result.Error, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Problem(detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return CreatedAtAction(nameof(Get), new { id = result.Value }, new StartWorkflowRunResponse(result.Value));
    }

    /// <summary>Reads back one run's current state, including its write-once manifest pins when it has one.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(WorkflowRunView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var run = await runs.FindAsync(id, ct);
        return run is null ? NotFound() : Ok(ToView(run));
    }

    private static WorkflowRunView ToView(WorkflowRun run) => new(
        run.Id,
        run.Process,
        run.ProcessVersion,
        run.Status.ToString(),
        run.CurrentNode,
        run.AwaitingSignal,
        run.LastError,
        run.Manifest?.Nodes,
        StandingInstructionsDiff: null);

    /// <summary>
    ///     Resumes <paramref name="id"/> if — and only if — it is parked awaiting exactly
    ///     <paramref name="request"/>'s <see cref="ResumeWorkflowRunRequest.Signal"/>. 404 when the run does not
    ///     exist — the same status <see cref="Cancel"/> uses for the same case, so a caller does not have to
    ///     learn two conventions for "no such run" on one controller. A mismatched or absent signal fails with
    ///     409 and a message naming what the run is actually awaiting; it never silently no-ops the run's status.
    /// </summary>
    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resume(Guid id, [FromBody] ResumeWorkflowRunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Signal))
        {
            return Problem(detail: "Signal must not be blank.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (await runs.FindAsync(id, ct) is null)
        {
            return NotFound();
        }

        var result = await runs.ResumeAsync(id, request.Signal, request.Payload, ct);
        return result.IsSuccess
            ? NoContent()
            : Problem(detail: result.Error, statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>
    ///     Cancels <paramref name="id"/> for <paramref name="request"/>'s reason, before it reaches a terminal
    ///     node. 404 when the run does not exist. <see cref="IWorkflowStore.CancelAsync"/> throws
    ///     <see cref="InvalidOperationException"/> for the same case and <see cref="WorkflowConcurrencyException"/>
    ///     when another write already changed the run between the lookup above and the write below — both are
    ///     caught here rather than left to become an unhandled 500, since a lost race is an ordinary outcome for
    ///     a run under concurrent operator action, not a server fault.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelWorkflowRunRequest? request, CancellationToken ct)
    {
        if (await runs.FindAsync(id, ct) is null)
        {
            return NotFound();
        }

        try
        {
            await runs.CancelAsync(id, request?.Reason ?? "Cancelled via API", ct);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (WorkflowConcurrencyException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

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

/// <summary>Request body for <see cref="WorkflowRunsController.Start"/>.</summary>
/// <param name="WorkIntent">What the run should manufacture, in the requester's own words. Must not be blank.</param>
public sealed record StartWorkflowRunRequest(string WorkIntent);

/// <summary>Response body for <see cref="WorkflowRunsController.Start"/>.</summary>
/// <param name="RunId">The id of the run that was just started.</param>
public sealed record StartWorkflowRunResponse(Guid RunId);

/// <summary>Response body for <see cref="WorkflowRunsController.Get"/>.</summary>
/// <param name="Id">The run's identity.</param>
/// <param name="Process">The process name this run is executing.</param>
/// <param name="ProcessVersion">The version of the process this run is executing.</param>
/// <param name="Status">The run's lifecycle status (<see cref="Thalos.Workflow.WorkflowStatus"/>, as text).</param>
/// <param name="CurrentNode">The node the run is currently positioned at.</param>
/// <param name="AwaitingSignal">The signal a parked run is waiting on, or <see langword="null"/> otherwise.</param>
/// <param name="LastError">The most recent error recorded against this run, or <see langword="null"/> if it has not failed.</param>
/// <param name="Pins">
///     The run's write-once agent/skill pins, keyed by task node name, or <see langword="null"/> for a run started
///     before manifests existed or through the legacy positional <c>StartAsync</c> overload.
/// </param>
/// <param name="StandingInstructionsDiff">
///     Always <see langword="null"/> in this task — filled in by phase 2.4 task B5, which diffs the run's pinned
///     standing instructions against the current file on disk.
/// </param>
public sealed record WorkflowRunView(
    Guid Id,
    string Process,
    int ProcessVersion,
    string Status,
    string CurrentNode,
    string? AwaitingSignal,
    string? LastError,
    IReadOnlyDictionary<string, NodePin>? Pins,
    string? StandingInstructionsDiff);
