using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Infrastructure.Services;

/// <summary>Implementation of project query service.</summary>
public sealed class ProjectQueryService(ApplicationDbContext context) : IProjectQueryService
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

        var dtos = projects.Select(p => new ProjectDto(
                p.Id,
                p.ProjectName,
                p.Description,
                p.Version,
                p.CreatedAt,
                p.ModifiedAt,
                p.Tasks.Select(TaskDtoMapper.ToDtoWithoutExecutions).ToList().AsReadOnly()
            ))
            .ToList();

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
            project.CreatedAt,
            project.ModifiedAt,
            project.Tasks.Select(TaskDtoMapper.ToDtoWithoutExecutions).ToList().AsReadOnly()
        );
    }
}
