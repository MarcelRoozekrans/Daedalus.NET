using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Concurrent;
using System.Data.Common;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Integration tests for <see cref="ScheduledRunExecutionStore"/> against a real PostgreSQL database — the
///     correctness core of the saga-free scheduling redesign. Every test resolves the store the way production
///     must: from its own <see cref="IServiceScope"/>, with the real ZeroAlloc.Outbox-generated writers sharing
///     that scope's <see cref="ApplicationDbContext"/>, because the store is scoped, not a singleton over
///     <c>IDbContextFactory</c> — see the store's own remarks for why. The central claims under test: the same
///     <see cref="ScheduledRunDue"/> can never create two executions (the unique index, not handler diligence);
///     a redelivered step command can never re-run a completed step; and the terminal delivery step's outbox write
///     commits atomically with <c>Step = Done</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunExecutionStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime Occurrence = new(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc);

    // Generous enough that it never fires on the happy path (a rendezvous resolves in microseconds once both
    // sides arrive), short enough that a genuine hang - one side turned away before it ever reaches the barrier -
    // fails the test with a diagnosable TimeoutException instead of a CI job timeout.
    private static readonly TimeSpan _rendezvousTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 17, 7, 0, 5, TimeSpan.Zero));
    private readonly List<IAsyncDisposable> _disposables = [];
    private IOutboxSerializer? _serializer;
    private Guid _scheduleId;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        // Two roles, not one: a single-element list round-trips through almost any encoding (JSON, a different
        // delimiter, ...) unchanged, so it cannot catch a drift between the store's hand-encoded
        // string.Join(',', row.Roles) and ScheduledRunExecutionConfiguration's independently declared converter.
        _scheduleId = await SeedScheduleAsync("daily-digest", "telegram", "123456", "schedule:daedalus", ["reader", "writer"]);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    [Fact]
    public async Task Begin_creates_one_execution_and_enqueues_the_scout_step()
    {
        var began = await Store().TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);

        began.Should().BeTrue();
        var row = await SingleExecutionAsync();
        row.Step.Should().Be(RunStep.Scout);
        row.ChannelId.Should().Be("telegram");
        row.ConversationId.Should().Be("123456");
        row.PrincipalId.Should().Be("schedule:daedalus",
            "identity is frozen at claim time, so editing the schedule mid-run cannot change a run already in flight");
        // OccurrenceAt is half the correlation key (UNIQUE (ScheduleId, OccurrenceAt)); every test uses the same
        // Occurrence constant, so a wrong binding in the hand-written INSERT would still produce consistent
        // ON CONFLICT matches and every other test would pass regardless. Roles is the authorization identity
        // frozen at claim time, hand-encoded here via string.Join(',', ...) while ScheduledRunExecutionConfiguration
        // independently declares the same delimiter - nothing pins the two together, and the column-drift guard
        // compares column NAMES only, so a future encoding change (JSON, a different delimiter, ...) would pass
        // that guard while silently corrupting the roles a detached run executes under. Order matters too:
        // Should().Equal (not BeEquivalentTo) fails if a delimiter change reordered the roles.
        row.OccurrenceAt.Should().Be(Occurrence);
        row.Roles.Should().Equal("reader", "writer");
        (await OutboxRowsAsync<RunScoutStep>()).Should().ContainSingle();
    }

    [Fact]
    public async Task Redelivering_the_same_ScheduledRunDue_creates_exactly_one_execution()
    {
        // the spec's central claim. The unique key does this, not the handler's diligence.
        var store = Store();
        var due = new ScheduledRunDue(_scheduleId, Occurrence);

        var first = await store.TryBeginAsync(due, default);
        var second = await store.TryBeginAsync(due, default);

        first.Should().BeTrue();
        second.Should().BeFalse("the row already exists; ON CONFLICT DO NOTHING inserted nothing");
        (await ExecutionCountAsync()).Should().Be(1);
        (await OutboxRowsAsync<RunScoutStep>()).Should().ContainSingle(
            "a second scout step would run the subagent again and bill for it");
    }

    [Fact]
    public async Task Begin_for_a_schedule_that_no_longer_exists_is_handled_not_thrown()
    {
        // permanent condition: retrying cannot make a deleted row reappear
        var began = await Store().TryBeginAsync(new ScheduledRunDue(Guid.CreateVersion7(), Occurrence), default);

        began.Should().BeFalse();
        (await ExecutionCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Completing_the_scout_persists_findings_and_enqueues_the_writer_step()
    {
        var store = Store();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;

        var advanced = await store.TryCompleteScoutAsync(id, "three open PRs", default);

        advanced.Should().BeTrue();
        var row = await SingleExecutionAsync();
        row.Step.Should().Be(RunStep.Writer);
        row.Findings.Should().Be("three open PRs");
        (await OutboxRowsAsync<RunWriterStep>()).Should().ContainSingle();
    }

    [Fact]
    public async Task Redelivering_a_step_command_does_not_re_run_the_step()
    {
        var store = Store();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;
        await store.TryCompleteScoutAsync(id, "three open PRs", default);

        var again = await store.TryCompleteScoutAsync(id, "DIFFERENT findings", default);

        again.Should().BeFalse();
        (await SingleExecutionAsync()).Findings.Should().Be("three open PRs",
            "the persisted output of a completed step is what a resume reuses; overwriting it re-pays for it");
        (await OutboxRowsAsync<RunWriterStep>()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_crash_between_steps_resumes_at_the_persisted_step_reusing_its_output()
    {
        // "crash" is modelled as a brand-new store over the same database: nothing in memory survives
        await Store().TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;
        await Store().TryCompleteScoutAsync(id, "three open PRs", default);

        var afterRestart = Store();
        var row = await afterRestart.FindAsync(id, default);

        row!.Step.Should().Be(RunStep.Writer);
        row.Findings.Should().Be("three open PRs");
        (await afterRestart.TryCompleteScoutAsync(id, "re-scouted", default)).Should().BeFalse(
            "resuming must not re-pay for the scout stage");
    }

    [Fact]
    public async Task Delivery_marks_the_execution_Done_and_queues_the_channel_message()
    {
        var store = Store();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;
        await store.TryCompleteScoutAsync(id, "three open PRs", default);
        await store.TryCompleteWriterAsync(id, "Here is your digest.", default);

        var delivered = await store.TryCompleteDeliveryAsync(id, default);

        delivered.Should().BeTrue();
        (await SingleExecutionAsync()).Step.Should().Be(RunStep.Done);
        var queued = await OutboxPayloadsAsync<ChannelMessageQueued>();
        queued.Should().ContainSingle();
        queued[0].ConversationId.Should().Be("123456");
        queued[0].Text.Should().Be("Here is your digest.");
    }

    [Fact]
    public async Task A_failure_to_write_the_channel_message_leaves_the_step_un_advanced()
    {
        // the atomicity claim for the terminal step: Done and the outbox row commit together or not at all
        var store = Store();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;
        await store.TryCompleteScoutAsync(id, "f", default);
        await store.TryCompleteWriterAsync(id, "d", default);

        var act = async () => await StoreWithFailingChannelWriter().TryCompleteDeliveryAsync(id, default);

        // Narrowed to what ThrowingChannelWriter actually throws: asserting the base Exception type would let an
        // unrelated regression (e.g. a NullReferenceException from a totally different bug) satisfy this test.
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await SingleExecutionAsync()).Step.Should().Be(RunStep.Deliver,
            "a digest marked delivered that was never queued is the one outcome with no recovery path");
    }

    [Fact]
    public async Task A_non_concurrency_failure_still_lets_FailAsync_queue_the_operator_notice_on_the_same_store()
    {
        // Fix round 3, the blocking finding: ChangeTracker.Clear() used to sit only in the
        // DbUpdateConcurrencyException catch. Any OTHER exception out of enqueueNext or SaveChangesAsync -
        // exactly what a broken channel writer provokes - propagated with the row still tracked as Modified and
        // Step already set to Done in memory, even though the transaction rolled back. The natural dispatcher
        // shape is: call TryCompleteDeliveryAsync, catch, call FailAsync on the SAME store instance. Without the
        // fix, FailAsync's read would resolve that STALE tracked instance by identity - not the row's true,
        // rolled-back Deliver step - see Step == Done, and return through its own terminal guard, and no operator
        // notice would ever be queued for a run that just failed. This is the regression this fix exists to
        // prevent, and per the review it must be seen to fail first (see the round-3 report).
        var store = StoreWithChannelWriterThatFailsOnce();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;
        await store.TryCompleteScoutAsync(id, "f", default);
        await store.TryCompleteWriterAsync(id, "d", default);

        var act = async () => await store.TryCompleteDeliveryAsync(id, default);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Same store instance, same ApplicationDbContext - exactly the shape a real dispatcher uses.
        await store.FailAsync(id, "channel adapter unreachable", default);

        var row = await SingleExecutionAsync();
        row.Step.Should().Be(RunStep.Failed,
            "FailAsync must see the row's true, rolled-back Deliver step to be allowed to fail it at all - a " +
            "stale tracked Done would have turned it away at the terminal guard instead");
        var queued = await OutboxPayloadsAsync<ChannelMessageQueued>();
        queued.Should().ContainSingle(
            "the operator notice must still be queued; a stale tracked Done would have made FailAsync return " +
            "through its terminal guard without queuing anything, defeating the standing rule that the operator " +
            "is always told something");
    }

    [Fact]
    public async Task Failing_a_step_records_the_error_and_queues_an_operator_notice()
    {
        // the channels design's standing rule: the operator is always told something
        var store = Store();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;

        await store.FailAsync(id, "provider returned 529", default);

        var row = await SingleExecutionAsync();
        row.Step.Should().Be(RunStep.Failed);
        row.LastError.Should().Be("provider returned 529");
        var queued = await OutboxPayloadsAsync<ChannelMessageQueued>();
        queued.Should().ContainSingle();
        queued[0].Text.Should().Contain("daily-digest").And.Contain("529");
    }

    [Fact]
    public async Task Two_pollers_racing_one_step_advance_it_exactly_once()
    {
        // Fix round 2: Task.WhenAll does not guarantee overlap. If the first call finished before the second
        // began, the second would see Step already past Scout and return false through the STEP CHECK, never
        // reaching the concurrency path - a race test that can pass without a race ever happening, indistinguishable
        // from a version with no xmin token at all. A deterministic rendezvous (same approach as
        // StoreThatRendezvousesBeforeFailing) forces both calls to have already read Step == Scout and mutated
        // their own tracked copy before either is allowed to write, so the loser can only be turned away by the
        // xmin concurrency check. Captured logs assert that directly: EventId 450 (LogLostStepRace) must appear,
        // and EventId 449 (LogStepAlreadyPast) - the step-check path - must not, or this test would still be
        // proving nothing beyond Task.WhenAll's ordering.
        var store = Store();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;

        var aReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggerProvider = new CapturingLoggerProvider();
        var storeA = StoreThatRendezvousesBeforeAdvancingScout(aReady, bReady.Task, loggerProvider);
        var storeB = StoreThatRendezvousesBeforeAdvancingScout(bReady, aReady.Task, loggerProvider);

        var results = await Task.WhenAll(
            storeA.TryCompleteScoutAsync(id, "a", default).AsTask(),
            storeB.TryCompleteScoutAsync(id, "b", default).AsTask());

        results.Count(r => r).Should().Be(1, "one wins; the other must no-op rather than throw or double-advance");
        (await OutboxRowsAsync<RunWriterStep>()).Should().ContainSingle();
        loggerProvider.Entries.Should().Contain(e => e.EventId.Id == 450,
            "the barrier forces both calls to have already read Step == Scout before either writes, so the loser " +
            "can only be turned away by the xmin concurrency check (LogLostStepRace), never by the step-check " +
            "branch - this is what proves the OCC guarantee rather than the redundant step check");
        loggerProvider.Entries.Should().NotContain(e => e.EventId.Id == 449,
            "if the step-check branch (LogStepAlreadyPast) fired at all, the two calls did not genuinely overlap");
    }

    [Fact]
    public async Task Two_dispatchers_racing_to_fail_the_same_execution_write_the_operator_notice_exactly_once()
    {
        // Fix round 1: FailAsync used to have no concurrency-race protection at all - a redelivered step command
        // and its original both failing the same execution would let one dispatcher's SaveChangesAsync throw
        // DbUpdateConcurrencyException uncaught. It self-healed (the loser's retry no-ops on the row.Step check),
        // but only by burning retry budget on an unhandled exception. This proves the fix: exactly one write wins,
        // the loser returns normally, and the execution still ends up Failed exactly once.
        var store = Store();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;

        // A deterministic rendezvous, not a sleep: both FailAsync calls signal readiness and wait for each other
        // INSIDE the outbox write, so both have already read and mutated the row (row.Fail(...) already applied,
        // both tracked as Modified) and are genuinely about to write before either takes Postgres's row lock. A
        // sleep-based "start both, hope they overlap" version would flake under CI's variable scheduling; this
        // does not, because neither side can proceed past the rendezvous until the other has already arrived at
        // the same point.
        var aReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeA = StoreThatRendezvousesBeforeFailing(aReady, bReady.Task);
        var storeB = StoreThatRendezvousesBeforeFailing(bReady, aReady.Task);

        var act = async () => await Task.WhenAll(
            storeA.FailAsync(id, "provider returned 529", default).AsTask(),
            storeB.FailAsync(id, "provider returned 529 (retry)", default).AsTask());

        await act.Should().NotThrowAsync("the loser must no-op rather than throw or double-notify");
        var row = await SingleExecutionAsync();
        row.Step.Should().Be(RunStep.Failed);
        var queued = await OutboxPayloadsAsync<ChannelMessageQueued>();
        queued.Should().ContainSingle("exactly one dispatcher wins the race and writes the operator notice");
    }

    [Fact]
    public void The_insert_statement_lists_every_mapped_column()
    {
        using var db = fixture.CreateContext();

        var mapped = db.Model.FindEntityType(typeof(ScheduledRunExecution))!
            .GetProperties()
            .Select(p => p.GetColumnName())
            .Where(c => !string.Equals(c, "xmin", StringComparison.Ordinal)) // system column; the database maintains it
            .ToHashSet(StringComparer.Ordinal);

        ScheduledRunExecutionStore.InsertColumnNames.ToHashSet(StringComparer.Ordinal)
            .Should().BeEquivalentTo(mapped,
                "TryBeginAsync writes this table with hand-written SQL; a property added to the entity without a " +
                "matching column here is inserted as NULL or rejected at run time, and only at run time");
    }

    /// <summary>Seeds a <see cref="ScheduledRun"/> via <see cref="ScheduledRun.Create"/>, due at <see cref="Occurrence"/>.</summary>
    private async Task<Guid> SeedScheduleAsync(
        string name, string channelId, string conversationId, string principalId, IReadOnlyList<string> roles)
    {
        var schedule = ScheduledRun.Create(
            name, "0 7 * * *", "RepoDigestSaga", channelId, conversationId, principalId, roles,
            ScheduleOrigin.Config, Occurrence).Value;

        await using var db = fixture.CreateDbContext();
        db.ScheduledRuns.Add(schedule);
        await db.SaveChangesAsync();
        return schedule.Id;
    }

    private async Task<ScheduledRunExecution> SingleExecutionAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.ScheduledRunExecutions.AsNoTracking().SingleAsync();
    }

    private async Task<int> ExecutionCountAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.ScheduledRunExecutions.CountAsync();
    }

    /// <summary>
    ///     Rows in the shared <c>OutboxMessages</c> table (see <see cref="ApplicationDbContext.OutboxMessages"/>)
    ///     whose type-name column matches <typeparamref name="T"/>'s full name — the same key ZeroAlloc.Outbox's
    ///     generated writer stamps every row with.
    /// </summary>
    private async Task<List<OutboxMessageEntity>> OutboxRowsAsync<T>()
    {
        var typeName = typeof(T).FullName;
        await using var db = fixture.CreateDbContext();
        return await db.OutboxMessages.AsNoTracking().Where(m => m.TypeName == typeName).ToListAsync();
    }

    /// <summary>
    ///     Deserializes every <typeparamref name="T"/> row's payload with the same <see cref="IOutboxSerializer"/>
    ///     the host registers (resolved from the last <see cref="ResolveStore"/> call), rather than assuming a
    ///     specific wire format in the test.
    /// </summary>
    private async Task<List<T>> OutboxPayloadsAsync<T>()
    {
        var serializer = _serializer
            ?? throw new InvalidOperationException($"{nameof(Store)}() must be called before reading outbox payloads.");
        var rows = await OutboxRowsAsync<T>();
        return rows.Select(r => serializer.Deserialize<T>(r.Payload)).ToList();
    }

    /// <summary>
    ///     A store resolved from its own scope, wired exactly like production: the real generated
    ///     <c>IOutboxWriter&lt;T&gt;</c> for each of the four message types shares the scope's single
    ///     <see cref="ApplicationDbContext"/> with the store itself, so the store's transactions actually enlist
    ///     the outbox writes. Each call builds an entirely separate DI container (and so an entirely separate
    ///     database connection) — the same "brand-new store" fiction the crash-resumption and racing-pollers tests
    ///     rely on to model two independent processes.
    /// </summary>
    private ScheduledRunExecutionStore Store() => ResolveStore();

    /// <summary>A store built the same real way as <see cref="Store"/>, except <c>IOutboxWriter&lt;ChannelMessageQueued&gt;</c> always throws.</summary>
    private ScheduledRunExecutionStore StoreWithFailingChannelWriter() =>
        ResolveStore(wrapChannelWriter: (_, _) => new ThrowingChannelWriter());

    /// <summary>
    ///     A store whose <c>IOutboxWriter&lt;ChannelMessageQueued&gt;</c> throws on its first call and delegates to
    ///     the real, DI-resolved writer on every call after that — for proving a non-concurrency exception (unlike
    ///     <see cref="ThrowingChannelWriter"/>'s permanent break) still leaves the store instance usable
    ///     afterward. <c>ScheduledRunExecutionStore</c> is constructed once per DI scope and this writer is one of
    ///     its constructor-injected dependencies, so the SAME <see cref="ThrowOnceThenDelegateChannelWriter"/>
    ///     instance backs every call this store makes - the first call throws, and the very next call (whichever
    ///     method makes it) delegates for real.
    /// </summary>
    private ScheduledRunExecutionStore StoreWithChannelWriterThatFailsOnce() =>
        ResolveStore(wrapChannelWriter: (real, _) => new ThrowOnceThenDelegateChannelWriter(real));

    /// <summary>
    ///     A store whose <c>IOutboxWriter&lt;ChannelMessageQueued&gt;</c> signals <paramref name="mySignal"/> and
    ///     awaits <paramref name="otherSignal"/> before performing the real write — see
    ///     <see cref="RendezvousChannelWriter"/> for why the rendezvous happens before, not after, the real write.
    ///     Used in pairs so two concurrent <c>FailAsync</c> calls are both forced to have already read and mutated
    ///     the row and be about to write before either is allowed to proceed, making them genuinely race for the
    ///     same row's <c>xmin</c> instead of merely running one after the other.
    /// </summary>
    private ScheduledRunExecutionStore StoreThatRendezvousesBeforeFailing(TaskCompletionSource mySignal, Task otherSignal) =>
        ResolveStore(wrapChannelWriter: (real, _) => new RendezvousChannelWriter(real, mySignal, otherSignal));

    /// <summary>
    ///     A store whose <c>IOutboxWriter&lt;RunWriterStep&gt;</c> signals <paramref name="mySignal"/> and awaits
    ///     <paramref name="otherSignal"/> before performing the real write — the same rendezvous shape as
    ///     <see cref="StoreThatRendezvousesBeforeFailing"/>, over the step-advance path instead of the failure path.
    ///     Used in pairs so two concurrent <c>TryCompleteScoutAsync</c> calls are both forced to have already read
    ///     <c>Step == Scout</c> and mutated their own tracked copy before either is allowed to write, so the loser
    ///     can only be turned away by the <c>xmin</c> concurrency check, never by the redundant step check.
    ///     <paramref name="loggerProvider"/> is shared across both stores in a pair so the test can assert which
    ///     path the loser actually took.
    /// </summary>
    private ScheduledRunExecutionStore StoreThatRendezvousesBeforeAdvancingScout(
        TaskCompletionSource mySignal, Task otherSignal, ILoggerProvider loggerProvider) =>
        ResolveStore(
            wrapWriterStepWriter: (real, _) => new RendezvousWriterStepWriter(real, mySignal, otherSignal),
            loggerProvider: loggerProvider);

    private ScheduledRunExecutionStore ResolveStore(
        Func<IOutboxWriter<ChannelMessageQueued>, IServiceProvider, IOutboxWriter<ChannelMessageQueued>>? wrapChannelWriter = null,
        Func<IOutboxWriter<RunWriterStep>, IServiceProvider, IOutboxWriter<RunWriterStep>>? wrapWriterStepWriter = null,
        ILoggerProvider? loggerProvider = null)
    {
        var services = new ServiceCollection();
        // AddLogging()'s default configuration adds no providers, so log calls are normally no-ops; a
        // CapturingLoggerProvider is added only for tests that need to assert on which internal branch fired
        // (see Two_pollers_racing_one_step_advance_it_exactly_once), not for every store.
        services.AddLogging(builder =>
        {
            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContextPool<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddOutbox(o => { })
            .WithEfCore<ApplicationDbContext>()
            .AddRunScoutStepOutbox()
            .AddRunWriterStepOutbox()
            .AddDeliverDigestOutbox()
            .AddChannelMessageQueuedOutbox();
        services.AddScoped<ScheduledRunExecutionStore>();

        if (wrapChannelWriter is not null)
        {
            // Registered after AddChannelMessageQueuedOutbox's own registration: DI resolves the last
            // registration for a service type, so this wins. The real, DI-resolved
            // ChannelMessageQueuedOutboxWriter (accessible here via InternalsVisibleTo) is passed in so the
            // wrapped write genuinely enlists in the ambient transaction, the same pattern
            // ScheduledRunStoreTests uses for its own rendezvous.
            services.AddTransient<IOutboxWriter<ChannelMessageQueued>>(sp =>
                wrapChannelWriter(
                    new ChannelMessageQueuedOutboxWriter(sp.GetRequiredService<IOutboxStore>(), sp.GetRequiredService<IOutboxSerializer>()),
                    sp));
        }

        if (wrapWriterStepWriter is not null)
        {
            // Same "registered after, real writer underneath" pattern as wrapChannelWriter above.
            services.AddTransient<IOutboxWriter<RunWriterStep>>(sp =>
                wrapWriterStepWriter(
                    new RunWriterStepOutboxWriter(sp.GetRequiredService<IOutboxStore>(), sp.GetRequiredService<IOutboxSerializer>()),
                    sp));
        }

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        var scope = provider.CreateAsyncScope();
        _disposables.Add(scope);
        _serializer = scope.ServiceProvider.GetRequiredService<IOutboxSerializer>();
        return scope.ServiceProvider.GetRequiredService<ScheduledRunExecutionStore>();
    }

    /// <summary>Simulates a channel outbox writer that is permanently broken — e.g. a serialization or database fault.</summary>
    private sealed class ThrowingChannelWriter : IOutboxWriter<ChannelMessageQueued>
    {
        public ValueTask WriteAsync(ChannelMessageQueued message, DbTransaction? transaction = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated failure writing the ChannelMessageQueued outbox row.");
    }

    /// <summary>Throws on its first call (simulating a transient channel fault), then delegates to the real writer on every call after that.</summary>
    private sealed class ThrowOnceThenDelegateChannelWriter(IOutboxWriter<ChannelMessageQueued> inner) : IOutboxWriter<ChannelMessageQueued>
    {
        private bool _hasThrown;

        public async ValueTask WriteAsync(ChannelMessageQueued message, DbTransaction? transaction = null, CancellationToken ct = default)
        {
            if (!_hasThrown)
            {
                _hasThrown = true;
                throw new InvalidOperationException("Simulated one-time (transient) failure writing the ChannelMessageQueued outbox row.");
            }

            await inner.WriteAsync(message, transaction, ct);
        }
    }

    /// <summary>
    ///     Signals readiness and waits for the paired writer's own signal BEFORE performing the real write. The
    ///     rendezvous must happen before the write, not after: <c>EfCoreOutboxStore</c> shares the calling
    ///     <see cref="ApplicationDbContext"/> (see <see cref="ScheduledRunExecutionStore"/>'s remarks), so its
    ///     internal <c>SaveChangesAsync</c> flushes the already-mutated <c>ScheduledRunExecution</c> row too — the
    ///     row lock is acquired inside the inner writer's write, not at <c>FailAsync</c>'s later explicit
    ///     <c>SaveChangesAsync</c>. Rendezvousing after the write would have the loser block on Postgres's row lock
    ///     INSIDE the write, unable to ever reach the signal the winner is waiting on — a deadlock. Rendezvousing
    ///     first guarantees both sides have already read and mutated the row and are about to write before either
    ///     takes the lock, so they race for real.
    /// </summary>
    private sealed class RendezvousChannelWriter(IOutboxWriter<ChannelMessageQueued> inner, TaskCompletionSource mySignal, Task otherSignal)
        : IOutboxWriter<ChannelMessageQueued>
    {
        public async ValueTask WriteAsync(ChannelMessageQueued message, DbTransaction? transaction = null, CancellationToken ct = default)
        {
            mySignal.TrySetResult();
            // A generous budget, not a happy-path timing assumption: if a regression turns one side away before
            // it ever reaches this write, the other would otherwise block forever and CI would report a job
            // timeout instead of a named, diagnosable assertion failure.
            await otherSignal.WaitAsync(_rendezvousTimeout);
            await inner.WriteAsync(message, transaction, ct);
        }
    }

    /// <summary>Same rendezvous shape as <see cref="RendezvousChannelWriter"/>, over <see cref="RunWriterStep"/> instead.</summary>
    private sealed class RendezvousWriterStepWriter(IOutboxWriter<RunWriterStep> inner, TaskCompletionSource mySignal, Task otherSignal)
        : IOutboxWriter<RunWriterStep>
    {
        public async ValueTask WriteAsync(RunWriterStep message, DbTransaction? transaction = null, CancellationToken ct = default)
        {
            mySignal.TrySetResult();
            await otherSignal.WaitAsync(_rendezvousTimeout);
            await inner.WriteAsync(message, transaction, ct);
        }
    }

    /// <summary>
    ///     An <see cref="ILoggerProvider"/> that records every log entry across every category into one shared
    ///     list, so a test can assert on which of two named outcomes a store actually logged (e.g.
    ///     <c>LogLostStepRace</c> vs. <c>LogStepAlreadyPast</c>) rather than only on the boolean result the public
    ///     API returns. Thread-safe: <see cref="Entries"/> is a <see cref="ConcurrentBag{T}"/> because the whole
    ///     point of the tests that use this is two stores logging concurrently from different threads.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<(string Category, EventId EventId, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentBag<(string, EventId, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add((category, eventId, formatter(state, exception)));
        }
    }
}
