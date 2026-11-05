using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.AgentSessions;
using Daedalus.Infrastructure.Persistence.Orm.Projects;
using Daedalus.Infrastructure.Persistence.Orm.Tasks;
using Daedalus.Infrastructure.Persistence.Orm.WorkflowRunRecords;
using Npgsql;
using DomainTask = Daedalus.Domain.Entities.Task;
using Project = Daedalus.Domain.Entities.Project;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Shared;

/// <summary>
///     Seeds test rows through the ZeroAlloc.ORM SQL classes, where tests added them to an EF Core <c>DbSet</c> and
///     called <c>SaveChangesAsync</c> before phase 3.5 deleted those mappings. A project is written with the tasks of its
///     <see cref="Project.Tasks"/>, as EF's graph insert wrote them, and projects and tasks are written in one
///     transaction. Linked into each test project that seeds these tables.
/// </summary>
internal static class StoreSeed
{
    /// <summary>
    ///     Inserts <paramref name="project"/> and every task in its <see cref="Project.Tasks"/>, as
    ///     <c>db.Projects.Add(project)</c> did through the navigation.
    /// </summary>
    public static async SystemTask AddProjectAsync(DaedalusDb db, Project project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        await AddAsync(db, [project], [], ct).ConfigureAwait(false);
    }

    /// <summary>Inserts <paramref name="project"/> and its tasks unless a project with its id is stored already.</summary>
    public static async SystemTask AddProjectIfAbsentAsync(DaedalusDb db, Project project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(project);
        bool exists;
        await using (var connection = await db.OpenAsync(ct).ConfigureAwait(false))
        {
            exists = await new ProjectSql(connection).ExistsAsync(project.Id, ct).ConfigureAwait(false);
        }

        if (!exists)
        {
            await AddProjectAsync(db, project, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     <paramref name="task"/> as an old task-loop row reads back: the loop settings and status that
    ///     <see cref="DomainTask.Create"/> no longer takes, set through <see cref="DomainTask.Rehydrate"/>, where tests set
    ///     them on EF's change tracker before saving.
    /// </summary>
    public static DomainTask AsStored(
        DomainTask task, int? maxIterations = null, string? completionPromise = null,
        Daedalus.Domain.Entities.TaskStatus? status = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        return DomainTask.Rehydrate(
            task.Id, task.TaskId, task.ProjectId, task.Title, task.Description, task.Priority, status ?? task.Status,
            task.Phase, task.ParallelGroup, task.EstimatedComplexity, task.Prompt, completionPromise ?? task.CompletionPromise,
            maxIterations ?? task.MaxIterations, task.CurrentSessionId, task.Result, task.IterationCount, task.CreatedAt,
            task.CompletedAt, task.Learnings, task.LearningsUpdatedAt, task.Dependencies, task.FilesToModify,
            task.WorkflowRunId, task.RowVersion, task.Executions);
    }

    /// <summary>Inserts <paramref name="tasks"/>, each of whose project is already stored.</summary>
    public static SystemTask AddTasksAsync(DaedalusDb db, params DomainTask[] tasks) =>
        AddAsync(db, [], tasks, CancellationToken.None);

    /// <summary>Inserts <paramref name="projects"/> with their tasks and then <paramref name="tasks"/>, in one transaction.</summary>
    public static async SystemTask AddAsync(
        DaedalusDb db, IReadOnlyList<Project> projects, IReadOnlyList<DomainTask> tasks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var projectSql = new ProjectSql(connection);
        var taskSql = new TaskSql(connection);
        foreach (var project in projects)
        {
            await projectSql.InsertAsync(project, ct).ConfigureAwait(false);
            foreach (var task in project.Tasks)
            {
                await taskSql.InsertAsync(task, ct).ConfigureAwait(false);
            }
        }

        foreach (var task in tasks)
        {
            await taskSql.InsertAsync(task, ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Inserts <paramref name="session"/> with its turn count and token totals as they stand.</summary>
    public static async SystemTask AddSessionAsync(DaedalusDb db, AgentSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await new AgentSessionSql(connection).InsertAsync(session, ct).ConfigureAwait(false);
    }

    /// <summary>Inserts <paramref name="record"/>; the database gives it its id.</summary>
    public static async SystemTask AddRecordAsync(DaedalusDb db, WorkflowRunRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await new WorkflowRunRecordSql(connection).InsertAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Inserts <paramref name="executions"/>, every column as given, by raw SQL: production no longer writes task
    ///     executions, the task loop that did being retired, so no SQL class has an insert for them.
    /// </summary>
    public static async SystemTask AddExecutionsAsync(string connectionString, params TaskExecution[] executions)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        foreach (var execution in executions)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO "TaskExecutions"
                    ("Id", "TaskId", "SessionId", "IterationNumber", "Prompt", "LlmResponse", "CompletionPromiseFound",
                     "ExecutedAt", "ExecutionDuration", "Error", "InputTokens", "ModelId", "OutputTokens")
                VALUES (@id, @taskId, @sessionId, @iteration, @prompt, @response, @found, @at, @duration, @error,
                        @input, @model, @output)
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("id", execution.Id);
            command.Parameters.AddWithValue("taskId", execution.TaskId);
            command.Parameters.AddWithValue("sessionId", execution.SessionId);
            command.Parameters.AddWithValue("iteration", execution.IterationNumber);
            command.Parameters.AddWithValue("prompt", execution.Prompt);
            command.Parameters.AddWithValue("response", execution.LlmResponse);
            command.Parameters.AddWithValue("found", execution.CompletionPromiseFound);
            command.Parameters.AddWithValue("at", execution.ExecutedAt);
            command.Parameters.AddWithValue("duration", execution.ExecutionDuration);
            command.Parameters.AddWithValue("error", (object?)execution.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("input", execution.InputTokens);
            command.Parameters.AddWithValue("model", (object?)execution.ModelId ?? DBNull.Value);
            command.Parameters.AddWithValue("output", execution.OutputTokens);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }
}

/// <summary>
///     Rows staged the way tests staged them on an EF Core context, <c>Projects.Add</c>, <c>Tasks.AddRange</c> and so on,
///     and written by <see cref="SaveChangesAsync"/> in the order the foreign keys need: projects with their tasks, then
///     tasks, then executions, sessions and run records. Saving clears the staged rows, so a batch is reused as a context
///     was.
/// </summary>
/// <param name="db">The database the rows go to.</param>
/// <param name="connectionString">The same database's connection string, for executions; null when none are staged.</param>
internal sealed class SeedBatch(DaedalusDb db, string? connectionString = null)
{
    /// <summary>Projects to insert, each with the tasks of its <see cref="Project.Tasks"/>.</summary>
    public List<Project> Projects { get; } = [];

    /// <summary>Tasks to insert on their own, after the projects.</summary>
    public List<DomainTask> Tasks { get; } = [];

    /// <summary>Task executions to insert, after the tasks.</summary>
    public List<TaskExecution> TaskExecutions { get; } = [];

    /// <summary>Agent sessions to insert.</summary>
    public List<AgentSession> AgentSessions { get; } = [];

    /// <summary>Workflow run records to insert.</summary>
    public List<WorkflowRunRecord> WorkflowRunRecords { get; } = [];

    /// <summary>Writes every staged row and clears the batch.</summary>
    public async SystemTask SaveChangesAsync(CancellationToken ct = default)
    {
        await StoreSeed.AddAsync(db, Projects, Tasks, ct).ConfigureAwait(false);
        if (TaskExecutions.Count > 0)
        {
            await StoreSeed.AddExecutionsAsync(
                connectionString ?? throw new InvalidOperationException("Executions need the batch's connection string."),
                [.. TaskExecutions]).ConfigureAwait(false);
        }

        foreach (var session in AgentSessions)
        {
            await StoreSeed.AddSessionAsync(db, session, ct).ConfigureAwait(false);
        }

        foreach (var record in WorkflowRunRecords)
        {
            await StoreSeed.AddRecordAsync(db, record, ct).ConfigureAwait(false);
        }

        Projects.Clear();
        Tasks.Clear();
        TaskExecutions.Clear();
        AgentSessions.Clear();
        WorkflowRunRecords.Clear();
    }
}
