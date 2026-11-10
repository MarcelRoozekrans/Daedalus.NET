using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRunExecutions;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Seeds and reads the two scheduling tables through the ZeroAlloc.ORM SQL classes, for the tests that used to do
///     it through <c>ApplicationDbContext</c> before Task 21 deleted that mapping (ruling R125). Not a second code
///     path to the data: every call goes through the same <see cref="ScheduledRunSql"/> and
///     <see cref="ScheduledRunExecutionSql"/> the stores use.
/// </summary>
internal static class ScheduleTables
{
    /// <summary>Inserts <paramref name="runs"/> in one transaction.</summary>
    /// <param name="fixture">The database fixture.</param>
    /// <param name="runs">The schedules to store.</param>
    /// <returns>A task that completes when they are committed.</returns>
    public static async Task InsertAsync(PostgresFixture fixture, params ScheduledRun[] runs)
    {
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        await using var tx = await connection.BeginTransactionAsync(CancellationToken.None);
        var sql = new ScheduledRunSql(connection);
        foreach (var run in runs)
        {
            await sql.InsertAsync(run, tx, CancellationToken.None);
        }

        await tx.CommitAsync(CancellationToken.None);
    }

    /// <summary>Inserts <paramref name="executions"/> in one transaction, each of which must claim a free occurrence.</summary>
    /// <param name="fixture">The database fixture.</param>
    /// <param name="executions">The executions to store.</param>
    /// <returns>A task that completes when they are committed.</returns>
    public static async Task InsertAsync(PostgresFixture fixture, params ScheduledRunExecution[] executions)
    {
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        await using var tx = await connection.BeginTransactionAsync(CancellationToken.None);
        var sql = new ScheduledRunExecutionSql(connection);
        foreach (var execution in executions)
        {
            (await sql.TryInsertAsync(execution, tx, CancellationToken.None))
                .Should().BeTrue("a seeded execution must claim a free occurrence");
        }

        await tx.CommitAsync(CancellationToken.None);
    }

    /// <summary>Every schedule, by name.</summary>
    /// <param name="fixture">The database fixture.</param>
    /// <returns>The schedules.</returns>
    public static async Task<IReadOnlyList<ScheduledRun>> RunsAsync(PostgresFixture fixture)
    {
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        return await new ScheduledRunSql(connection).ListAllAsync(CancellationToken.None);
    }

    /// <summary>The one schedule called <paramref name="name"/>.</summary>
    /// <param name="fixture">The database fixture.</param>
    /// <param name="name">The schedule's name.</param>
    /// <returns>The schedule.</returns>
    public static async Task<ScheduledRun> RunAsync(PostgresFixture fixture, string name) =>
        (await RunsAsync(fixture)).Single(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    /// <summary>The execution with <paramref name="id"/>.</summary>
    /// <param name="fixture">The database fixture.</param>
    /// <param name="id">The execution's id.</param>
    /// <returns>The execution.</returns>
    public static async Task<ScheduledRunExecution> ExecutionAsync(PostgresFixture fixture, Guid id)
    {
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        return (await new ScheduledRunExecutionSql(connection).FindAsync(id, CancellationToken.None))
            ?? throw new InvalidOperationException($"No execution {id}.");
    }

    /// <summary>The ids of every execution, in no particular order.</summary>
    /// <param name="fixture">The database fixture.</param>
    /// <returns>The ids.</returns>
    public static async Task<IReadOnlyList<Guid>> ExecutionIdsAsync(PostgresFixture fixture)
    {
        await using var command = fixture.DataSource.CreateCommand("""SELECT "Id" FROM "ScheduledRunExecutions" """);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>The only execution.</summary>
    /// <param name="fixture">The database fixture.</param>
    /// <returns>The execution.</returns>
    public static async Task<ScheduledRunExecution> SingleExecutionAsync(PostgresFixture fixture) =>
        await ExecutionAsync(fixture, (await ExecutionIdsAsync(fixture)).Single());

    /// <summary>How many executions exist.</summary>
    /// <param name="fixture">The database fixture.</param>
    /// <returns>The count.</returns>
    public static async Task<int> ExecutionCountAsync(PostgresFixture fixture) =>
        (await ExecutionIdsAsync(fixture)).Count;
}
