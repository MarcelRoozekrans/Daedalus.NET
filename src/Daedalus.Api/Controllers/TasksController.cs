using System.Threading.RateLimiting;
using Daedalus.Agents.Security;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Agents;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.AbandonTask;
using Daedalus.Application.Commands.CreateTask;
using Daedalus.Application.Commands.DeleteTask;
using Daedalus.Application.Commands.ResumeTask;
using Daedalus.Application.Commands.UpdateTask;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Daedalus.Api.Controllers;

/// <summary>API endpoints for accessing task data.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public sealed partial class TasksController(
    ITaskQueryService taskService,
    IApplicationCommands commands,
    ILogger<TasksController> logger) : ControllerBase
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Error retrieving tasks")]
    private static partial void LogErrorRetrievingTasks(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Error retrieving task {TaskId}")]
    private static partial void LogErrorRetrievingTask(ILogger logger, Guid taskId, Exception ex);

    /// <summary>Get all tasks with pagination.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<TaskDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllTasks([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        try
        {
            var result = await taskService.GetAllAsync(page, pageSize, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingTasks(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get a specific task by ID.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTaskById(Guid id, CancellationToken ct = default)
    {
        try
        {
            var task = await taskService.GetByIdAsync(id, ct);
            if (task is null)
            {
                return NotFound(new { error = $"Task with ID {id} not found" });
            }

            return Ok(task);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingTask(logger, id, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Create a new task.</summary>
    [Authorize(Policy = "TaskManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpPost]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateTask([FromBody] CreateTaskDto dto, CancellationToken ct = default)
    {
        var command = new CreateTaskCommand(
            dto.ProjectId,
            dto.TaskId ?? $"TASK-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            dto.Title,
            dto.Description,
            (Priority)dto.Priority,
            dto.Phase ?? "Phase-1",
            dto.ParallelGroup,
            (Complexity)dto.EstimatedComplexity,
            dto.Prompt);

        var result = await commands.CreateTaskAsync(command, ct);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetTaskById), new { id = result.Value.Id }, result.Value)
            : BadRequest(new { error = result.Error });
    }

    /// <summary>Update a task's metadata.</summary>
    [Authorize(Policy = "TaskManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTask(Guid id, [FromBody] UpdateTaskDto dto, CancellationToken ct = default)
    {
        var command = new UpdateTaskCommand(
            id,
            dto.Title,
            dto.Description,
            dto.Priority.HasValue ? (Priority)dto.Priority.Value : null,
            dto.Phase,
            dto.ParallelGroup,
            dto.EstimatedComplexity.HasValue ? (Complexity)dto.EstimatedComplexity.Value : null,
            dto.Prompt);

        var result = await commands.UpdateTaskAsync(command, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        if (TaskRunGuard.IsConflict(result.Error))
        {
            return Conflict(new { error = result.Error });
        }

        return result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? NotFound(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>Delete a task.</summary>
    [Authorize(Policy = "TaskManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTask(Guid id, CancellationToken ct = default)
    {
        var command = new DeleteTaskCommand(id);
        var result = await commands.DeleteTaskAsync(command, ct);

        if (result.IsSuccess)
        {
            return NoContent();
        }

        if (TaskRunGuard.IsConflict(result.Error))
        {
            return Conflict(new { error = result.Error });
        }

        return result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? NotFound(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>Abandon a task.</summary>
    [Authorize(Policy = "TaskManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpPost("{id:guid}/abandon")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AbandonTask(Guid id, [FromBody] AbandonTaskDto dto, CancellationToken ct = default)
    {
        var command = new AbandonTaskCommand(id, dto.Reason);
        var result = await commands.AbandonTaskAsync(command, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? NotFound(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>Resume an abandoned task.</summary>
    [Authorize(Policy = "TaskManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResumeTask(Guid id, [FromBody] ResumeTaskDto dto, CancellationToken ct = default)
    {
        var command = new ResumeTaskCommand(id, dto.NewSessionId);
        var result = await commands.ResumeTaskAsync(command, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? NotFound(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Starts a manufacture run for the task (phase 2.8). Same authorization as <c>POST /api/workflow-runs</c>. It answers:
    ///     <list type="bullet">
    ///         <item>404 when the task does not exist;</item>
    ///         <item>422 when its project's repository is not allow-listed, or a dependency is not Completed;</item>
    ///         <item>409 while its current run is live, or when the task changed after it was read and the run that
    ///         started could not be attached; that run is cancelled, and the body says whether it was;</item>
    ///         <item>201 with the run id once the run is started and attached.</item>
    ///     </list>
    ///     A starter failure maps as <c>POST /api/workflow-runs</c> maps it.
    /// </summary>
    [Authorize(Policy = "WorkflowResume")]
    [EnableRateLimiting("write-operations")]
    [HttpPost("{id:guid}/manufacture")]
    [ProducesResponseType(typeof(StartWorkflowRunResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Manufacture(Guid id, [FromServices] TaskManufactureService manufacture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manufacture);
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        var startedBy = RunPrincipals.From(caller, User.FindFirst("preferred_username")?.Value);
        var result = await manufacture.StartAsync(id, startedBy, ct);
        if (result.IsSuccess)
        {
            return Created(new Uri($"/api/workflow-runs/{result.Value}", UriKind.Relative), new StartWorkflowRunResponse(result.Value));
        }

        var failure = result.Error;
        switch (failure)
        {
            case { Kind: TaskManufactureFailureKind.TaskNotFound }:
                return Problem(detail: failure.Message, statusCode: StatusCodes.Status404NotFound);
            case { Kind: TaskManufactureFailureKind.RepositoryNotAllowed or TaskManufactureFailureKind.DependencyNotCompleted }:
                return Problem(detail: failure.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            case { Kind: TaskManufactureFailureKind.RunLive }:
                return Problem(detail: failure.Message, statusCode: StatusCodes.Status409Conflict);
            case { Kind: TaskManufactureFailureKind.StartFailed, Start: { } start }:
                return ManufactureStartProblem.From(this, start);
            case { Kind: TaskManufactureFailureKind.AttachConflict, RunId: { } lostRun }:
                return UnattachedRunProblem(StatusCodes.Status409Conflict, failure, lostRun);
            case { Kind: TaskManufactureFailureKind.AttachFailed, RunId: { } unattached }:
                return UnattachedRunProblem(StatusCodes.Status500InternalServerError, failure, unattached);
            default:
                return Problem(detail: failure.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    ///     A problem for a run that started but is not attached: the detail names the run and the cancel outcome, and the
    ///     <c>runId</c> and <c>runCancelled</c> extensions carry both for a client.
    /// </summary>
    private ObjectResult UnattachedRunProblem(int statusCode, TaskManufactureFailure failure, Guid runId)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: statusCode, detail: failure.Message);
        problem.Extensions["runId"] = runId;
        problem.Extensions["runCancelled"] = failure.RunCancelled;
        return new ObjectResult(problem) { StatusCode = statusCode };
    }
}
