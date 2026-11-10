using DomainTask = Daedalus.Domain.Entities.Task;
using Project = Daedalus.Domain.Entities.Project;

namespace Daedalus.Infrastructure.Persistence.Orm.Projects;

/// <summary>One <c>"Projects"</c> row, every live column and its <c>xmin</c>, as <see cref="ProjectSql"/> reads it.</summary>
internal sealed record ProjectRow(
    Guid Id,
    string Version,
    string ProjectName,
    string Description,
    DateTime CreatedAt,
    DateTime? ModifiedAt,
    string DefaultBranch,
    string RepositoryUrl,
    uint RowVersion)
{
    /// <summary>The project the row holds, as stored: <see cref="Project.Rehydrate"/> validates nothing.</summary>
    /// <param name="tasks">The project's tasks, empty for a read that loads none.</param>
    /// <returns>The domain project.</returns>
    public Project ToDomain(IEnumerable<DomainTask> tasks) =>
        Project.Rehydrate(
            Id, Version, ProjectName, Description, CreatedAt, ModifiedAt, DefaultBranch, RepositoryUrl, RowVersion, tasks);
}
