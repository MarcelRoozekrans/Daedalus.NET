using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using DomainTask = Daedalus.Domain.Entities.Task;

namespace Daedalus.Infrastructure.Services;

/// <summary>Implementation of project query service.</summary>
public sealed class ProjectQueryService(ApplicationDbContext context, IWorkflowRunStatusReader runs) : IProjectQueryService
{
    public async Task<PagedResultDto<ProjectDto>> GetAllAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var query = context.Projects.AsNoTracking();
        var total = await query.CountAsync(ct).ConfigureAwait(false);

        var projects = await query
            .OrderBy(p => p.ProjectName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(p => p.Tasks)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var dtos = new List<ProjectDto>(projects.Count);
        foreach (var p in projects)
        {
            dtos.Add(new ProjectDto(
                p.Id,
                p.ProjectName,
                p.Description,
                p.Version,
                p.RepositoryUrl,
                p.DefaultBranch,
                p.CreatedAt,
                p.ModifiedAt,
                await ToTaskDtosAsync(p.Tasks, ct).ConfigureAwait(false)));
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
        var project = await context.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            .ConfigureAwait(false);

        if (project is null)
        {
            return null;
        }

        return new ProjectDto(
            project.Id,
            project.ProjectName,
            project.Description,
            project.Version,
            project.RepositoryUrl,
            project.DefaultBranch,
            project.CreatedAt,
            project.ModifiedAt,
            new List<TaskDto>().AsReadOnly()
        );
    }

    public async Task<ProjectDto?> GetWithTasksAsync(Guid id, CancellationToken ct = default)
    {
        var project = await context.Projects
            .AsNoTracking()
            .Include(p => p.Tasks)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            .ConfigureAwait(false);

        if (project is null)
        {
            return null;
        }

        return new ProjectDto(
            project.Id,
            project.ProjectName,
            project.Description,
            project.Version,
            project.RepositoryUrl,
            project.DefaultBranch,
            project.CreatedAt,
            project.ModifiedAt,
            await ToTaskDtosAsync(project.Tasks, ct).ConfigureAwait(false)
        );
    }

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
