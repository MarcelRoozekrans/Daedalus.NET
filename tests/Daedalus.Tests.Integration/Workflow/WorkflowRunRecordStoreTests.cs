using System.Text.Json;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     <see cref="WorkflowRunRecordStore"/> against the real <c>WorkflowRunRecords</c> table. Tests in the database
///     collection run sequentially, and the database is reset before each one.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowRunRecordStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid _runId = new(0x0b6f2f5e, 0x1a1d, 0x4d8e, 0x8c, 0x5b, 0x2f, 0x7a, 0x9e, 0x3c, 0x4d, 0x10);
    private static readonly Guid _otherRunId = new(0x5c8e1d2a, 0x9b3f, 0x4a6e, 0xb7, 0xc1, 0x0d, 0x2e, 0x3f, 0x4a, 0x5b, 0x6c);
    private static readonly DateTime _sameInstant = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private WorkflowRunRecordStore NewStore() => new(new FixtureDbContextFactory(fixture));

    private static WorkflowRunRecord Record(
        long seq, string kind, string node = "implement", Guid? runId = null, string payloadJson = "{}") =>
        WorkflowRunRecord.Create(
            runId ?? _runId, seq, node, kind, "workflow:run/" + node, "u-admin", payloadJson, _sameInstant).Value;

    [Fact]
    public async Task Records_list_in_seq_order_not_append_or_timestamp_order()
    {
        var store = NewStore();
        // Appended out of Seq order, all at one instant, so neither Id nor CreatedAt order gives Seq order.
        await store.AppendAsync(Record(3, WorkflowRunRecord.WorkspaceWriteKind, "c"), CancellationToken.None);
        await store.AppendAsync(Record(1, WorkflowRunRecord.WorkspaceWriteKind, "a"), CancellationToken.None);
        await store.AppendAsync(Record(2, WorkflowRunRecord.ReviewEvidenceKind, "b"), CancellationToken.None);

        var listed = await store.ListAsync(_runId, kind: null, CancellationToken.None);

        listed.Select(r => r.Node).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Records_that_share_a_seq_list_in_append_order()
    {
        var store = NewStore();
        await store.AppendAsync(Record(4, WorkflowRunRecord.WorkspaceWriteKind, "first"), CancellationToken.None);
        await store.AppendAsync(Record(4, WorkflowRunRecord.WorkspaceWriteKind, "second"), CancellationToken.None);
        await store.AppendAsync(Record(4, WorkflowRunRecord.WorkspaceWriteKind, "third"), CancellationToken.None);

        var listed = await store.ListAsync(_runId, kind: null, CancellationToken.None);

        listed.Select(r => r.Node).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task A_kind_filter_returns_only_that_kind_of_that_run()
    {
        var store = NewStore();
        await store.AppendAsync(Record(1, WorkflowRunRecord.WorkspaceWriteKind, "w1"), CancellationToken.None);
        await store.AppendAsync(Record(2, WorkflowRunRecord.ReviewEvidenceKind, "r2"), CancellationToken.None);
        await store.AppendAsync(Record(3, WorkflowRunRecord.WorkspaceWriteKind, "w3"), CancellationToken.None);
        await store.AppendAsync(
            Record(1, WorkflowRunRecord.WorkspaceWriteKind, "other", _otherRunId), CancellationToken.None);

        var writes = await store.ListAsync(_runId, WorkflowRunRecord.WorkspaceWriteKind, CancellationToken.None);
        var reviews = await store.ListAsync(_runId, WorkflowRunRecord.ReviewEvidenceKind, CancellationToken.None);
        var all = await store.ListAsync(_runId, kind: null, CancellationToken.None);

        writes.Select(r => r.Node).Should().Equal("w1", "w3");
        reviews.Select(r => r.Node).Should().Equal("r2");
        all.Select(r => r.Node).Should().Equal("w1", "r2", "w3");
    }

    [Fact]
    public async Task A_record_round_trips_every_field_and_its_payload_value()
    {
        var store = NewStore();
        var appended = WorkflowRunRecord.Create(
            _runId, 7, "review", WorkflowRunRecord.ReviewEvidenceKind, "workflow:run/review", startedById: null,
            """{ "lens": "tests",   "checked": ["a.cs"] }""", _sameInstant).Value;

        await store.AppendAsync(appended, CancellationToken.None);
        var read = (await store.ListAsync(_runId, kind: null, CancellationToken.None)).Single();

        read.Id.Should().BePositive();
        read.RunId.Should().Be(_runId);
        read.Seq.Should().Be(7);
        read.Node.Should().Be("review");
        read.Kind.Should().Be("review-evidence");
        read.PrincipalId.Should().Be("workflow:run/review");
        read.StartedById.Should().BeNull();
        read.CreatedAt.Should().Be(_sameInstant);
        // jsonb keeps the value, not the text, so compare parsed values.
        using var payload = JsonDocument.Parse(read.PayloadJson);
        payload.RootElement.GetProperty("lens").GetString().Should().Be("tests");
        payload.RootElement.GetProperty("checked")[0].GetString().Should().Be("a.cs");
    }

    [Fact]
    public void The_store_has_no_update_or_delete_method() =>
        typeof(IWorkflowRunRecordStore).GetMethods().Select(m => m.Name)
            .Should().BeEquivalentTo(["AppendAsync", "ListAsync"]);

    /// <summary>The store disposes every context it creates, so the factory needs no tracking.</summary>
    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
