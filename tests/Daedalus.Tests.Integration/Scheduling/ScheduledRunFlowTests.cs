using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using Thalos.Testing;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     End-to-end acceptance tests for the saga-free scheduling redesign: a due schedule becomes an execution row,
///     a scout run, a writer run, and finally a message queued to a channel. Drives the real
///     <see cref="ScheduledRunDueDispatcher"/>, <see cref="RunScoutStepDispatcher"/>,
///     <see cref="RunWriterStepDispatcher"/>, and <see cref="DeliverDigestDispatcher"/> over a real PostgreSQL
///     database, with a real <see cref="SubagentRunExecutor"/> and Thalos agent runtime behind a
///     <see cref="ScriptedChatClient"/> standing in for the model provider, and a fake <see cref="IChannelAdapter"/>
///     recording deliveries.
/// </summary>
/// <remarks>
///     These tests deliberately bypass <c>OutboxWorkerService</c>: each dispatcher is constructed directly and
///     invoked with the message it should receive, exactly as the brief's own sample dispatchers are written
///     against. The step commands' <c>ExecutionId</c> is read straight off the database rather than off an outbox
///     row's deserialized payload, because what is under test here is what each dispatcher does with a message it
///     is given, not the outbox's own delivery mechanics — those are Task 13's and phase 1.4's tests. The one
///     exception is the terminal <see cref="ChannelMessageQueued"/> row <see cref="DeliverDigestDispatcher"/>
///     causes the store to enqueue: that row is read back and fed to a real <see cref="ChannelMessageQueuedDispatcher"/>
///     so a test can assert on what actually reaches the channel adapter, not merely on what the store queued.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunFlowTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string TelegramChannelId = "telegram";
    private const string ConversationId = "482910337";
    private const string PrincipalId = "schedule:daedalus";

    // A sentinel embedded in a scout response and asserted for in the writer's own prompt: proves the writer
    // actually received the scout's findings (RepoDigestPrompts.WriterTask(execution.Findings!)), not merely
    // that some plausible-looking digest happened to come back. Distinctive enough that it cannot match by
    // accident against the writer's own scripted response or any other request text.
    private const string ScoutFindingsSentinel = "SCOUT-FINDINGS-SENTINEL-4f2c9e";

    private static readonly string[] Roles = ["reader", "writer"];
    private static readonly DateTime Occurrence = new(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 17, 7, 0, 5, TimeSpan.Zero));
    private readonly List<IAsyncDisposable> _disposables = [];
    private IOutboxSerializer? _serializer;
    private Guid _scheduleId;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _scheduleId = await SeedScheduleAsync("daily-digest", TelegramChannelId, ConversationId, PrincipalId, Roles);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_due_schedule_produces_a_digest_delivered_to_the_fake_adapter()
    {
        var scripted = new ScriptedChatClient();
        scripted.ThenText($"Scout findings: three open PRs, one failing CI run. {ScoutFindingsSentinel}");
        scripted.ThenText("Here is your digest: three PRs are open and one CI run needs attention.");
        var adapter = new RecordingChannelAdapter(TelegramChannelId);

        var store = BuildStore();
        await new ScheduledRunDueDispatcher(store).DispatchAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = await SingleExecutionIdAsync();

        var executor = BuildExecutor(scripted);
        await new RunScoutStepDispatcher(store, executor, NullLogger<RunScoutStepDispatcher>.Instance)
            .DispatchAsync(new RunScoutStep(id), default);
        await new RunWriterStepDispatcher(store, executor, NullLogger<RunWriterStepDispatcher>.Instance)
            .DispatchAsync(new RunWriterStep(id), default);
        await new DeliverDigestDispatcher(store, NullLogger<DeliverDigestDispatcher>.Instance)
            .DispatchAsync(new DeliverDigest(id), default);
        await DrainChannelMessageAsync(adapter);

        (await SingleExecutionRowAsync()).Step.Should().Be(RunStep.Done);
        adapter.DeliveredTexts.Should().ContainSingle()
            .Which.Should().Be("Here is your digest: three PRs are open and one CI run needs attention.");
        scripted.Requests.Should().HaveCount(2, "one scout call and one writer call, and nothing else");
        RequestText(scripted, 1).Should().Contain(ScoutFindingsSentinel,
            "the writer's own prompt must carry the scout's findings text, not merely produce a plausible digest");
    }

    [Fact]
    public async Task Redelivering_the_ScheduledRunDue_runs_the_subagents_once()
    {
        // The central claim of the redesign: at-least-once outbox delivery, at every one of the four steps,
        // must never re-run a subagent turn that has already completed. The scripted client's call count is
        // the only thing that can prove this - a delivery-only assertion would still pass if a step silently
        // re-ran and simply overwrote its own output before the next step read it.
        var scripted = new ScriptedChatClient();
        scripted.ThenText("Scout findings: nothing urgent.");
        scripted.ThenText("The repository is quiet today.");
        var adapter = new RecordingChannelAdapter(TelegramChannelId);

        var store = BuildStore();
        var due = new ScheduledRunDue(_scheduleId, Occurrence);
        var dueDispatcher = new ScheduledRunDueDispatcher(store);
        await dueDispatcher.DispatchAsync(due, default);
        await dueDispatcher.DispatchAsync(due, default); // redelivery of the trigger itself
        var id = await SingleExecutionIdAsync();

        var executor = BuildExecutor(scripted);
        var scoutDispatcher = new RunScoutStepDispatcher(store, executor, NullLogger<RunScoutStepDispatcher>.Instance);
        await scoutDispatcher.DispatchAsync(new RunScoutStep(id), default);
        await scoutDispatcher.DispatchAsync(new RunScoutStep(id), default); // redelivery of the scout step

        var writerDispatcher = new RunWriterStepDispatcher(store, executor, NullLogger<RunWriterStepDispatcher>.Instance);
        await writerDispatcher.DispatchAsync(new RunWriterStep(id), default);
        await writerDispatcher.DispatchAsync(new RunWriterStep(id), default); // redelivery of the writer step

        var deliverDispatcher = new DeliverDigestDispatcher(store, NullLogger<DeliverDigestDispatcher>.Instance);
        await deliverDispatcher.DispatchAsync(new DeliverDigest(id), default);
        await deliverDispatcher.DispatchAsync(new DeliverDigest(id), default); // redelivery of the deliver step
        await DrainChannelMessageAsync(adapter);

        scripted.Requests.Should().HaveCount(2,
            "every step command was delivered twice, but each subagent must still have run exactly once");
        (await ExecutionCountAsync()).Should().Be(1, "the redelivered ScheduledRunDue must not create a second execution");
        adapter.DeliveredTexts.Should().ContainSingle("the redelivered DeliverDigest must not queue a second channel message");
    }

    [Fact]
    public async Task A_failure_in_the_writer_step_delivers_a_failure_notice_naming_the_schedule()
    {
        var scripted = new ScriptedChatClient();
        scripted.ThenText("Scout findings: three open PRs.");
        scripted.ThenThrow(new InvalidOperationException("simulated provider outage"));
        var adapter = new RecordingChannelAdapter(TelegramChannelId);

        var store = BuildStore();
        await new ScheduledRunDueDispatcher(store).DispatchAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = await SingleExecutionIdAsync();

        var executor = BuildExecutor(scripted);
        await new RunScoutStepDispatcher(store, executor, NullLogger<RunScoutStepDispatcher>.Instance)
            .DispatchAsync(new RunScoutStep(id), default);
        var act = async () => await new RunWriterStepDispatcher(store, executor, NullLogger<RunWriterStepDispatcher>.Instance)
            .DispatchAsync(new RunWriterStep(id), default);

        await act.Should().NotThrowAsync("no dispatcher throws; a subagent failure ends in FailAsync, not an outbox retry");
        await DrainChannelMessageAsync(adapter);

        var row = await SingleExecutionRowAsync();
        row.Step.Should().Be(RunStep.Failed);
        row.LastError.Should().Contain("ProviderError");
        adapter.DeliveredTexts.Should().ContainSingle()
            .Which.Should().Contain("daily-digest").And.Contain("Writer");
        scripted.Requests.Should().HaveCount(2, "the scout ran once and the writer's single failing attempt ran once");
    }

    [Fact]
    public async Task A_restart_between_the_scout_and_writer_steps_resumes_without_re_running_the_scout()
    {
        // Models a host restart the way ScheduledRunExecutionStoreTests does: a brand-new store, a brand-new
        // executor (and therefore a brand-new Thalos agent runtime/session store), and brand-new dispatcher
        // instances resolved over the SAME database, so nothing about "the scout already ran" survives in
        // memory - only what the database persisted. The scripted client itself is NOT rebuilt: it stands in
        // for the external model provider, which is outside the restarting process, and its call count is what
        // proves the resumed run skipped the scout rather than merely happening to still show one delivery.
        var scripted = new ScriptedChatClient();
        scripted.ThenText("Scout findings: nothing urgent.");
        scripted.ThenText("The repository is quiet today.");
        var adapter = new RecordingChannelAdapter(TelegramChannelId);

        var storeBeforeRestart = BuildStore();
        await new ScheduledRunDueDispatcher(storeBeforeRestart)
            .DispatchAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = await SingleExecutionIdAsync();

        var executorBeforeRestart = BuildExecutor(scripted);
        await new RunScoutStepDispatcher(storeBeforeRestart, executorBeforeRestart, NullLogger<RunScoutStepDispatcher>.Instance)
            .DispatchAsync(new RunScoutStep(id), default);

        scripted.Requests.Should().HaveCount(1, "only the scout has run so far");
        (await SingleExecutionRowAsync()).Step.Should().Be(RunStep.Writer);

        // --- restart: everything below is constructed fresh, over the same database ---
        var storeAfterRestart = BuildStore();
        var executorAfterRestart = BuildExecutor(scripted);

        // A redelivered RunScoutStep arriving after the restart, before the writer step ever ran, must not
        // re-run the scout: the fresh dispatcher's pre-check reads the persisted Step (Writer, not Scout) off
        // the database, not off any in-memory state, since nothing in memory survived the "restart".
        await new RunScoutStepDispatcher(storeAfterRestart, executorAfterRestart, NullLogger<RunScoutStepDispatcher>.Instance)
            .DispatchAsync(new RunScoutStep(id), default);
        scripted.Requests.Should().HaveCount(1,
            "the resumed scout step must be a no-op; the scout has already completed and must not run again");

        await new RunWriterStepDispatcher(storeAfterRestart, executorAfterRestart, NullLogger<RunWriterStepDispatcher>.Instance)
            .DispatchAsync(new RunWriterStep(id), default);
        scripted.Requests.Should().HaveCount(2, "the writer runs exactly once, resuming normally after the restart");

        await new DeliverDigestDispatcher(storeAfterRestart, NullLogger<DeliverDigestDispatcher>.Instance)
            .DispatchAsync(new DeliverDigest(id), default);
        await DrainChannelMessageAsync(adapter);

        (await SingleExecutionRowAsync()).Step.Should().Be(RunStep.Done);
        adapter.DeliveredTexts.Should().ContainSingle().Which.Should().Be("The repository is quiet today.");
    }

    [Fact]
    public async Task The_delivered_text_is_the_writer_output_not_the_scout_findings()
    {
        var scripted = new ScriptedChatClient();
        scripted.ThenText("SCOUT-FINDINGS-MUST-NOT-BE-DELIVERED");
        scripted.ThenText("WRITER-DIGEST-IS-WHAT-GETS-DELIVERED");
        var adapter = new RecordingChannelAdapter(TelegramChannelId);

        var store = BuildStore();
        await new ScheduledRunDueDispatcher(store).DispatchAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = await SingleExecutionIdAsync();

        var executor = BuildExecutor(scripted);
        await new RunScoutStepDispatcher(store, executor, NullLogger<RunScoutStepDispatcher>.Instance)
            .DispatchAsync(new RunScoutStep(id), default);
        await new RunWriterStepDispatcher(store, executor, NullLogger<RunWriterStepDispatcher>.Instance)
            .DispatchAsync(new RunWriterStep(id), default);
        await new DeliverDigestDispatcher(store, NullLogger<DeliverDigestDispatcher>.Instance)
            .DispatchAsync(new DeliverDigest(id), default);
        await DrainChannelMessageAsync(adapter);

        var delivered = adapter.DeliveredTexts.Should().ContainSingle().Subject;
        delivered.Should().Be("WRITER-DIGEST-IS-WHAT-GETS-DELIVERED");
        delivered.Should().NotContain("SCOUT-FINDINGS-MUST-NOT-BE-DELIVERED");
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

    private async Task<ScheduledRunExecution> SingleExecutionRowAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.ScheduledRunExecutions.AsNoTracking().SingleAsync();
    }

    private async Task<Guid> SingleExecutionIdAsync() => (await SingleExecutionRowAsync()).Id;

    /// <summary>
    ///     The concatenated text of every <see cref="Microsoft.Extensions.AI.ChatMessage"/> in the
    ///     <paramref name="scripted"/> client's <paramref name="requestIndex"/>-th captured request - what the
    ///     model actually saw for that call, so a test can assert on the prompt content itself rather than only
    ///     on the scripted response that came back.
    /// </summary>
    private static string RequestText(ScriptedChatClient scripted, int requestIndex) =>
        string.Join('\n', scripted.Requests[requestIndex].Messages.Select(m => m.Text));

    private async Task<int> ExecutionCountAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.ScheduledRunExecutions.CountAsync();
    }

    /// <summary>
    ///     Reads the single <see cref="ChannelMessageQueued"/> outbox row written so far, deserializes it with the
    ///     same <see cref="IOutboxSerializer"/> the host registers, and feeds it to a real
    ///     <see cref="ChannelMessageQueuedDispatcher"/> over <paramref name="adapter"/> - the same last leg
    ///     phase 1.4 already wired and tested, given its first real writer by this phase.
    /// </summary>
    private async Task DrainChannelMessageAsync(IChannelAdapter adapter)
    {
        var serializer = _serializer
            ?? throw new InvalidOperationException($"{nameof(BuildStore)}() must be called before draining the channel outbox.");
        var typeName = typeof(ChannelMessageQueued).FullName;

        await using var db = fixture.CreateDbContext();
        var row = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.TypeName == typeName);
        var message = serializer.Deserialize<ChannelMessageQueued>(row.Payload);

        var dispatcher = new ChannelMessageQueuedDispatcher([adapter], NullLogger<ChannelMessageQueuedDispatcher>.Instance);
        await dispatcher.DispatchAsync(message, default);
    }

    /// <summary>
    ///     A store resolved from its own scope, wired like production: the real generated
    ///     <c>IOutboxWriter&lt;T&gt;</c> for each of the four message types shares the scope's single
    ///     <see cref="ApplicationDbContext"/> with the store itself. Each call builds an entirely separate DI
    ///     container - the same "brand-new store" fiction <c>ScheduledRunExecutionStoreTests</c> uses to model an
    ///     independent process, which is what the restart test relies on.
    /// </summary>
    private ScheduledRunExecutionStore BuildStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContextPool<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddOutbox(o => { })
            .WithEfCore<ApplicationDbContext>()
            .AddRunScoutStepOutbox()
            .AddRunWriterStepOutbox()
            .AddDeliverDigestOutbox()
            .AddChannelMessageQueuedOutbox();
        services.AddScoped<ScheduledRunExecutionStore>();

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        var scope = provider.CreateAsyncScope();
        _disposables.Add(scope);
        _serializer = scope.ServiceProvider.GetRequiredService<IOutboxSerializer>();
        return scope.ServiceProvider.GetRequiredService<ScheduledRunExecutionStore>();
    }

    /// <summary>
    ///     A real <see cref="SubagentRunExecutor"/> over a real Thalos agent runtime (session store, agent
    ///     factory, event hub - all wired by <c>AddThalos</c> exactly as production does), with only the chat
    ///     client swapped for <paramref name="scripted"/> via a minimal <see cref="IChatClientProvider"/>. Every
    ///     call builds an entirely separate DI container, mirroring <see cref="BuildStore"/>; <paramref name="scripted"/>
    ///     itself is passed in rather than created here; see the restart test for why.
    /// </summary>
    private SubagentRunExecutor BuildExecutor(ScriptedChatClient scripted)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThalos(thalos =>
        {
            thalos.UseChatClientProvider(new ScriptedChatClientProvider(scripted));
            thalos.UseInMemorySessionStore();
            thalos.AddAgent(new AgentDefinition { Id = AgentId.New(), Name = RepoDigestPrompts.ScoutAgent, Instructions = "You are the scout." });
            thalos.AddAgent(new AgentDefinition { Id = AgentId.New(), Name = RepoDigestPrompts.WriterAgent, Instructions = "You are the writer." });
        });

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);

        var runner = provider.GetRequiredService<ISubagentRunner>();
        var catalog = provider.GetRequiredService<IAgentCatalog>();
        var options = Options.Create(new DetachedRunOptions
        {
            PrincipalId = PrincipalId,
            Roles = Roles,
            MaxTotalTokens = 50_000,
            DeadlineSeconds = 300,
        });

        return new SubagentRunExecutor(runner, catalog, options, NullLogger<SubagentRunExecutor>.Instance);
    }

    /// <summary>Hands every agent the same scripted client, standing in for the real model provider.</summary>
    private sealed class ScriptedChatClientProvider(Microsoft.Extensions.AI.IChatClient client) : IChatClientProvider
    {
        public string Name => "scripted";

        public string DefaultModel => "scripted-model";

        public Microsoft.Extensions.AI.IChatClient CreateChatClient(AgentDefinition agent) => client;
    }

    /// <summary>Fake <see cref="IChannelAdapter"/> recording every delivered message's text.</summary>
    private sealed class RecordingChannelAdapter(string channelId) : IChannelAdapter
    {
        public string ChannelId { get; } = channelId;

        public List<string> DeliveredTexts { get; } = [];

        public ValueTask DeliverAsync(Thalos.ConversationId conversationId, AgentEvent agentEvent, CancellationToken ct)
        {
            DeliveredTexts.Add(((TextDeltaEvent)agentEvent).Text);
            return ValueTask.CompletedTask;
        }
    }
}
