using Daedalus.Application.Abstractions;
using Daedalus.Application.Mappers;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Api.Services;

/// <summary>Reads tasks, each with its status derived from its run: one run lookup per task that has one.</summary>
public sealed class TaskQueryService(ApplicationDbContext dbContext, IWorkflowRunStatusReader runs) : ITaskQueryService
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

        var dtos = new List<TaskDto>(tasks.Count);
        foreach (var task in tasks)
        {
            dtos.Add(TaskDtoMapper.ToDto(task, await runs.ReadRunAsync(task, ct).ConfigureAwait(false)));
        }

        return new PagedResultDto<TaskDto>(dtos, total, page, pageSize);
    }

    public async Task<TaskDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var task = await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.Executions)
            .FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);

        return task is null ? null : TaskDtoMapper.ToDto(task, await runs.ReadRunAsync(task, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyDictionary<Guid, TaskRunLink>> GetRunLinksAsync(
        IReadOnlyCollection<Guid> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0)
        {
            return new Dictionary<Guid, TaskRunLink>();
        }

        var ids = runIds.ToList();
        var rows = await dbContext.Tasks
            .AsNoTracking()
            .Where(t => t.WorkflowRunId != null && ids.Contains(t.WorkflowRunId.Value))
            .Select(t => new { RunId = t.WorkflowRunId!.Value, t.Id, t.TaskId, t.Title })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var links = new Dictionary<Guid, TaskRunLink>(rows.Count);
        foreach (var row in rows)
        {
            links.TryAdd(row.RunId, new TaskRunLink(row.Id, row.TaskId, row.Title));
        }

        return links;
    }
}
