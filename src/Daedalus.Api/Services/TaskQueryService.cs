using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Api.Services;

/// <summary>Implementation of ITaskQueryService.</summary>
public sealed class TaskQueryService(ApplicationDbContext dbContext) : ITaskQueryService
{
    public async Task<PagedResultDto<TaskDto>> GetAllAsync(int page = 1, int pageSize = 10,
        CancellationToken ct = default)
    {
        var total = await dbContext.Tasks.CountAsync(ct).ConfigureAwait(false);

        var tasks = await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.Executions)
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        var dtos = tasks.Select(MapToDto).ToList();

        return new PagedResultDto<TaskDto>(dtos, total, page, pageSize);
    }

    public async Task<TaskDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var task = await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.Executions)
            .FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);

        return task is null ? null : MapToDto(task);
    }

    private static TaskDto MapToDto(Task task)
    {
        return new TaskDto(
            task.Id,
            task.TaskId,
            task.ProjectId,
            task.Title,
            task.Description,
            (int)task.Priority,
            task.Phase,
            task.ParallelGroup,
            task.Dependencies,
            task.FilesToModify,
            (int)task.EstimatedComplexity,
            task.Prompt,
            task.CompletionPromise,
            task.MaxIterations,
            (int)task.Status,
            task.CurrentSessionId,
            task.Result,
            task.IterationCount,
            task.CreatedAt,
            task.CompletedAt,
            task.Learnings,
            task.LearningsUpdatedAt,
            task.Executions.Select(e => new TaskExecutionDto(
                e.Id,
                e.TaskId,
                e.SessionId,
                e.IterationNumber,
                e.Prompt,
                e.LlmResponse,
                e.CompletionPromiseFound,
                e.ExecutedAt,
                e.ExecutionDuration,
                e.Error,
                e.InputTokens,
                e.OutputTokens,
                e.ModelId
            )).ToList()
        );
    }
}
