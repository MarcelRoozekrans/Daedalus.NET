using Daedalus.Agents.Memory;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Memory;
using Thalos.Testing;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Runs Thalos.NET's <see cref="IMemoryStore"/> contract suite against the Postgres-backed store.
///     Tests in the database collection run sequentially; the database is reset before each test, and the base
///     class creates one store per test through <see cref="CreateStoreAsync"/> with a fake clock it advances itself.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresMemoryStoreTests(PostgresFixture fixture) : MemoryStoreContractTests, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    protected override ValueTask<IMemoryStore> CreateStoreAsync(TimeProvider clock) =>
        new(new PostgresMemoryStore(new FixtureDbContextFactory(fixture), clock));

    /// <summary>
    ///     Daedalus-specific: the contract suite never crosses a keyset page (its largest stream is 12 rows), so exercise the
    ///     paging seam with a batch size of 3 over 10 rows in three <c>CreatedAt</c> groups (ties larger than one page) while
    ///     the consumer clears <c>IndexPending</c> on every yielded record.
    /// </summary>
    [Fact]
    public async Task Stream_keyset_pages_across_batches_with_created_at_ties_while_yielded_rows_leave_the_filter()
    {
        var clock = NewClock();
        var store = new PostgresMemoryStore(new FixtureDbContextFactory(fixture), clock, streamBatchSize: 3);
        var groups = new List<HashSet<MemoryId>>();
        foreach (var groupSize in new[] { 4, 1, 5 }) // 4 rows share t0, 1 row at t1, 5 rows share t2
        {
            var ids = new HashSet<MemoryId>();
            for (var i = 0; i < groupSize; i++)
            {
                var created = await store.CreateAsync(NewRecord(clock, text: $"g{groups.Count}-{i}", indexPending: true), CancellationToken.None);
                created.IsSuccess.Should().BeTrue();
                ids.Add(created.Value.Id);
            }

            groups.Add(ids);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var yielded = new List<MemoryId>();
        await foreach (var r in store.StreamAsync(new MemoryQuery { IndexPending = true }, CancellationToken.None))
        {
            yielded.Add(r.Id);
            (await store.UpdateAsync(r.Id, new MemoryUpdate { IndexPending = false }, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        // Every match exactly once, oldest CreatedAt group first; the order inside a tie group is the store's own tie-break.
        yielded.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        yielded.Take(4).Should().BeEquivalentTo(groups[0]);
        yielded.Skip(4).Take(1).Should().BeEquivalentTo(groups[1]);
        yielded.Skip(5).Should().BeEquivalentTo(groups[2]);
        (await store.ListAsync(new MemoryQuery { IndexPending = true }, CancellationToken.None)).Value.TotalCount.Should().Be(0);
    }

    /// <summary>Daedalus-specific: the tag filter is AND over normalised tags, translated to per-tag <c>= ANY("Tags")</c>.</summary>
    [Fact]
    public async Task List_tag_filter_requires_every_tag()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.CreateAsync(NewRecord(clock, text: "xy", tags: ["x", "y"]), CancellationToken.None);
        await store.CreateAsync(NewRecord(clock, text: "x", tags: ["x"]), CancellationToken.None);
        await store.CreateAsync(NewRecord(clock, text: "none", tags: []), CancellationToken.None);

        (await store.ListAsync(new MemoryQuery { Tags = ["x"] }, CancellationToken.None)).Value.Items.Select(i => i.Text).Should().BeEquivalentTo(["xy", "x"]);
        (await store.ListAsync(new MemoryQuery { Tags = ["x", "y"] }, CancellationToken.None)).Value.Items.Select(i => i.Text).Should().BeEquivalentTo(["xy"]);
        (await store.ListAsync(new MemoryQuery { Tags = ["x", "z"] }, CancellationToken.None)).Value.Items.Should().BeEmpty();
        (await store.ListAsync(new MemoryQuery { Tags = [" "] }, CancellationToken.None)).Value.Items.Should().BeEmpty("a blank query tag matches nothing, like MemoryQuery.Matches");
    }

    /// <summary>The store disposes every context it creates, so the factory needs no tracking.</summary>
    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
