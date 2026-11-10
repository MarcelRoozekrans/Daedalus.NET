using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 3.5: <see cref="WorkflowRunRecordStore"/> on ZeroAlloc.ORM keeps every column of a record, and loads a row
///     whatever <see cref="WorkflowRunRecord.Create"/> would say of it today.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowRunRecordStoreRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid _runId = new(0x2e7c4a19, 0x6d3b, 0x4f8a, 0x91, 0x5c, 0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f);
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Every property comes back as stored, the id the database gave it aside. The payload is written in
    ///     <c>jsonb</c>'s own text form, so the string compares as well as the value.
    ///     Red: leave any column out of the insert or the read, and its property differs.
    /// </summary>
    [Fact]
    public async Task Stored_record_loads_back_whole()
    {
        var store = new WorkflowRunRecordStore(fixture.Db);
        var stored = WorkflowRunRecord.Create(
            _runId, 11, "review", WorkflowRunRecord.ReviewEvidenceKind, "workflow:run/review", "u-admin",
            """{"lens": "tests", "checked": ["a.cs"]}""", _at).Value;

        await store.AppendAsync(stored, CancellationToken.None);
        var loaded = (await store.ListAsync(_runId, kind: null, CancellationToken.None)).Single();

        loaded.Should().BeEquivalentTo(stored, options => options.Excluding(r => r.Id));
        loaded.Id.Should().BePositive();
        loaded.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>
    ///     Review focus 1: a row <see cref="WorkflowRunRecord.Create"/> refuses, a blank node and an empty starter id
    ///     here, still loads, as written. Red: build the loaded record through <c>Create</c>, and the load fails.
    /// </summary>
    [Fact]
    public async Task A_row_Create_rejects_still_loads()
    {
        WorkflowRunRecord.Create(_runId, 3, "   ", WorkflowRunRecord.WorkspaceWriteKind, "p", "", "{}", _at)
            .IsFailure.Should().BeTrue("the row below is one Create refuses today");
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO "WorkflowRunRecords"
                    ("RunId", "Seq", "Node", "Kind", "PrincipalId", "StartedById", "PayloadJson", "CreatedAt")
                VALUES (@runId, 3, '   ', 'workspace-write', 'p', '', '{}', @at)
                """,
                connection);
            insert.Parameters.AddWithValue("runId", _runId);
            insert.Parameters.AddWithValue("at", _at);
            await insert.ExecuteNonQueryAsync();
        }

        var loaded = (await new WorkflowRunRecordStore(fixture.Db).ListAsync(_runId, kind: null, CancellationToken.None))
            .Single();

        loaded.Node.Should().Be("   ");
        loaded.StartedById.Should().BeEmpty();
        loaded.Seq.Should().Be(3);
        loaded.CreatedAt.Should().Be(_at);
    }
}
