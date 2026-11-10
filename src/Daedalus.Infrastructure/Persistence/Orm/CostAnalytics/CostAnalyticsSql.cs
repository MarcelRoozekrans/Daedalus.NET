using System.Data.Async;
using ZeroAlloc.ORM.Linq;
using AgentSession = Daedalus.Domain.Entities.AgentSession;
using DomainTask = Daedalus.Domain.Entities.Task;
using Project = Daedalus.Domain.Entities.Project;
using TaskExecution = Daedalus.Domain.Entities.TaskExecution;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Infrastructure.Persistence.Orm.CostAnalytics;

/// <summary>
///     The read-only SQL of the cost analytics, over one open connection. It reads five tables that belong to other
///     aggregates, through their maps: <c>"TaskExecutions"</c>, <c>"Tasks"</c>, <c>"Projects"</c>,
///     <c>"WorkflowRunRecords"</c> and <c>"AgentSessions"</c>. It writes nothing.
/// </summary>
/// <remarks>
///     A read model, not an aggregate, so it returns its row records and is internal to Infrastructure, where its one
///     caller, <c>CostAnalyticsService</c>, lives (ruling R98). Every <c>.Date</c> renders
///     <c>date_trunc('day', "col", 'UTC')</c>, the UTC day EF Core's Npgsql provider cut, because every
///     <see cref="DateTime"/> column is declared <c>timestamptz</c>.
/// </remarks>
internal sealed partial class CostAnalyticsSql(IAsyncDbConnection connection)
{
    /// <summary>The task loop's executions, per model and UTC day of <c>"ExecutedAt"</c>.</summary>
    [LinqQuery(nameof(HistoryByModelAndDayQuery))]
    internal partial Task<List<HistoryDayRow>> HistoryByModelAndDay(CancellationToken ct);

    /// <summary>Every node-usage record's payload and creation time.</summary>
    [LinqQuery(nameof(NodeUsageRecordsQuery))]
    internal partial Task<List<NodeUsageRecordRow>> NodeUsageRecords(CancellationToken ct);

    /// <summary>The agent sessions, per owner and UTC day of <c>"CreatedAt"</c>.</summary>
    [LinqQuery(nameof(SessionsByOwnerAndDayQuery))]
    internal partial Task<List<SessionDayRow>> SessionsByOwnerAndDay(CancellationToken ct);

    /// <summary>The task loop's executions joined to their task and its project, per project and model.</summary>
    [LinqQuery(nameof(CostsByProjectAndModelQuery))]
    internal partial Task<List<ProjectModelCostRow>> CostsByProjectAndModel(CancellationToken ct);

    /// <summary>The executions of one project's tasks, per task and model.</summary>
    [LinqQuery(nameof(CostsByTaskAndModelQuery))]
    internal partial Task<List<TaskModelCostRow>> CostsByTaskAndModel(Guid projectId, CancellationToken ct);

    /// <summary>The mean output tokens of the executions that produced any, or null when none did.</summary>
    [LinqQuery(nameof(AverageOutputTokensQuery))]
    internal partial Task<double?> AverageOutputTokens(CancellationToken ct);

    private static IQuery<HistoryDayRow> HistoryByModelAndDayQuery() =>
        Sql.Table<TaskExecution>()
            .GroupBy(e => new { e.ModelId, Day = e.ExecutedAt.Date })
            .Select(g => new HistoryDayRow(
                g.Key.ModelId, g.Key.Day, g.Sum(e => (long)e.InputTokens), g.Sum(e => (long)e.OutputTokens), g.Count()));

    private static IQuery<NodeUsageRecordRow> NodeUsageRecordsQuery() =>
        Sql.Table<WorkflowRunRecord>()
            .Where(r => string.Equals(r.Kind, WorkflowRunRecord.NodeUsageKind, StringComparison.Ordinal))
            .Select(r => new NodeUsageRecordRow(r.PayloadJson, r.CreatedAt));

    private static IQuery<SessionDayRow> SessionsByOwnerAndDayQuery() =>
        Sql.Table<AgentSession>()
            .GroupBy(s => new { s.OwnerId, Day = s.CreatedAt.Date })
            .Select(g => new SessionDayRow(
                g.Key.OwnerId, g.Key.Day, g.Sum(s => s.TotalInputTokens), g.Sum(s => s.TotalOutputTokens), g.Count()));

    private static IQuery<ProjectModelCostRow> CostsByProjectAndModelQuery() =>
        Sql.Table<TaskExecution>()
            .Join(Sql.Table<DomainTask>(), e => e.TaskId, t => t.Id, (e, t) => new { Execution = e, Task = t })
            .Join(Sql.Table<Project>(), et => et.Task.ProjectId, p => p.Id, (et, p) => new { et.Execution, et.Task, Project = p })
            .GroupBy(x => new { x.Project.Id, x.Project.ProjectName, x.Execution.ModelId })
            .Select(g => new ProjectModelCostRow(
                g.Key.Id, g.Key.ProjectName, g.Key.ModelId,
                g.Sum(x => (long)x.Execution.InputTokens), g.Sum(x => (long)x.Execution.OutputTokens), g.Count()));

    private static IQuery<TaskModelCostRow> CostsByTaskAndModelQuery(Guid projectId) =>
        Sql.Table<TaskExecution>()
            .Join(
                Sql.Table<DomainTask>().Where(t => t.ProjectId == projectId),
                e => e.TaskId,
                t => t.Id,
                (e, t) => new { Execution = e, Task = t })
            .GroupBy(x => new { x.Task.Id, x.Task.Title, x.Execution.ModelId })
            .Select(g => new TaskModelCostRow(
                g.Key.Id, g.Key.Title, g.Key.ModelId,
                g.Sum(x => (long)x.Execution.InputTokens), g.Sum(x => (long)x.Execution.OutputTokens), g.Count()));

    private static IQuery<double?> AverageOutputTokensQuery() =>
        Sql.Table<TaskExecution>()
            .Where(e => e.OutputTokens > 0)
            .Average(e => (double?)e.OutputTokens);
}
