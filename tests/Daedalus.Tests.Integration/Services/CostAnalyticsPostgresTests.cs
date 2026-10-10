using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Options;
using AgentSession = Daedalus.Domain.Entities.AgentSession;
using Complexity = Daedalus.Domain.Entities.Complexity;
using DomainProject = Daedalus.Domain.Entities.Project;
using DomainTask = Daedalus.Domain.Entities.Task;
using NodeUsage = Daedalus.Domain.Entities.NodeUsage;
using Priority = Daedalus.Domain.Entities.Priority;
using TaskExecution = Daedalus.Domain.Entities.TaskExecution;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     The summary groups by <c>timestamptz.Date</c> inside the database. The in-memory provider evaluates that client-side,
///     so only Npgsql proves the query translates.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CostAnalyticsPostgresTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime Day1 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 10, 2, 23, 30, 0, DateTimeKind.Utc);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Red: replace <c>ExecutedAt.Date</c> in the history query with a call to a client-side method;
    ///     Npgsql throws "could not be translated".
    ///     Red: take the sessions day as <c>CreatedAt.Date.AddDays(1)</c>; the chat and scheduled rows land on day 3.
    /// </summary>
    [Fact]
    public async Task The_summary_runs_on_postgres_and_splits_every_source_by_utc_day()
    {
        await using var db = fixture.CreateDbContext();
        var project = DomainProject.Create(Guid.NewGuid(), "P", "D").Value;
        var task = DomainTask.Create(Guid.NewGuid(), project.Id, "T-1", "T", "D", Priority.Medium, "x", 1, Complexity.Medium, "p").Value;
        db.Projects.Add(project);
        db.Tasks.Add(task);
        db.TaskExecutions.Add(new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SessionId = Guid.NewGuid(),
            IterationNumber = 1,
            InputTokens = 100,
            OutputTokens = 10,
            ModelId = "m",
            ExecutedAt = Day1,
        });
        db.WorkflowRunRecords.Add(WorkflowRunRecord.Create(
            Guid.NewGuid(), 3, "implement", WorkflowRunRecord.NodeUsageKind, "host", null,
            new NodeUsage("m", 2_000, 200, 500, 50).ToPayloadJson(), Day2).Value);
        foreach (var (owner, input) in new[] { ("alice", 300), ("alice", 30), ("schedule:daily", 400), ("workflow:manufacture:1", 9_999) })
        {
            var session = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), owner, Day2).Value;
            session.RecordTurn(input, 1, Day2);
            db.AgentSessions.Add(session);
        }

        await db.SaveChangesAsync();
        var pricing = Options.Create(new ModelPricingConfiguration());

        var summary = await new CostAnalyticsService(db, pricing).GetSummaryAsync();

        summary.ByDay.Select(d => (d.Day, d.Source, d.InputTokens, d.Entries)).Should().Equal(
            (new DateOnly(2026, 10, 1), CostSources.History, 100L, 1),
            (new DateOnly(2026, 10, 2), CostSources.Chat, 330L, 2),
            (new DateOnly(2026, 10, 2), CostSources.Manufacture, 2_000L, 1),
            (new DateOnly(2026, 10, 2), CostSources.Scheduled, 400L, 1));
    }
}
