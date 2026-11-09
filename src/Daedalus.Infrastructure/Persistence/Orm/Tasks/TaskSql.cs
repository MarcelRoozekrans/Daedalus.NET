using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using Complexity = Daedalus.Domain.Entities.Complexity;
using DomainTask = Daedalus.Domain.Entities.Task;
using Priority = Daedalus.Domain.Entities.Priority;
using TaskExecution = Daedalus.Domain.Entities.TaskExecution;

namespace Daedalus.Infrastructure.Persistence.Orm.Tasks;

/// <summary>
///     The SQL of the <c>"Tasks"</c> and <c>"TaskExecutions"</c> tables, over one open connection. Its public methods
///     take and return <see cref="DomainTask"/> and <see cref="TaskExecution"/>; the generated queries, their companions
///     and the row records stay inside.
/// </summary>
/// <remarks>
///     A task and its executions read as one command of two statements, a tuple, in place of EF Core's <c>Include</c>;
///     a page of tasks selects the children of the same page through an <c>IN</c> subquery that repeats the page's order,
///     which ends in the id so the two agree. The update and the delete are guarded by the <c>xmin</c> the task was read
///     with: a task another write changed since throws <see cref="ZeroAllocOrmConcurrencyException"/>. The insert is raw
///     SQL, as the ORM has no LINQ insert (ruling R10), and lists every column.
/// </remarks>
public sealed partial class TaskSql(IAsyncDbConnection connection)
{
    private const string InsertTaskSql = """
        INSERT INTO "Tasks"
            ("Id", "TaskId", "ProjectId", "Title", "Description", "Priority", "Status", "Phase", "ParallelGroup",
             "EstimatedComplexity", "Prompt", "CompletionPromise", "MaxIterations", "CurrentSessionId", "Result",
             "IterationCount", "CreatedAt", "CompletedAt", "Learnings", "LearningsUpdatedAt", "Dependencies",
             "FilesToModify", "WorkflowRunId")
        VALUES (@id, @taskId, @projectId, @title, @description, @priority, @status, @phase, @parallelGroup,
                @estimatedComplexity, @prompt, @completionPromise, @maxIterations, @currentSessionId, @result,
                @iterationCount, @createdAt, @completedAt, @learnings, @learningsUpdatedAt, @dependencies,
                @filesToModify, @workflowRunId)
        """;

    /// <summary>
    ///     Inserts <paramref name="task"/>'s row with every column as given. Its executions are not written: a task is
    ///     created without any, and the domain never adds one.
    /// </summary>
    /// <param name="task">The task to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public async Task InsertAsync(DomainTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await InsertTask(
            task.Id, task.TaskId, task.ProjectId, task.Title, task.Description, (int)task.Priority, (int)task.Status,
            task.Phase, task.ParallelGroup, (int)task.EstimatedComplexity, task.Prompt, task.CompletionPromise,
            task.MaxIterations, task.CurrentSessionId, task.Result, task.IterationCount, task.CreatedAt,
            task.CompletedAt, task.Learnings, task.LearningsUpdatedAt, [.. task.Dependencies], [.. task.FilesToModify],
            task.WorkflowRunId, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the task with <paramref name="id"/> and its executions, in one round trip.</summary>
    /// <param name="id">The task's id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The task as stored with its row version and executions, or null when there is none.</returns>
    public async Task<DomainTask?> FindAsync(Guid id, CancellationToken ct)
    {
        var (head, executions) = await FindRows(id, ct).ConfigureAwait(false);
        return head?.ToDomain(executions.ConvertAll(e => e.ToDomain()));
    }

    /// <summary>Whether a task with <paramref name="id"/> is stored.</summary>
    /// <param name="id">The task's id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>True when the row exists.</returns>
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Exists(id, ct);

    /// <summary>Reads every task of a project, oldest first, without their executions.</summary>
    /// <param name="projectId">The project's id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The tasks as stored, ordered by <c>CreatedAt</c>.</returns>
    public async Task<IReadOnlyList<DomainTask>> ListByProjectAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await ListByProject(projectId, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain([]));
    }

    /// <summary>
    ///     Reads the number of tasks and one page of them, newest first, each with its executions, in one read-only
    ///     REPEATABLE READ transaction: BEGIN, SET TRANSACTION, the three-statement read, COMMIT. The page orders by <c>CreatedAt</c> descending, then by id, so tasks created at the same instant page in one
    ///     order; the executions are those of the page's tasks.
    /// </summary>
    /// <param name="skip">How many tasks to skip.</param>
    /// <param name="take">How many tasks to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The total number of tasks and the page.</returns>
    /// <remarks>
    ///     The three statements run in one read-only REPEATABLE READ transaction, so they share one snapshot: a task
    ///     inserted or reordered between them cannot shift the page under its executions, as EF's one query could not.
    /// </remarks>
    public async Task<(int Total, IReadOnlyList<DomainTask> Page)> PageAsync(int skip, int take, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await SnapshotReadOnly(tx, ct).ConfigureAwait(false);
        var (total, heads, executions) = await PageRows(skip, take, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        var byTask = executions.ToLookup(e => e.TaskId);
        return (total, heads.ConvertAll(head => head.ToDomain(byTask[head.Id].Select(e => e.ToDomain()))));
    }

    /// <summary>Reads, for each of <paramref name="runIds"/>, the task it is attached to.</summary>
    /// <param name="runIds">The run ids.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>One entry per task attached to one of the runs: the run id, the task's id, identifier and title.</returns>
    public async Task<IReadOnlyList<(Guid RunId, Guid Id, string TaskId, string Title)>> RunLinksAsync(
        IEnumerable<Guid> runIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        var rows = await RunLinks([.. runIds], ct).ConfigureAwait(false);
        return rows.ConvertAll(row => (row.RunId!.Value, row.Id, row.TaskId, row.Title));
    }

    /// <summary>Reads the number of a task's executions and one page of them, newest first, in one round trip.</summary>
    /// <param name="taskId">The task's id.</param>
    /// <param name="skip">How many executions to skip.</param>
    /// <param name="take">How many executions to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The total and the page, ordered by <c>ExecutedAt</c> then id, both descending.</returns>
    public async Task<(int Total, IReadOnlyList<TaskExecution> Page)> ExecutionsByTaskAsync(
        Guid taskId, int skip, int take, CancellationToken ct)
    {
        var (total, rows) = await ExecutionsByTask(taskId, skip, take, ct).ConfigureAwait(false);
        return (total, rows.ConvertAll(row => row.ToDomain()));
    }

    /// <summary>Reads the number of a session's executions and one page of them, newest first, in one round trip.</summary>
    /// <param name="sessionId">The session's id.</param>
    /// <param name="skip">How many executions to skip.</param>
    /// <param name="take">How many executions to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The total and the page, ordered by <c>ExecutedAt</c> then id, both descending.</returns>
    public async Task<(int Total, IReadOnlyList<TaskExecution> Page)> ExecutionsBySessionAsync(
        Guid sessionId, int skip, int take, CancellationToken ct)
    {
        var (total, rows) = await ExecutionsBySession(sessionId, skip, take, ct).ConfigureAwait(false);
        return (total, rows.ConvertAll(row => row.ToDomain()));
    }

    /// <summary>
    ///     Writes the columns the task's domain methods change, <c>UpdateMetadata</c>'s, <c>UpdateExecutionConfig</c>'s
    ///     and <c>AttachRun</c>'s, when the row still has the <c>xmin</c> the task was read with. Its executions are not
    ///     written.
    /// </summary>
    /// <param name="task">The task as changed, carrying the row version it was read with.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since the task was read.</exception>
    public Task UpdateAsync(DomainTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        return Update(
            task.Id, task.RowVersion, task.Title, task.Description, task.Priority, task.Phase, task.EstimatedComplexity,
            task.ParallelGroup, task.Prompt, task.WorkflowRunId, ct);
    }

    /// <summary>
    ///     Deletes the task's row when it still has the <c>xmin</c> the task was read with; the database's cascade removes
    ///     its executions.
    /// </summary>
    /// <param name="task">The task as read, carrying its row version.</param>
    /// <param name="ct">Cancels the delete.</param>
    /// <returns>A task that completes when the row is deleted.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since the task was read.</exception>
    public Task DeleteAsync(DomainTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        return Delete(task.Id, task.RowVersion, ct);
    }

    [Command(InsertTaskSql)]
    private partial Task<int> InsertTask(
        Guid id, string taskId, Guid projectId, string title, string description, int priority, int status,
        string phase, int parallelGroup, int estimatedComplexity, string prompt, string completionPromise,
        int maxIterations, Guid? currentSessionId, string? result, int iterationCount, DateTime createdAt,
        DateTime? completedAt, string learnings, DateTime? learningsUpdatedAt, string[] dependencies,
        string[] filesToModify, Guid? workflowRunId, CancellationToken ct);

    [LinqQuery(nameof(FindRowsQuery))]
    private partial Task<(TaskRow? Head, List<TaskExecutionRow> Executions)> FindRows(Guid id, CancellationToken ct);

    private static (IQuery<TaskRow>, IQuery<TaskExecutionRow>) FindRowsQuery(Guid id) =>
        (Sql.Table<DomainTask>()
            .Where(t => t.Id == id)
            .Select(t => new TaskRow(
                t.Id, t.TaskId, t.ProjectId, t.Title, t.Description, t.Priority, t.Status, t.Phase, t.ParallelGroup,
                t.EstimatedComplexity, t.Prompt, t.CompletionPromise, t.MaxIterations, t.CurrentSessionId, t.Result,
                t.IterationCount, t.CreatedAt, t.CompletedAt, t.Learnings, t.LearningsUpdatedAt, t.Dependencies,
                t.FilesToModify, t.WorkflowRunId, t.RowVersion)),
         Sql.Table<TaskExecution>()
            .Where(e => e.TaskId == id)
            .OrderBy(e => e.ExecutedAt)
            .ThenBy(e => e.Id)
            .Select(e => new TaskExecutionRow(
                e.Id, e.TaskId, e.SessionId, e.IterationNumber, e.Prompt, e.LlmResponse, e.CompletionPromiseFound,
                e.ExecutedAt, e.ExecutionDuration, e.Error, e.InputTokens, e.ModelId, e.OutputTokens)));

    [LinqQuery(nameof(ExistsQuery))]
    private partial Task<bool> Exists(Guid id, CancellationToken ct);

    private static IQuery<bool> ExistsQuery(Guid id) =>
        Sql.Table<DomainTask>().Where(t => t.Id == id).Any();

    [LinqQuery(nameof(ListByProjectQuery))]
    private partial Task<List<TaskRow>> ListByProject(Guid projectId, CancellationToken ct);

    private static IQuery<TaskRow> ListByProjectQuery(Guid projectId) =>
        Sql.Table<DomainTask>()
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new TaskRow(
                t.Id, t.TaskId, t.ProjectId, t.Title, t.Description, t.Priority, t.Status, t.Phase, t.ParallelGroup,
                t.EstimatedComplexity, t.Prompt, t.CompletionPromise, t.MaxIterations, t.CurrentSessionId, t.Result,
                t.IterationCount, t.CreatedAt, t.CompletedAt, t.Learnings, t.LearningsUpdatedAt, t.Dependencies,
                t.FilesToModify, t.WorkflowRunId, t.RowVersion));

    [LinqQuery(nameof(PageRowsQuery))]
    private partial Task<(int Total, List<TaskRow> Heads, List<TaskExecutionRow> Executions)> PageRows(
        int skip, int take, IAsyncDbTransaction tx, CancellationToken ct);

    [Command("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY")]
    private partial Task<int> SnapshotReadOnly(IAsyncDbTransaction tx, CancellationToken ct);

    private static (IQuery<int>, IQuery<TaskRow>, IQuery<TaskExecutionRow>) PageRowsQuery(int skip, int take) =>
        (Sql.Table<DomainTask>().Count(),
         Sql.Table<DomainTask>()
            .OrderByDescending(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Skip(skip)
            .Take(take)
            .Select(t => new TaskRow(
                t.Id, t.TaskId, t.ProjectId, t.Title, t.Description, t.Priority, t.Status, t.Phase, t.ParallelGroup,
                t.EstimatedComplexity, t.Prompt, t.CompletionPromise, t.MaxIterations, t.CurrentSessionId, t.Result,
                t.IterationCount, t.CreatedAt, t.CompletedAt, t.Learnings, t.LearningsUpdatedAt, t.Dependencies,
                t.FilesToModify, t.WorkflowRunId, t.RowVersion)),
         Sql.Table<TaskExecution>()
            .Where(e => Sql.Table<DomainTask>()
                .OrderByDescending(t => t.CreatedAt)
                .ThenBy(t => t.Id)
                .Skip(skip)
                .Take(take)
                .Select(t => t.Id)
                .Contains(e.TaskId))
            .OrderBy(e => e.ExecutedAt)
            .ThenBy(e => e.Id)
            .Select(e => new TaskExecutionRow(
                e.Id, e.TaskId, e.SessionId, e.IterationNumber, e.Prompt, e.LlmResponse, e.CompletionPromiseFound,
                e.ExecutedAt, e.ExecutionDuration, e.Error, e.InputTokens, e.ModelId, e.OutputTokens)));

    // A NULL "WorkflowRunId" never matches = ANY, as EF's != null filter did (ruling R59).
    [LinqQuery(nameof(RunLinksQuery))]
    private partial Task<List<TaskRunLinkRow>> RunLinks(Guid[] runIds, CancellationToken ct);

    private static IQuery<TaskRunLinkRow> RunLinksQuery(Guid[] runIds) =>
        Sql.Table<DomainTask>()
            .Where(t => runIds.Contains(t.WorkflowRunId!.Value))
            .Select(t => new TaskRunLinkRow(t.WorkflowRunId, t.Id, t.TaskId, t.Title));

    [LinqQuery(nameof(ExecutionsByTaskQuery))]
    private partial Task<(int Total, List<TaskExecutionRow> Page)> ExecutionsByTask(
        Guid taskId, int skip, int take, CancellationToken ct);

    private static (IQuery<int>, IQuery<TaskExecutionRow>) ExecutionsByTaskQuery(Guid taskId, int skip, int take) =>
        (Sql.Table<TaskExecution>().Where(e => e.TaskId == taskId).Count(),
         Sql.Table<TaskExecution>()
            .Where(e => e.TaskId == taskId)
            .OrderByDescending(e => e.ExecutedAt)
            .ThenByDescending(e => e.Id)
            .Skip(skip)
            .Take(take)
            .Select(e => new TaskExecutionRow(
                e.Id, e.TaskId, e.SessionId, e.IterationNumber, e.Prompt, e.LlmResponse, e.CompletionPromiseFound,
                e.ExecutedAt, e.ExecutionDuration, e.Error, e.InputTokens, e.ModelId, e.OutputTokens)));

    [LinqQuery(nameof(ExecutionsBySessionQuery))]
    private partial Task<(int Total, List<TaskExecutionRow> Page)> ExecutionsBySession(
        Guid sessionId, int skip, int take, CancellationToken ct);

    private static (IQuery<int>, IQuery<TaskExecutionRow>) ExecutionsBySessionQuery(Guid sessionId, int skip, int take) =>
        (Sql.Table<TaskExecution>().Where(e => e.SessionId == sessionId).Count(),
         Sql.Table<TaskExecution>()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.ExecutedAt)
            .ThenByDescending(e => e.Id)
            .Skip(skip)
            .Take(take)
            .Select(e => new TaskExecutionRow(
                e.Id, e.TaskId, e.SessionId, e.IterationNumber, e.Prompt, e.LlmResponse, e.CompletionPromiseFound,
                e.ExecutedAt, e.ExecutionDuration, e.Error, e.InputTokens, e.ModelId, e.OutputTokens)));

    [LinqCommand(nameof(UpdateQuery), ThrowOnNoRows = true)]
    private partial Task Update(
        Guid id, uint rowVersion, string title, string description, Priority priority, string phase,
        Complexity estimatedComplexity, int parallelGroup, string prompt, Guid? workflowRunId, CancellationToken ct);

    private static int UpdateQuery(
        Guid id, uint rowVersion, string title, string description, Priority priority, string phase,
        Complexity estimatedComplexity, int parallelGroup, string prompt, Guid? workflowRunId) =>
        Sql.Table<DomainTask>()
            .Where(t => t.Id == id && t.RowVersion == rowVersion)
            .ExecuteUpdate(set => set
                .SetProperty(t => t.Title, title)
                .SetProperty(t => t.Description, description)
                .SetProperty(t => t.Priority, priority)
                .SetProperty(t => t.Phase, phase)
                .SetProperty(t => t.EstimatedComplexity, estimatedComplexity)
                .SetProperty(t => t.ParallelGroup, parallelGroup)
                .SetProperty(t => t.Prompt, prompt)
                .SetProperty(t => t.WorkflowRunId, workflowRunId));

    [LinqCommand(nameof(DeleteQuery), ThrowOnNoRows = true)]
    private partial Task Delete(Guid id, uint rowVersion, CancellationToken ct);

    private static int DeleteQuery(Guid id, uint rowVersion) =>
        Sql.Table<DomainTask>().Where(t => t.Id == id && t.RowVersion == rowVersion).ExecuteDelete();
}
