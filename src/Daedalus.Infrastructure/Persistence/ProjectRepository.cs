using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Project repository for persistence and querying.
/// </summary>
public sealed partial class ProjectRepository(ApplicationDbContext dbContext, ILogger<ProjectRepository> logger)
    : IProjectRepository
{
    public async Task<Result<Project>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var project = await dbContext.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id, ct)
                .ConfigureAwait(false);

            return project is not null
                ? Result.Success(project)
                : Result.Failure<Project>($"Project {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingProject(logger, ex, id);
            return Result.Failure<Project>($"Error retrieving project: {ex.Message}");
        }
    }

    public async Task<Result<Project>> AddAsync(Project project, CancellationToken ct)
    {
        try
        {
            dbContext.Projects.Add(project);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success(project);
        }
        catch (Exception ex)
        {
            LogErrorAddingProject(logger, ex, project.Id);
            return Result.Failure<Project>($"Error adding project: {ex.Message}");
        }
    }

    public async Task<Result> UpdateAsync(Project project, CancellationToken ct)
    {
        try
        {
            dbContext.Projects.Update(project);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorUpdatingProject(logger, ex, project.Id);
            return Result.Failure($"Error updating project: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var project = await dbContext.Projects
                .FirstOrDefaultAsync(p => p.Id == id, ct)
                .ConfigureAwait(false);

            if (project is null)
            {
                return Result.Failure($"Project {id} not found");
            }

            dbContext.Projects.Remove(project);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorDeletingProject(logger, ex, id);
            return Result.Failure($"Error deleting project: {ex.Message}");
        }
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 200, Level = LogLevel.Error,
        Message = "Error retrieving project {ProjectId}")]
    private static partial void LogErrorRetrievingProject(
        ILogger logger, Exception ex, Guid projectId);

    [LoggerMessage(EventId = 201, Level = LogLevel.Error,
        Message = "Error adding project {ProjectId}")]
    private static partial void LogErrorAddingProject(
        ILogger logger, Exception ex, Guid projectId);

    [LoggerMessage(EventId = 202, Level = LogLevel.Error,
        Message = "Error updating project {ProjectId}")]
    private static partial void LogErrorUpdatingProject(
        ILogger logger, Exception ex, Guid projectId);

    [LoggerMessage(EventId = 203, Level = LogLevel.Error,
        Message = "Error deleting project {ProjectId}")]
    private static partial void LogErrorDeletingProject(
        ILogger logger, Exception ex, Guid projectId);
}
