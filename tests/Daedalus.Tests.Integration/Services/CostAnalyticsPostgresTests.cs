using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Configuration;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Integration.Fixtures;
using Daedalus.Tests.Shared;
using Microsoft.Extensions.Options;
using Npgsql;
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
///     The summary groups by <c>timestamptz.Date</c> inside the database, cut to the UTC day as EF Core's Npgsql provider cut
///     it, so only PostgreSQL proves the day boundaries.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CostAnalyticsPostgresTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime Day1 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 10, 2, 23, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime JustAfterUtcMidnight = new(2026, 10, 2, 0, 30, 0, DateTimeKind.Utc);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Red: swap <c>TotalInputTokens</c> and <c>TotalOutputTokens</c> in the sessions query; the chat row reads 2 input
    ///     tokens and the scheduled row 1.
    /// </summary>
    [Fact]
    public async Task The_summary_runs_on_postgres_and_splits_every_source_by_utc_day()
    {
        var project = DomainProject.Create(Guid.NewGuid(), "P", "D").Value;
        var task = DomainTask.Create(Guid.NewGuid(), project.Id, "T-1", "T", "D", Priority.Medium, "x", 1, Complexity.Medium, "p").Value;
        await StoreSeed.AddAsync(fixture.Db, [project], [task]);
        await StoreSeed.AddExecutionsAsync(fixture.ConnectionString, new TaskExecution
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
        await StoreSeed.AddRecordAsync(fixture.Db, WorkflowRunRecord.Create(
            Guid.NewGuid(), 3, "implement", WorkflowRunRecord.NodeUsageKind, "host", null,
            new NodeUsage("m", 2_000, 200, 500, 50).ToPayloadJson(), Day2).Value);
        foreach (var (owner, input) in new[] { ("alice", 300), ("alice", 30), ("schedule:daily", 400), ("workflow:manufacture:1", 9_999) })
        {
            var session = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), owner, Day2).Value;
            session.RecordTurn(input, 1, Day2);
            await StoreSeed.AddSessionAsync(fixture.Db, session);
        }

        var pricing = Options.Create(new ModelPricingConfiguration());

        var summary = await new CostAnalyticsService(fixture.Db, pricing, Options.Create(new ClaudeConfiguration())).GetSummaryAsync();

        summary.ByDay.Select(d => (d.Day, d.Source, d.InputTokens, d.Entries)).Should().Equal(
            (new DateOnly(2026, 10, 1), CostSources.History, 100L, 1),
            (new DateOnly(2026, 10, 2), CostSources.Chat, 330L, 2),
            (new DateOnly(2026, 10, 2), CostSources.Manufacture, 2_000L, 1),
            (new DateOnly(2026, 10, 2), CostSources.Scheduled, 400L, 1));
    }

    /// <summary>
    ///     The day is the UTC day whatever the connection's <c>TimeZone</c>, as EF Core's <c>date_trunc('day', col, 'UTC')</c>
    ///     cut it. In Los Angeles the 00:30 UTC rows of 2 October are still 1 October; a cut in the session's zone puts
    ///     them there.
    ///     Red: drop <c>EntityMapDefaults(DateTimeStorage = TimestampTz)</c> from the ORM assembly attributes; the cut is
    ///     <c>date_trunc('day', col)</c> in the session's zone and the rows move to 1 October.
    ///     Red: count each history group as one entry; the two executions of the day read as 1.
    /// </summary>
    [Fact]
    public async Task A_day_is_the_utc_day_whatever_the_session_time_zone()
    {
        var project = DomainProject.Create(Guid.NewGuid(), "P", "D").Value;
        var task = DomainTask.Create(Guid.NewGuid(), project.Id, "T-1", "T", "D", Priority.Medium, "x", 1, Complexity.Medium, "p").Value;
        await StoreSeed.AddAsync(fixture.Db, [project], [task]);
        await StoreSeed.AddExecutionsAsync(
            fixture.ConnectionString,
            Execution(task.Id, 10, inputTokens: 100, at: JustAfterUtcMidnight),
            Execution(task.Id, 10, inputTokens: 50, at: JustAfterUtcMidnight));
        var session = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), "alice", JustAfterUtcMidnight).Value;
        session.RecordTurn(300, 1, JustAfterUtcMidnight);
        await StoreSeed.AddSessionAsync(fixture.Db, session);
        var losAngeles = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Timezone = "America/Los_Angeles" };
        await using var scratch = new ScratchDaedalusDb(losAngeles.ConnectionString);

        var summary = await new CostAnalyticsService(
            scratch.Db, Options.Create(new ModelPricingConfiguration()), Options.Create(new ClaudeConfiguration())).GetSummaryAsync();

        summary.ByDay.Select(d => (d.Day, d.Source, d.InputTokens, d.Entries)).Should().Equal(
            (new DateOnly(2026, 10, 2), CostSources.Chat, 300L, 1),
            (new DateOnly(2026, 10, 2), CostSources.History, 150L, 2));
    }

    /// <summary>
    ///     The estimate's response tokens are the mean output of the executions that produced any, truncated, as EF's
    ///     <c>AverageAsync</c> gave them: 1000 and 3001 average 2000.5, so 2000; the execution with no output is left out.
    ///     With none, the default is 4000.
    ///     Red: drop the <c>OutputTokens &gt; 0</c> filter from the average; the mean is 1333.67 and the estimate 1333.
    ///     Red: fall back to 0 instead of 4000 when the average is null; the first assertion fails.
    /// </summary>
    [Fact]
    public async Task The_estimate_uses_the_mean_output_of_the_executions_that_produced_any()
    {
        var pricing = Options.Create(new ModelPricingConfiguration
        {
            Models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
            {
                ["m"] = new() { DisplayName = "M", InputTokenPricePerMillion = 1m, OutputTokenPricePerMillion = 1m },
            },
        });
        var service = new CostAnalyticsService(fixture.Db, pricing, Options.Create(new ClaudeConfiguration()));
        (await service.EstimateCostAsync("m", 10, 100)).Value.EstimatedResponseTokens.Should().Be(4000);

        var project = DomainProject.Create(Guid.NewGuid(), "P", "D").Value;
        var task = DomainTask.Create(Guid.NewGuid(), project.Id, "T-1", "T", "D", Priority.Medium, "x", 1, Complexity.Medium, "p").Value;
        await StoreSeed.AddAsync(fixture.Db, [project], [task]);
        await StoreSeed.AddExecutionsAsync(
            fixture.ConnectionString, Execution(task.Id, 0), Execution(task.Id, 1_000), Execution(task.Id, 3_001));

        (await service.EstimateCostAsync("m", 10, 100)).Value.EstimatedResponseTokens.Should().Be(2000);
    }

    private static TaskExecution Execution(Guid taskId, int outputTokens, int inputTokens = 0, DateTime? at = null) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        SessionId = Guid.NewGuid(),
        IterationNumber = 1,
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        ModelId = "m",
        ExecutedAt = at ?? Day1,
    };
}
