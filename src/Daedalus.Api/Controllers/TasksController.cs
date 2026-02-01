using System.Threading.RateLimiting;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.AbandonTask;
using Daedalus.Application.Commands.CreateTask;
using Daedalus.Application.Commands.DeleteTask;
using Daedalus.Application.Commands.ResumeTask;
using Daedalus.Application.Commands.UpdateTask;
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
    ICommandHandlerFactory commandFactory,
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
            dto.Prompt,
            dto.CompletionPromise,
            dto.MaxIterations);

        var handler = commandFactory.GetHandler<CreateTaskCommand, Result<TaskDto>>(command);
        var result = await handler.Handle(command, ct);

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
            dto.Prompt,
            dto.CompletionPromise,
            dto.MaxIterations);

        var handler = commandFactory.GetHandler<UpdateTaskCommand, Result<TaskDto>>(command);
        var result = await handler.Handle(command, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTask(Guid id, CancellationToken ct = default)
    {
        var command = new DeleteTaskCommand(id);
        var handler = commandFactory.GetHandler<DeleteTaskCommand, Result>(command);
        var result = await handler.Handle(command, ct);

        if (result.IsSuccess)
        {
            return NoContent();
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
        var handler = commandFactory.GetHandler<AbandonTaskCommand, Result<TaskDto>>(command);
        var result = await handler.Handle(command, ct);

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
        var handler = commandFactory.GetHandler<ResumeTaskCommand, Result<TaskDto>>(command);
        var result = await handler.Handle(command, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? NotFound(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }
}
