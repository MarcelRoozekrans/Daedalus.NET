using Daedalus.Application.Abstractions;
using Daedalus.Application.Mappers;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.Tasks;
using DomainTask = Daedalus.Domain.Entities.Task;

namespace Daedalus.Api.Services;

/// <summary>Reads tasks, each with its status derived from its run: one run lookup per task that has one.</summary>
public sealed class TaskQueryService(DaedalusDb db, IWorkflowRunStatusReader runs) : ITaskQueryService
{
    public async Task<PagedResultDto<TaskDto>> GetAllAsync(int page = 1, int pageSize = 10,
        CancellationToken ct = default)
    {
        int total;
        IReadOnlyList<DomainTask> tasks;
        await using (var connection = await db.OpenAsync(ct).ConfigureAwait(false))
        {
            (total, tasks) = await new TaskSql(connection).PageAsync((page - 1) * pageSize, pageSize, ct)
                .ConfigureAwait(false);
        }

        var dtos = new List<TaskDto>(tasks.Count);
        foreach (var task in tasks)
        {
            dtos.Add(TaskDtoMapper.ToDto(task, await runs.ReadRunAsync(task, ct).ConfigureAwait(false)));
        }

        return new PagedResultDto<TaskDto>(dtos, total, page, pageSize);
    }

    public async Task<TaskDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        DomainTask? task;
        await using (var connection = await db.OpenAsync(ct).ConfigureAwait(false))
        {
            task = await new TaskSql(connection).FindAsync(id, ct).ConfigureAwait(false);
        }

        return task is null ? null : TaskDtoMapper.ToDto(task, await runs.ReadRunAsync(task, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyDictionary<Guid, TaskRunLink>> GetRunLinksAsync(
        IReadOnlyCollection<Guid> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0)
        {
            return new Dictionary<Guid, TaskRunLink>();
        }

        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await new TaskSql(connection).RunLinksAsync(runIds, ct).ConfigureAwait(false);

        var links = new Dictionary<Guid, TaskRunLink>(rows.Count);
        foreach (var row in rows)
        {
            links.TryAdd(row.RunId, new TaskRunLink(row.Id, row.TaskId, row.Title));
        }

        return links;
    }
}
