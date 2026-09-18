using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Controllers;

#pragma warning disable S6960 // SonarQube complexity: reads and the one write they enable belong on one
                              // resource per the reviewer's ruling, not split into two controllers.

/// <summary>
///     Answers "a digest did not arrive — where did it die?" over HTTP, and offers the one remedy the page
///     provides: resending an undelivered run's message. The two read routes are a thin seam onto
///     <see cref="IScheduleDiagnostics"/>: they call straight through and shape the response; every
///     classification decision belongs to the service, where it is tested. The resend route is the same shape
///     onto <see cref="IScheduleDeliveryActions"/> — the lookup from execution id to outbox row is service work,
///     not controller work.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/schedules")]
[Authorize(Policy = "AgentUse")]
[Produces("application/json")]
public sealed class SchedulesController(IScheduleDiagnostics diagnostics, IScheduleDeliveryActions deliveryActions) : ControllerBase
{
    /// <summary>Every active schedule's current state and the verdict on its most recent run.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RunDiagnosis>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverview(CancellationToken ct) =>
        Ok(await diagnostics.GetOverviewAsync(ct));

    /// <summary>The most recent executions of one schedule, newest first.</summary>
    [HttpGet("{scheduleId:guid}/runs")]
    [ProducesResponseType(typeof(IReadOnlyList<RunDiagnosis>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRunHistory(Guid scheduleId, [FromQuery] int take = 20, CancellationToken ct = default) =>
        Ok(await diagnostics.GetRunHistoryAsync(scheduleId, take, ct));

    /// <summary>
    ///     Requeues an undelivered run's dead-lettered message for redelivery. No confirmation step: requeueing
    ///     risks a duplicate digest, not data loss. Never rewrites a verdict — the caller re-resolves it from the
    ///     next <see cref="GetOverview"/> or <see cref="GetRunHistory"/> call, so the page never claims a
    ///     delivery it has not observed. <c>scheduleId</c> identifies the resource in the URL but is not needed
    ///     to resolve the outbox row, so it is not bound to a parameter here.
    /// </summary>
    [HttpPost("{scheduleId:guid}/runs/{executionId:guid}/resend")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Resend(Guid executionId, CancellationToken ct)
    {
        var result = await deliveryActions.RequeueAsync(executionId, ct);
        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }
}
