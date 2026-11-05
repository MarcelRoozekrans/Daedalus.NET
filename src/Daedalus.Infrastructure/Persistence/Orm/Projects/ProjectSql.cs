using System.Data.Async;
using Daedalus.Infrastructure.Persistence.Orm.Tasks;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using DomainTask = Daedalus.Domain.Entities.Task;
using Project = Daedalus.Domain.Entities.Project;

namespace Daedalus.Infrastructure.Persistence.Orm.Projects;

/// <summary>
///     The SQL of the <c>"Projects"</c> table, and of the <c>"Tasks"</c> rows read with a project, over one open
///     connection. Its public methods take and return <see cref="Project"/>; the generated queries, their companions and
///     the row records stay inside. The tasks read through <see cref="TaskMap"/> and <see cref="TaskRow"/>, the task
///     family's own map and row record, with no second map of the table.
/// </summary>
/// <remarks>
///     A project and its tasks read as one command of two statements, a tuple, in place of EF Core's <c>Include</c>; a
///     page of projects selects the tasks of the same page through an <c>IN</c> subquery that repeats the page's order,
///     which ends in the id so the two agree. The update is guarded by the <c>xmin</c> the project was read with: a
///     project another write changed or deleted since throws <see cref="ZeroAllocOrmConcurrencyException"/>. The delete
///     is not guarded and answers the rows it removed. The insert is raw SQL, as the ORM has no LINQ insert (ruling
///     R10), and lists every column; migration 2 dropped the dead <c>bytea</c> <c>"RowVersion"</c>.
/// </remarks>
public sealed partial class ProjectSql(IAsyncDbConnection connection)
{
    private const string InsertProjectSql = """
        INSERT INTO "Projects"
            ("Id", "Version", "ProjectName", "Description", "CreatedAt", "ModifiedAt", "DefaultBranch", "RepositoryUrl")
        VALUES (@id, @version, @projectName, @description, @createdAt, @modifiedAt, @defaultBranch, @repositoryUrl)
        """;

    /// <summary>
    ///     Inserts <paramref name="project"/>'s row with every live column as given. Its tasks are not written: a project
    ///     is created without any, and tasks are stored through the task repository.
    /// </summary>
    /// <param name="project">The project to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public async Task InsertAsync(Project project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        await InsertProject(
            project.Id, project.Version, project.ProjectName, project.Description, project.CreatedAt,
            project.ModifiedAt, project.DefaultBranch, project.RepositoryUrl, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the project with <paramref name="id"/>, without its tasks.</summary>
    /// <param name="id">The project's id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The project as stored with its row version, or null when there is none.</returns>
    public async Task<Project?> FindAsync(Guid id, CancellationToken ct)
    {
        var row = await FindRow(id, ct).ConfigureAwait(false);
        return row?.ToDomain([]);
    }

    /// <summary>Reads the project with <paramref name="id"/> and its tasks, in one round trip.</summary>
    /// <param name="id">The project's id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    ///     The project as stored with its tasks, oldest first and then by id, without their executions; or null when
    ///     there is none.
    /// </returns>
    public async Task<Project?> FindWithTasksAsync(Guid id, CancellationToken ct)
    {
        var (head, tasks) = await FindWithTasksRows(id, ct).ConfigureAwait(false);
        return head?.ToDomain(tasks.ConvertAll(t => t.ToDomain([])));
    }

    /// <summary>
    ///     Reads the number of projects and one page of them, by name and then by id, each with its tasks, in one
    ///     read-only REPEATABLE READ transaction: BEGIN, SET TRANSACTION, the three-statement read, COMMIT.
    /// </summary>
    /// <param name="skip">How many projects to skip.</param>
    /// <param name="take">How many projects to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The total number of projects and the page, each project with its tasks, oldest first and then by id.</returns>
    /// <remarks>
    ///     The three statements share one snapshot, so a project inserted or renamed between them cannot shift the page
    ///     under its tasks, as EF's one query could not.
    /// </remarks>
    public async Task<(int Total, IReadOnlyList<Project> Page)> PageWithTasksAsync(int skip, int take, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await SnapshotReadOnly(tx, ct).ConfigureAwait(false);
        var (total, heads, tasks) = await PageRows(skip, take, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        var byProject = tasks.ToLookup(t => t.ProjectId);
        return (total, heads.ConvertAll(head => head.ToDomain(byProject[head.Id].Select(t => t.ToDomain([])))));
    }

    /// <summary>Whether a project with <paramref name="id"/> is stored.</summary>
    /// <param name="id">The project's id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>True when the row exists.</returns>
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Exists(id, ct);

    /// <summary>
    ///     Writes the columns <c>UpdateMetadata</c> and <c>UpdateVersion</c> change, the name, description, version and
    ///     modification time, when the row still has the <c>xmin</c> the project was read with. Its tasks are not
    ///     written.
    /// </summary>
    /// <param name="project">The project as changed, carrying the row version it was read with.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since the project was read.</exception>
    public Task UpdateAsync(Project project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Update(
            project.Id, project.RowVersion, project.ProjectName, project.Description, project.Version,
            project.ModifiedAt, ct);
    }

    /// <summary>Deletes the project's row; the database's cascade removes its tasks and their executions.</summary>
    /// <param name="id">The project's id.</param>
    /// <param name="ct">Cancels the delete.</param>
    /// <returns>The number of project rows deleted, 0 when none was stored.</returns>
    public Task<int> DeleteAsync(Guid id, CancellationToken ct) => Delete(id, ct);

    [Command(InsertProjectSql)]
    private partial Task<int> InsertProject(
        Guid id, string version, string projectName, string description, DateTime createdAt, DateTime? modifiedAt,
        string defaultBranch, string repositoryUrl, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<ProjectRow?> FindRow(Guid id, CancellationToken ct);

    private static IQuery<ProjectRow> FindRowQuery(Guid id) =>
        Sql.Table<Project>()
            .Where(p => p.Id == id)
            .Select(p => new ProjectRow(
                p.Id, p.Version, p.ProjectName, p.Description, p.CreatedAt, p.ModifiedAt, p.DefaultBranch,
                p.RepositoryUrl, p.RowVersion));

    [LinqQuery(nameof(FindWithTasksRowsQuery))]
    private partial Task<(ProjectRow? Head, List<TaskRow> Tasks)> FindWithTasksRows(Guid id, CancellationToken ct);

    private static (IQuery<ProjectRow>, IQuery<TaskRow>) FindWithTasksRowsQuery(Guid id) =>
        (Sql.Table<Project>()
            .Where(p => p.Id == id)
            .Select(p => new ProjectRow(
                p.Id, p.Version, p.ProjectName, p.Description, p.CreatedAt, p.ModifiedAt, p.DefaultBranch,
                p.RepositoryUrl, p.RowVersion)),
         Sql.Table<DomainTask>()
            .Where(t => t.ProjectId == id)
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => new TaskRow(
                t.Id, t.TaskId, t.ProjectId, t.Title, t.Description, t.Priority, t.Status, t.Phase, t.ParallelGroup,
                t.EstimatedComplexity, t.Prompt, t.CompletionPromise, t.MaxIterations, t.CurrentSessionId, t.Result,
                t.IterationCount, t.CreatedAt, t.CompletedAt, t.Learnings, t.LearningsUpdatedAt, t.Dependencies,
                t.FilesToModify, t.WorkflowRunId, t.RowVersion)));

    [Command("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY")]
    private partial Task<int> SnapshotReadOnly(IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(PageRowsQuery))]
    private partial Task<(int Total, List<ProjectRow> Heads, List<TaskRow> Tasks)> PageRows(
        int skip, int take, IAsyncDbTransaction tx, CancellationToken ct);

    private static (IQuery<int>, IQuery<ProjectRow>, IQuery<TaskRow>) PageRowsQuery(int skip, int take) =>
        (Sql.Table<Project>().Count(),
         Sql.Table<Project>()
            .OrderBy(p => p.ProjectName)
            .ThenBy(p => p.Id)
            .Skip(skip)
            .Take(take)
            .Select(p => new ProjectRow(
                p.Id, p.Version, p.ProjectName, p.Description, p.CreatedAt, p.ModifiedAt, p.DefaultBranch,
                p.RepositoryUrl, p.RowVersion)),
         Sql.Table<DomainTask>()
            .Where(t => Sql.Table<Project>()
                .OrderBy(p => p.ProjectName)
                .ThenBy(p => p.Id)
                .Skip(skip)
                .Take(take)
                .Select(p => p.Id)
                .Contains(t.ProjectId))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => new TaskRow(
                t.Id, t.TaskId, t.ProjectId, t.Title, t.Description, t.Priority, t.Status, t.Phase, t.ParallelGroup,
                t.EstimatedComplexity, t.Prompt, t.CompletionPromise, t.MaxIterations, t.CurrentSessionId, t.Result,
                t.IterationCount, t.CreatedAt, t.CompletedAt, t.Learnings, t.LearningsUpdatedAt, t.Dependencies,
                t.FilesToModify, t.WorkflowRunId, t.RowVersion)));

    [LinqQuery(nameof(ExistsQuery))]
    private partial Task<bool> Exists(Guid id, CancellationToken ct);

    private static IQuery<bool> ExistsQuery(Guid id) =>
        Sql.Table<Project>().Where(p => p.Id == id).Any();

    [LinqCommand(nameof(UpdateQuery), ThrowOnNoRows = true)]
    private partial Task Update(
        Guid id, uint rowVersion, string projectName, string description, string version, DateTime? modifiedAt,
        CancellationToken ct);

    private static int UpdateQuery(
        Guid id, uint rowVersion, string projectName, string description, string version, DateTime? modifiedAt) =>
        Sql.Table<Project>()
            .Where(p => p.Id == id && p.RowVersion == rowVersion)
            .ExecuteUpdate(set => set
                .SetProperty(p => p.ProjectName, projectName)
                .SetProperty(p => p.Description, description)
                .SetProperty(p => p.Version, version)
                .SetProperty(p => p.ModifiedAt, modifiedAt));

    [LinqCommand(nameof(DeleteQuery))]
    private partial Task<int> Delete(Guid id, CancellationToken ct);

    private static int DeleteQuery(Guid id) =>
        Sql.Table<Project>().Where(p => p.Id == id).ExecuteDelete();
}
