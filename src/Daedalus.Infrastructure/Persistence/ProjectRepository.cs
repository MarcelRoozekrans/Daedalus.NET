using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.Projects;
using Microsoft.Extensions.Logging;
using ZeroAlloc.ORM;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Stores projects on ZeroAlloc.ORM, one pooled connection per call.
/// </summary>
/// <remarks>
///     <see cref="UpdateAsync"/> is guarded by the <c>xmin</c> the project was read with, so a save of a project another
///     write changed since is refused with <see cref="ModifiedByAnotherOperation"/>. A save does not read the new
///     <c>xmin</c> back into the instance: re-read a project with <see cref="GetByIdAsync"/> after
///     <see cref="AddAsync"/> or <see cref="UpdateAsync"/> before its next guarded save, or that save is refused as
///     modified by another operation (rulings R58, R70).
/// </remarks>
public sealed partial class ProjectRepository(DaedalusDb db, ILogger<ProjectRepository> logger)
    : IProjectRepository
{
    /// <summary>The failure of a guarded save that lost a race with another write to the project.</summary>
    public const string ModifiedByAnotherOperation = "The project was modified by another operation. Please retry.";

    public async Task<Result<Project>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var project = await new ProjectSql(connection).FindAsync(id, ct).ConfigureAwait(false);

            return project is not null
                ? Result<Project>.Success(project)
                : Result<Project>.Failure($"Project {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingProject(logger, ex, id);
            return Result<Project>.Failure($"Error retrieving project {id}. The cause is logged.");
        }
    }

    public async Task<Result<Project>> AddAsync(Project project, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            await new ProjectSql(connection).InsertAsync(project, ct).ConfigureAwait(false);
            return Result<Project>.Success(project);
        }
        catch (Exception ex)
        {
            LogErrorAddingProject(logger, ex, project.Id);
            return Result<Project>.Failure($"Error adding project {project.Id}. The cause is logged.");
        }
    }

    /// <summary>
    ///     Writes the name, description, version and modification time when the row still has the <c>xmin</c> the
    ///     project was read with. A row another write changed since answers <see cref="ModifiedByAnotherOperation"/>; a
    ///     row deleted since keeps EF Core's answer, the logged "Error updating project {id}" (ruling R68).
    /// </summary>
    public async Task<Result> UpdateAsync(Project project, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var sql = new ProjectSql(connection);
            ZeroAllocOrmConcurrencyException conflict;
            try
            {
                await sql.UpdateAsync(project, ct).ConfigureAwait(false);
                return Result.Success();
            }
            catch (ZeroAllocOrmConcurrencyException ex)
            {
                conflict = ex;
            }

            if (await sql.ExistsAsync(project.Id, ct).ConfigureAwait(false))
            {
                return Result.Failure(ModifiedByAnotherOperation);
            }

            LogErrorUpdatingProject(logger, conflict, project.Id);
            return Result.Failure($"Error updating project {project.Id}. The cause is logged.");
        }
        catch (Exception ex)
        {
            LogErrorUpdatingProject(logger, ex, project.Id);
            return Result.Failure($"Error updating project {project.Id}. The cause is logged.");
        }
    }

    /// <summary>
    ///     Deletes the project's row; the database's cascade removes its tasks. A delete that removes no row, a project
    ///     not stored or deleted since the caller read it, answers "Project {id} not found" (ruling R69).
    /// </summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var deleted = await new ProjectSql(connection).DeleteAsync(id, ct).ConfigureAwait(false);
            return deleted > 0 ? Result.Success() : Result.Failure($"Project {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorDeletingProject(logger, ex, id);
            return Result.Failure($"Error deleting project {id}. The cause is logged.");
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
