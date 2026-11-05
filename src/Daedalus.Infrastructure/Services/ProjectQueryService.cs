using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.Projects;
using DomainTask = Daedalus.Domain.Entities.Task;
using Project = Daedalus.Domain.Entities.Project;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Reads projects on ZeroAlloc.ORM, each task with its status derived from its run: one run lookup per task that has
///     one. The connection is closed before the run lookups start.
/// </summary>
public sealed class ProjectQueryService(DaedalusDb db, IWorkflowRunStatusReader runs) : IProjectQueryService
{
    public async Task<PagedResultDto<ProjectDto>> GetAllAsync(int page, int pageSize, CancellationToken ct = default)
    {
        int total;
        IReadOnlyList<Project> projects;
        await using (var connection = await db.OpenAsync(ct).ConfigureAwait(false))
        {
            (total, projects) = await new ProjectSql(connection)
                .PageWithTasksAsync((page - 1) * pageSize, pageSize, ct)
                .ConfigureAwait(false);
        }

        var dtos = new List<ProjectDto>(projects.Count);
        foreach (var p in projects)
        {
            dtos.Add(ToDto(p, await ToTaskDtosAsync(p.Tasks, ct).ConfigureAwait(false)));
        }

        return new PagedResultDto<ProjectDto>(
            dtos.AsReadOnly(),
            total,
            page,
            pageSize
        );
    }

    public async Task<ProjectDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var project = await new ProjectSql(connection).FindAsync(id, ct).ConfigureAwait(false);

        return project is null ? null : ToDto(project, new List<TaskDto>().AsReadOnly());
    }

    public async Task<ProjectDto?> GetWithTasksAsync(Guid id, CancellationToken ct = default)
    {
        Project? project;
        await using (var connection = await db.OpenAsync(ct).ConfigureAwait(false))
        {
            project = await new ProjectSql(connection).FindWithTasksAsync(id, ct).ConfigureAwait(false);
        }

        return project is null
            ? null
            : ToDto(project, await ToTaskDtosAsync(project.Tasks, ct).ConfigureAwait(false));
    }

    private static ProjectDto ToDto(Project project, IReadOnlyList<TaskDto> tasks) =>
        new(
            project.Id,
            project.ProjectName,
            project.Description,
            project.Version,
            project.RepositoryUrl,
            project.DefaultBranch,
            project.CreatedAt,
            project.ModifiedAt,
            tasks);

    private async Task<IReadOnlyList<TaskDto>> ToTaskDtosAsync(IEnumerable<DomainTask> tasks, CancellationToken ct)
    {
        var dtos = new List<TaskDto>();
        foreach (var task in tasks)
        {
            dtos.Add(TaskDtoMapper.ToDtoWithoutExecutions(task, await runs.ReadRunAsync(task, ct).ConfigureAwait(false)));
        }

        return dtos.AsReadOnly();
    }
}
