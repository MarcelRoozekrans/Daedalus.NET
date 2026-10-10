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
}
