using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Controllers;

/// <summary>
///     Answers "a digest did not arrive — where did it die?" over HTTP. A thin seam onto
///     <see cref="IScheduleDiagnostics"/>: both endpoints call straight through and shape the response; every
///     classification decision belongs to the service, where it is tested.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/schedules")]
[Authorize(Policy = "AgentUse")]
[Produces("application/json")]
public sealed class SchedulesController(IScheduleDiagnostics diagnostics) : ControllerBase
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
}
