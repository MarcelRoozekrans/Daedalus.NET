# Phase 1.5 Plan B — Daedalus: sagas, schedules, and the outbox writer

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Daedalus run agents autonomously — a cron-fired saga orchestrates subagent steps durably, compensates on failure, and pushes its result to Telegram through the outbox phase 1.4 left without a writer.

**Architecture:** `ZeroAlloc.Scheduling` fires a per-minute sweeper. The sweeper claims due `ScheduledRuns` rows and — in one transaction — advances `NextRunAt` and writes a `ScheduledRunDue` outbox row. That starts a `[Saga]`, whose steps call Thalos 0.5.0's `ISubagentRunner` and whose terminal step writes `ChannelMessageQueued` to the existing channel outbox. Saga state persists via `ZeroAlloc.Saga.EfCore` in the Daedalus database.

**Tech Stack:** .NET 10, EF Core 10 + PostgreSQL, `ZeroAlloc.Saga` 2.0.0, `ZeroAlloc.Saga.EfCore` 1.3.0, `ZeroAlloc.Saga.Outbox` 2.0.0, `ZeroAlloc.Scheduling` 1.2.46, `ZeroAlloc.Scheduling.EfCore` 1.2.46, `ZeroAlloc.Outbox` 2.5.2, Cronos, Thalos.NET 0.5.0. Tests: xunit, Testcontainers.

**Spec:** `docs/plans/2026-09-16-thalos-subagents-design.md`

**Depends on:** Plan A merged and **Thalos.NET 0.5.0 live on nuget.org**. Task 1 verifies this.

## Global Constraints

- **Thalos.NET 0.5.0** — `ISubagentRunner`, `SubagentRunRequest`, `SubagentBudget` come from the package, not from source.
- **Time comes from `TimeProvider`, injected.** Never `DateTime.UtcNow` or `DateTimeOffset.UtcNow` in scheduling, cron evaluation, deadline or catch-up code. Spec §7.
- **Domain stays framework-free.** `Daedalus.Domain` references no EF Core, no Thalos, no ZeroAlloc.Saga. Entities derive from `Entity<Guid>`, validate through a `static Result<T> Create(...)` factory using `CSharpFunctionalExtensions`, and store the `Guid` backing Thalos typed ids — never the 26-character rendered form.
- **ArchUnit rules are enforced.** `Domain`/`Application` must not reference `Thalos.*` or `Daedalus.Agents`; `Daedalus.Agents` must not reference `Api`. Adding a reference that breaks these fails the architecture tests — restructure rather than relax the rule.
- **Stores are singletons taking `IDbContextFactory<ApplicationDbContext>`** and creating a fresh short-lived context per call. Follow `PostgresConversationMap`.
- **Migrations:** `dotnet ef migrations add <Name> --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations`. Apply with `dotnet run --project src/Daedalus.Migrations`. **EF 10 throws on `Migrate()` when the model has pending changes not in the snapshot** — always regenerate with `migrations add`, never hand-edit the snapshot.
- **A host must call both `AddDaedalusAgents` and `AddDaedalusChannels`.** Only the former wires the channel outbox. This plan adds two more hosted services; Task 12 pins the whole set.
- **Never reintroduce `IConversationMap.GetBySessionAsync`.** A schedule carries its delivery target; it does not look one up.
- **Conventional commits, free scopes.** Commit bodies must not contain nested parentheses — release-please drops such commits silently.

## File Structure

| File | Responsibility |
|---|---|
| `src/Daedalus.Domain/Entities/ScheduledRun.cs` | Create — the aggregate, validation, `AdvanceTo` |
| `src/Daedalus.Infrastructure/Persistence/Configurations/ScheduledRunConfiguration.cs` | Create — table, index, `RowVersion` |
| `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs` | Modify — add `DbSet<ScheduledRun>` |
| `src/Daedalus.Agents/Scheduling/ScheduledRunDue.cs` | Create — the outbox trigger message |
| `src/Daedalus.Agents/Scheduling/ScheduledRunStore.cs` | Create — claim, advance, reconcile |
| `src/Daedalus.Agents/Scheduling/ScheduleSweeperJob.cs` | Create — the `[Job]` |
| `src/Daedalus.Agents/Scheduling/ScheduleReconciler.cs` | Create — startup config → table, with validation |
| `src/Daedalus.Agents/Scheduling/ScheduledRunDueDispatcher.cs` | Create — outbox row → saga start |
| `src/Daedalus.Agents/Sagas/RunSubagentCommand.cs` | Create — command, event, handler |
| `src/Daedalus.Agents/Sagas/RepoDigestSaga.cs` | Create — the first workflow |
| `src/Daedalus.Agents/Sagas/DeliverDigestCommand.cs` | Create — terminal step → channel outbox |
| `src/Daedalus.Agents/DaedalusSchedulingServiceCollectionExtensions.cs` | Create — one entry point for saga + scheduling |
| `src/Daedalus.Api/appsettings.json` | Modify — `ScheduledRuns` and `DetachedRuns` sections |

Scheduling and saga code are separate folders under `Daedalus.Agents` because they change for different reasons: scheduling changes when triggering changes, sagas change when a workflow changes.

---

### Task 1: De-risk the `Saga.EfCore` / `Saga` version skew

**This runs before any feature work.** `ZeroAlloc.Saga.EfCore` 1.3.0 declares `ZeroAlloc.Saga >= 1.3.0`; `ZeroAlloc.Saga` is at 2.0.0 after a release marked **BREAKING CHANGES: release the generator fix from #95**. NuGet resolves the constraint happily, so this *builds* — that pairing has simply never shipped together. `Saga.EfCore` 1.3.0 also pins `Microsoft.EntityFrameworkCore.Relational` 9.0.4 while Daedalus is on EF Core 10.

If the generator change broke the 1.3.0-era store, that reshapes the phase. Finding out costs an hour now and a rewrite at Task 9.

**Files:**
- Create: `spikes/SagaEfCoreSmoke/` — **throwaway**, deleted in step 6

**Interfaces:** none — nothing downstream consumes this.

- [x] **Step 1: Confirm Thalos.NET 0.5.0 is live**

Run: `curl -s https://api.nuget.org/v3-flatcontainer/thalos.net/index.json | tail -c 200`
Expected: `0.5.0` present. If absent, **stop** — plan A is not finished and every later task depends on it.

- [x] **Step 2: Create a throwaway console project**

```bash
mkdir -p spikes/SagaEfCoreSmoke && cd spikes/SagaEfCoreSmoke
dotnet new console -f net10.0
dotnet add package ZeroAlloc.Saga --version 2.0.0
dotnet add package ZeroAlloc.Saga.EfCore --version 1.3.0
dotnet add package ZeroAlloc.Saga.Outbox --version 2.0.0
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

Do **not** add this to the solution — it must not reach CI.

- [x] **Step 3: Write the smallest saga that proves the store works**

```csharp
// A two-step saga over the EfCore store. The point is not the workflow, it is that
// the 2.0.0 generator's output still satisfies the 1.3.0 store's interfaces at runtime.
[Saga]
public partial class SmokeSaga
{
    public Guid CorrelationId { get; private set; }

    [CorrelationKey] public Guid Correlation(SmokeStarted e)  => e.Id;
    [CorrelationKey] public Guid Correlation(SmokeAdvanced e) => e.Id;

    [Step(Order = 1)]
    public AdvanceCommand Begin(SmokeStarted e) { CorrelationId = e.Id; return new AdvanceCommand(e.Id); }

    [Step(Order = 2)]
    public FinishCommand Finish(SmokeAdvanced e) => new(e.Id);
}
```

Wire it with `services.AddSaga().WithSmokeSaga()` plus the EfCore store, point it at a local Postgres, publish `SmokeStarted`, and assert the saga reaches a terminal state and its row is removed.

Read `ZeroAlloc.Saga.EfCore`'s own README before writing the wiring — the fluent call for selecting the EfCore store is named there, and guessing it wastes a cycle.

- [x] **Step 4: Run it against a real Postgres**

Run: `docker run --rm -d -p 5433:5432 -e POSTGRES_PASSWORD=spike --name saga-spike postgres:16`
then `dotnet run`.
Expected: the saga completes and its row is gone.

- [x] **Step 5: Record the verdict**

Write the outcome into `docs/plans/2026-09-16-saga-efcore-spike.md` — versions tried, whether it worked, and any API that differed from the README. **This file is kept**; the spike project is not.

If it **failed**, stop and report. Options are: pin `ZeroAlloc.Saga` to 1.3.0 and forgo the 2.0.0 generator fix; raise a fix upstream in the ZeroAlloc-Net org and wait for a `Saga.EfCore` 2.x; or fall back to the InMemory store and accept that sagas do not survive a restart, which would contradict spec D1 and needs your decision.

- [x] **Step 6: Delete the spike project, keep the note**

```bash
docker rm -f saga-spike
rm -rf spikes/SagaEfCoreSmoke
git add docs/plans/2026-09-16-saga-efcore-spike.md
git commit -m "docs(spike): record the Saga 2.0.0 and Saga.EfCore 1.3.0 compatibility result"
```

---

### Task 2: Baseline every test project

Spec §7. Phase 1.4's retrospective names this omission as what hid a 126-test Playwright failure for an entire plan. A baseline scoped to "the projects this work touches" is how that happened.

**Files:**
- Create: `docs/plans/2026-09-16-plan-b-baseline.md`

**Interfaces:** none.

- [x] **Step 1: Enumerate every test project**

Run: `dotnet sln list | grep -i test`
Expected: seven projects, including `Daedalus.Tests.Playwright.Api` and `Daedalus.Tests.Playwright.Browser`.

- [x] **Step 2: Run each one separately and record the result**

```bash
for p in $(dotnet sln list | grep -i 'tests'); do
  echo "=== $p ==="
  dotnet test "$p" --nologo 2>&1 | tail -3
done
```

Record pass/fail/skip per project in the baseline file. Run each **separately** — a single root `dotnet test` masks which project a failure came from.

- [x] **Step 3: Record the known-failing suites explicitly**

Two failures are expected and are **not** this plan's to fix:

- `Daedalus.Tests.Playwright.Api` — 126/126 failing. `E2EServerFixture.GlobalSetupAsync()` touches `_factory.Services`, starting the host including Thalos's `SkillSyncService` querying the `Skills` table, before its own `EnsureCreatedAsync()` creates the schema. Pre-existing since phase 1.3; `ci.yml` excludes `~Playwright` from both test steps, which is why it is invisible.
- `AuthenticationFlowTests` — 9 failing when a `traefik` container holds `localhost:8080`. Environmental. Note whether it is holding the port on this machine right now: `docker ps --filter publish=8080`.

Write both into the baseline with their counts. **The purpose is attribution:** at review time, these must be provably pre-existing rather than something phase 1.5 broke.

- [x] **Step 4: Commit the baseline**

```bash
git add docs/plans/2026-09-16-plan-b-baseline.md
git commit -m "docs(baseline): record test state before plan B"
```

---

### Task 3: The `ScheduledRun` aggregate

**Files:**
- Create: `src/Daedalus.Domain/Entities/ScheduledRun.cs`
- Test: `tests/Daedalus.Tests.Unit.Domain/ScheduledRunTests.cs`

**Interfaces:**
- Produces:
  - `ScheduledRun.Create(string name, string cron, string trigger, string channelId, string conversationId, string principalId, IReadOnlyList<string> roles, ScheduleOrigin origin, DateTime nextRunAtUtc) -> Result<ScheduledRun>`
  - Properties: `Name`, `Cron`, `Trigger`, `ChannelId`, `ConversationId`, `PrincipalId`, `Roles`, `Origin`, `NextRunAt`, `LastRunAt`, `Enabled`, `MissedOccurrences`
  - `void AdvanceTo(DateTime nextRunAtUtc, DateTime lastRunAtUtc, int missed)`
  - `void Disable()`, `void UpdateFromConfig(...)`
  - `enum ScheduleOrigin { Config, Agent }`

- [x] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain;

public class ScheduledRunTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_returns_an_enabled_config_row_with_its_first_occurrence()
    {
        var result = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "123456",
            "schedule:daily-digest", ["reader"], ScheduleOrigin.Config, Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.Enabled.Should().BeTrue();
        result.Value.NextRunAt.Should().Be(Now);
        result.Value.MissedOccurrences.Should().Be(0);
        result.Value.Origin.Should().Be(ScheduleOrigin.Config);
    }

    [Theory]
    [InlineData("", "0 7 * * *", "name")]
    [InlineData("daily-digest", "", "cron")]
    public void Create_rejects_blank_required_fields(string name, string cron, string because)
    {
        var result = ScheduledRun.Create(
            name, cron, "RepoDigestSaga", "telegram", "123456",
            "schedule:x", ["reader"], ScheduleOrigin.Config, Now);

        result.IsFailure.Should().BeTrue(because);
    }

    [Fact]
    public void Create_rejects_an_empty_conversation_id()
    {
        // unlike ChannelConversation, a schedule with no delivery target is useless: there is no
        // live turn to fall back on, so the digest would run and have nowhere to go
        var result = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "",
            "schedule:x", ["reader"], ScheduleOrigin.Config, Now);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void AdvanceTo_moves_the_next_occurrence_and_records_the_run()
    {
        var run = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "123456",
            "schedule:x", ["reader"], ScheduleOrigin.Config, Now).Value;

        run.AdvanceTo(Now.AddDays(1), Now, missed: 0);

        run.NextRunAt.Should().Be(Now.AddDays(1));
        run.LastRunAt.Should().Be(Now);
    }

    [Fact]
    public void AdvanceTo_accumulates_missed_occurrences()
    {
        var run = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "123456",
            "schedule:x", ["reader"], ScheduleOrigin.Config, Now).Value;

        run.AdvanceTo(Now.AddDays(1), Now, missed: 3);
        run.AdvanceTo(Now.AddDays(2), Now.AddDays(1), missed: 2);

        run.MissedOccurrences.Should().Be(5, "the count is cumulative, for observability over time");
    }
}
```

- [x] **Step 2: Run and verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit.Domain --filter FullyQualifiedName~ScheduledRunTests`
Expected: FAIL — the type does not exist.

- [x] **Step 3: Write the aggregate**

Follow `ChannelConversation` exactly for shape: `sealed class ... : Entity<Guid>`, private setters, a static `Create` returning `Result<T>` from `CSharpFunctionalExtensions`, `const int Max*Length` fields, XML docs on every public member. Roles are stored as a `IReadOnlyList<string>` and persisted as a delimited string by the EF configuration in Task 4.

Key rules in `Create`:
- `Name`, `Cron`, `Trigger`, `ChannelId`, `ConversationId`, `PrincipalId` all non-blank; **`ConversationId` non-blank here, unlike `ChannelConversation`** — a schedule with no delivery target cannot deliver, and the empty-string case that `ChannelConversation` allows exists only for the console channel's live binding.
- Length caps: `Name` 64, `Cron` 128, `Trigger` 128, `ChannelId` 32 reusing `ChannelConversation.MaxChannelIdLength`, `ConversationId` 128, `PrincipalId` 128.
- `Roles` non-empty — a principal with no roles can do nothing, so it is a configuration error, not a valid narrow principal.
- **Do not validate the cron expression here.** Domain stays framework-free and Cronos is a library; cron parsing happens in the reconciler (Task 7).

`AdvanceTo` sets `NextRunAt`, `LastRunAt`, and does `MissedOccurrences += missed`.

- [x] **Step 4: Run and verify it passes**

Run: `dotnet test tests/Daedalus.Tests.Unit.Domain --filter FullyQualifiedName~ScheduledRunTests`
Expected: PASS, 6 tests.

- [x] **Step 5: Commit**

```bash
git add src/Daedalus.Domain tests/Daedalus.Tests.Unit.Domain
git commit -m "feat(domain): add the ScheduledRun aggregate"
```

---

### Task 4: Persist `ScheduledRuns`

**Files:**
- Create: `src/Daedalus.Infrastructure/Persistence/Configurations/ScheduledRunConfiguration.cs`
- Modify: `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs`
- Create: migration `AddScheduledRuns`
- Test: `tests/Daedalus.Tests.Unit.Infrastructure/ScheduledRunConfigurationTests.cs`

**Interfaces:**
- Produces: `ApplicationDbContext.ScheduledRuns` as `DbSet<ScheduledRun>`; table `ScheduledRuns`; unique index `IX_ScheduledRun_Name`

- [x] **Step 1: Write the configuration**

Model it on `ChannelConversationConfiguration`:

```csharp
builder.ToTable("ScheduledRuns");
builder.HasKey(r => r.Id);

builder.Property(r => r.Name).IsRequired().HasMaxLength(ScheduledRun.MaxNameLength);
builder.Property(r => r.Cron).IsRequired().HasMaxLength(ScheduledRun.MaxCronLength);
builder.Property(r => r.Trigger).IsRequired().HasMaxLength(ScheduledRun.MaxTriggerLength);
builder.Property(r => r.ChannelId).IsRequired().HasMaxLength(ScheduledRun.MaxChannelIdLength);
builder.Property(r => r.ConversationId).IsRequired().HasMaxLength(ScheduledRun.MaxConversationIdLength);
builder.Property(r => r.PrincipalId).IsRequired().HasMaxLength(ScheduledRun.MaxPrincipalIdLength);
builder.Property(r => r.Origin).IsRequired().HasConversion<string>().HasMaxLength(16);
builder.Property(r => r.NextRunAt).IsRequired();
builder.Property(r => r.LastRunAt);
builder.Property(r => r.Enabled).IsRequired();
builder.Property(r => r.MissedOccurrences).IsRequired();

// Roles is a small fixed list of short tokens; a delimited column avoids a join table for data that is
// always read whole and never queried by element.
builder.Property(r => r.Roles)
    .IsRequired()
    .HasMaxLength(512)
    .HasConversion(
        v => string.Join(',', v),
        v => v.Split(',', StringSplitOptions.RemoveEmptyEntries))
    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
        (a, b) => a!.SequenceEqual(b!),
        v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
        v => v.ToList()));

// Name is the natural key reconciliation matches on; enforced by the database, not by convention,
// so the reconciler's upsert can rely on it.
builder.HasIndex(r => r.Name).IsUnique().HasDatabaseName("IX_ScheduledRun_Name");

// The sweeper's hot query is "enabled and due"; this index serves it directly.
builder.HasIndex(r => new { r.Enabled, r.NextRunAt }).HasDatabaseName("IX_ScheduledRun_Enabled_NextRunAt");

// Optimistic concurrency for the claim in Task 6. xmin is Postgres's own system column — no extra
// column, and it is updated by the database rather than by the application.
builder.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");
```

The `ValueComparer` is required: without it EF cannot detect changes to a converted collection and the roles column silently never updates.

- [x] **Step 2: Add the `DbSet`**

In `ApplicationDbContext`, beside `ChannelConversations`:

```csharp
    /// <summary>Recurring autonomous runs; see <see cref="ScheduledRun"/>.</summary>
    public DbSet<ScheduledRun> ScheduledRuns => Set<ScheduledRun>();
```

- [x] **Step 3: Generate the migration**

Run:
```bash
dotnet ef migrations add AddScheduledRuns --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations
```

Read the generated `Up` before continuing. Expected: one `CreateTable` and two `CreateIndex`. If it contains changes to any other table, the model has drifted — stop and find out why rather than applying it.

- [x] **Step 4: Apply and verify against a real database**

Run: `dotnet run --project src/Daedalus.Migrations`
Then confirm: `docker exec -it <postgres-container> psql -U postgres -d daedalus -c '\d "ScheduledRuns"'`
Expected: the table with both indexes.

If Postgres complains about a collation version mismatch, that is the known local issue — `docker volume rm daedalus_postgres_data` or `REINDEX DATABASE daedalus;`.

- [x] **Step 5: Write a round-trip test**

```csharp
[Fact]
public async Task A_ScheduledRun_round_trips_including_its_roles()
{
    await using var db = CreateContext();           // existing test fixture
    var run = ScheduledRun.Create(
        "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "123456",
        "schedule:daily-digest", ["reader", "digest"], ScheduleOrigin.Config,
        new DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc)).Value;

    db.ScheduledRuns.Add(run);
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();

    var loaded = await db.ScheduledRuns.SingleAsync(r => r.Name == "daily-digest");
    loaded.Roles.Should().BeEquivalentTo(["reader", "digest"]);
    loaded.Origin.Should().Be(ScheduleOrigin.Config);
}
```

Use whatever context fixture `tests/Daedalus.Tests.Unit.Infrastructure` already uses. If that project is SQLite- or in-memory-backed, put this test in `Daedalus.Tests.Integration` instead — the `xmin` row version is Postgres-specific.

Run it. Expected: PASS.

- [x] **Step 6: Commit**

```bash
git add src/Daedalus.Infrastructure tests
git commit -m "feat(persistence): add the ScheduledRuns table"
```

---

### Task 5: `ScheduledRunDue` and its outbox wiring

> **PARTIALLY SUPERSEDED (2026-09-17).** Steps 1 and 2 stand — `ScheduledRunDue` and
> `ScheduleOccurrence` are unchanged. Steps 3 and 4 are replaced: this solution has no mediator, so
> the dispatcher does not publish a saga start event. See Task 15 of
> `docs/plans/2026-09-17-scheduled-runs-plan-b-continuation.md`.

**Files:**
- Create: `src/Daedalus.Agents/Scheduling/ScheduledRunDue.cs`
- Create: `src/Daedalus.Agents/Scheduling/ScheduledRunDueDispatcher.cs`
- Test: `tests/Daedalus.Tests.Unit/Scheduling/ScheduledRunDueDispatcherTests.cs`

**Interfaces:**
- Produces:
  - `record ScheduledRunDue(Guid ScheduleId, DateTime OccurrenceAtUtc)` marked `[OutboxMessage]`
  - Generated `IOutboxWriter<ScheduledRunDue>` and `AddScheduledRunDueOutbox()`
  - `ScheduledRunDueDispatcher : IOutboxDispatcher<ScheduledRunDue>`
  - `readonly record struct ScheduleOccurrence(Guid ScheduleId, DateTime OccurrenceAtUtc)` — the saga correlation key

- [x] **Step 1: Create the message**

```csharp
using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     A scheduled run became due. Written by <see cref="ScheduleSweeperJob"/> inside the same transaction that
///     advances the row's <c>NextRunAt</c>, so the advance and the trigger commit together or not at all — a crash
///     between them can neither lose the run nor fire it twice.
/// </summary>
/// <remarks>
///     Outbox delivery is at-least-once, so this message can arrive more than once. That is safe by construction:
///     <see cref="Sagas.RepoDigestSaga"/> correlates on <see cref="ScheduleOccurrence"/>, which is
///     <paramref name="ScheduleId"/> plus <paramref name="OccurrenceAtUtc"/>, so a redelivery resolves to the saga
///     instance already running rather than starting a second one. Idempotency comes from the saga's correlation
///     key, not from a dedupe table here.
/// </remarks>
[OutboxMessage]
public sealed record ScheduledRunDue(Guid ScheduleId, DateTime OccurrenceAtUtc);
```

The generated DI extension is `Add{TypeName}Outbox` — `AddScheduledRunDueOutbox()`. This is `ZeroAlloc.Outbox`'s `OutboxCodeWriter.DiExtensionMethodName` formatting `$"Add{typeName}Outbox"`, as `ChannelMessageQueued` documents.

- [x] **Step 2: Create the correlation key type**

```csharp
namespace Daedalus.Agents.Scheduling;

/// <summary>One firing of one schedule. The saga correlation key; two deliveries of the same occurrence correlate here.</summary>
public readonly record struct ScheduleOccurrence(Guid ScheduleId, DateTime OccurrenceAtUtc);
```

- [x] **Step 3: Write the failing dispatcher test**

```csharp
[Fact]
public async Task Dispatching_publishes_a_saga_start_event_for_the_occurrence()
{
    var mediator = Substitute.For<IMediator>();
    var dispatcher = new ScheduledRunDueDispatcher(mediator, NullLogger<ScheduledRunDueDispatcher>.Instance);
    var message = new ScheduledRunDue(ScheduleId, new DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc));

    await dispatcher.DispatchAsync(message, CancellationToken.None);

    await mediator.Received(1).Publish(
        Arg.Is<ScheduledRunDue>(e => e.ScheduleId == ScheduleId), Arg.Any<CancellationToken>());
}
```

`ZeroAlloc.Mediator` generates an internal `IMediator` per assembly. Check how `Daedalus.Agents` already obtains one — if it cannot be substituted, introduce a thin `IScheduleTriggerPublisher` the dispatcher depends on, and test that instead. Look at how `IAgentNotificationPublisher` solves the same problem before inventing anything.

- [x] **Step 4: Run, implement, re-run**

Implement `ScheduledRunDueDispatcher` to publish the saga start event. Follow `ChannelMessageQueuedDispatcher`'s error policy exactly: a **permanent** condition — an unknown schedule id — is logged at `Error` and treated as handled, never thrown, because retrying cannot make a deleted row reappear and throwing only burns the retry budget before dead-lettering something that could never succeed.

Run: `dotnet test tests/Daedalus.Tests.Unit --filter FullyQualifiedName~ScheduledRunDueDispatcherTests`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/Daedalus.Agents tests/Daedalus.Tests.Unit
git commit -m "feat(scheduling): add the ScheduledRunDue outbox trigger"
```

---

### Task 6: `ScheduledRunStore` — the atomic claim

This is the correctness core of the phase. Spec §5.

**Files:**
- Create: `src/Daedalus.Agents/Scheduling/ScheduledRunStore.cs`
- Test: `tests/Daedalus.Tests.Integration/Scheduling/ScheduledRunStoreTests.cs`

**Interfaces:**
- Produces: `ScheduledRunStore(IDbContextFactory<ApplicationDbContext>, IOutboxWriter<ScheduledRunDue>, TimeProvider, ILogger<ScheduledRunStore>)` with `ValueTask<int> ClaimAndEnqueueDueAsync(CancellationToken ct)` returning the number of runs fired

- [x] **Step 1: Write the failing tests**

```csharp
public class ScheduledRunStoreTests : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_due_row_advances_and_enqueues_exactly_one_trigger()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));

        var fired = await StoreWith(time).ClaimAndEnqueueDueAsync(default);

        fired.Should().Be(1);
        (await LoadAsync("daily-digest")).NextRunAt.Should().Be(At(7, 0).AddDays(1));
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_row_that_is_not_due_is_left_alone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 6, 59, 0, TimeSpan.Zero));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));

        var fired = await StoreWith(time).ClaimAndEnqueueDueAsync(default);

        fired.Should().Be(0);
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_row_never_fires()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "off", cron: "0 7 * * *", nextRunAt: At(7, 0), enabled: false);

        (await StoreWith(time).ClaimAndEnqueueDueAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task Three_missed_days_fire_once_and_record_the_skipped_count()
    {
        // spec D8: yesterday's digest has no value today, and catch-up-all would bill three
        // subagent runs to deliver two documents nobody wants
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));

        var fired = await StoreWith(time).ClaimAndEnqueueDueAsync(default);

        fired.Should().Be(1);
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().ContainSingle();
        var row = await LoadAsync("daily-digest");
        row.MissedOccurrences.Should().Be(3);
        row.NextRunAt.Should().Be(new DateTime(2026, 9, 20, 7, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task A_failure_writing_the_outbox_leaves_NextRunAt_untouched()
    {
        // the atomicity claim: advance and trigger commit together or not at all
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));
        var store = StoreWithFailingOutbox(time);

        var act = async () => await store.ClaimAndEnqueueDueAsync(default);

        await act.Should().ThrowAsync<Exception>();
        (await LoadAsync("daily-digest")).NextRunAt.Should().Be(At(7, 0),
            "a crash between advancing and enqueueing must not consume the occurrence");
    }
}
```

`PostgresFixture` — reuse the Testcontainers fixture `Daedalus.Tests.Integration` already has. Read that project first.

- [x] **Step 2: Run and verify they fail**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ScheduledRunStoreTests`
Expected: FAIL — the store does not exist.

- [x] **Step 3: Implement the store**

```csharp
public async ValueTask<int> ClaimAndEnqueueDueAsync(CancellationToken ct)
{
    var now = _time.GetUtcNow().UtcDateTime;
    await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

    var due = await db.ScheduledRuns
        .Where(r => r.Enabled && r.NextRunAt <= now)
        .ToListAsync(ct).ConfigureAwait(false);

    var fired = 0;
    foreach (var run in due)
    {
        var occurrence = run.NextRunAt;
        var (next, missed) = NextOccurrence(run.Cron, occurrence, now);

        run.AdvanceTo(next, now, missed);
        await _outbox.WriteAsync(new ScheduledRunDue(run.Id, occurrence), tx.GetDbTransaction(), ct)
                     .ConfigureAwait(false);
        fired++;
    }

    await db.SaveChangesAsync(ct).ConfigureAwait(false);
    await tx.CommitAsync(ct).ConfigureAwait(false);
    return fired;
}
```

`NextOccurrence` uses Cronos to walk forward from the due occurrence to the first time **after** `now`, counting how many it skipped:

```csharp
private static (DateTime Next, int Missed) NextOccurrence(string cron, DateTime occurrence, DateTime now)
{
    var expression = CronExpression.Parse(cron);
    var cursor = occurrence;
    var missed = 0;

    while (true)
    {
        var candidate = expression.GetNextOccurrence(cursor, TimeZoneInfo.Utc);
        if (candidate is null) return (DateTime.MaxValue, missed);   // a one-shot cron with no future
        if (candidate > now) return (candidate.Value, missed);
        cursor = candidate.Value;
        missed++;
    }
}
```

Confirm `IOutboxWriter<T>.WriteAsync`'s transaction parameter against `ZeroAlloc.Outbox` 2.5.2 — `ChannelMessageQueued`'s XML docs say it takes one, but check the actual signature and the `using Microsoft.EntityFrameworkCore.Storage;` needed for `GetDbTransaction()`.

**Do not "improve" this into a per-row transaction.** One transaction per sweep is what makes the last test pass: if the outbox write throws on row three, rows one and two must roll back too, or those occurrences are consumed with no trigger.

- [x] **Step 4: Run and verify all five pass**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ScheduledRunStoreTests`
Expected: PASS, 5 tests.

- [x] **Step 5: Run them five times**

Run: `for i in 1 2 3 4 5; do dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ScheduledRunStoreTests || echo "FAILED run $i"; done`
Expected: five clean runs. Transactional tests against containers are exactly where intermittent failures hide.

- [x] **Step 6: Commit**

```bash
git add src/Daedalus.Agents tests/Daedalus.Tests.Integration
git commit -m "feat(scheduling): claim due runs and enqueue their triggers atomically"
```

---

### Task 7: `ScheduleReconciler` — config into the table, with validation

> **PATCHED (2026-09-17).** `Trigger` no longer names a saga class. The configured value becomes
> `"RepoDigest"` and the validation checks a known-workflow set; `DetachedRuns` also gains
> `MaxTotalTokens` and `DeadlineSeconds`. See Task 17 step 1 of
> `docs/plans/2026-09-17-scheduled-runs-plan-b-continuation.md`. Everything else stands.

**Files:**
- Create: `src/Daedalus.Agents/Scheduling/ScheduleReconciler.cs`
- Modify: `src/Daedalus.Api/appsettings.json`
- Test: `tests/Daedalus.Tests.Integration/Scheduling/ScheduleReconcilerTests.cs`

**Interfaces:**
- Produces: `ScheduleReconciler.ReconcileAsync(CancellationToken) -> ValueTask`, throwing `InvalidOperationException` on invalid configuration; `ScheduledRunOptions` bound from `ScheduledRuns`

- [x] **Step 1: Add the configuration sections**

```json
"DetachedRuns": {
  "PrincipalId": "schedule:daedalus",
  "Roles": [ "reader" ]
},
"ScheduledRuns": [
  {
    "Name": "daily-digest",
    "Cron": "0 7 * * *",
    "Trigger": "RepoDigestSaga",
    "ChannelId": "telegram",
    "ConversationId": "REPLACE_WITH_CHAT_ID",
    "Enabled": false
  }
]
```

`Enabled: false` by default: the Telegram path has never been exercised end to end, so a fresh clone must not start pushing to an unverified chat id on a timer.

- [x] **Step 2: Write the failing tests**

```csharp
[Fact]
public async Task Config_rows_are_inserted_on_first_run()
[Fact]
public async Task Config_rows_are_updated_in_place_on_the_second_run_and_not_duplicated()
[Fact]
public async Task A_config_row_that_disappears_from_configuration_is_disabled_not_deleted()
[Fact]
public async Task Agent_origin_rows_are_never_touched_by_config_reconciliation()
[Fact]
public async Task An_unparseable_cron_expression_throws_at_startup()
[Fact]
public async Task A_trigger_naming_no_registered_saga_throws_at_startup()
```

Write each body out. The fourth is the one that protects phase 1.6 — seed a row with `Origin.Agent` and a name absent from configuration, reconcile, and assert it is still enabled and unchanged. Without it, 1.6's agent-created schedules get disabled on every restart.

- [x] **Step 3: Implement**

Upsert by `Name` for `Origin == Config` rows only. Disable — never delete — config rows missing from configuration, so history and `MissedOccurrences` survive. Validate before writing anything:

- `CronExpression.Parse(cron)` for every row; a `CronFormatException` becomes an `InvalidOperationException` naming the schedule and the expression.
- Every `Trigger` resolves to a registered saga name; an unknown one throws, naming it and listing the registered ones.

Spec §7 requires the boot to fail rather than defer the error to 07:00.

- [x] **Step 4: Wire it to run at startup**

An `IHostedService` that calls `ReconcileAsync` in `StartAsync`, registered ahead of the sweeper. A throw here must stop the host — that is the point.

- [x] **Step 5: Run the tests**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ScheduleReconcilerTests`
Expected: PASS, 6 tests.

- [x] **Step 6: Commit**

```bash
git add src/Daedalus.Agents src/Daedalus.Api tests/Daedalus.Tests.Integration
git commit -m "feat(scheduling): reconcile configured schedules and validate them at startup"
```

---

### Task 8: Validate agent names at startup

Spec §7 — the thread carried since phase 1.4. `DefaultAgent` is an agent **name**, not an id; `AgentId` is ULID-backed with no string constructor. `DefaultAgentConfigurationTests` pins each host's `appsettings.json`, but nothing validates at boot.

**Files:**
- Create: `src/Daedalus.Agents/AgentNameValidator.cs`
- Test: `tests/Daedalus.Tests.Integration/AgentNameValidationTests.cs`

**Interfaces:**
- Produces: `AgentNameValidator.Validate(IAgentCatalog, IEnumerable<string> names)` throwing `InvalidOperationException` listing every unknown name

- [x] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task A_host_configured_with_an_unknown_DefaultAgent_fails_to_start()
[Fact]
public async Task A_saga_referencing_an_unknown_agent_name_fails_to_start()
[Fact]
public async Task The_error_names_every_unknown_agent_at_once_not_just_the_first()
```

The third matters: failing on the first unknown name means a misconfigured host is fixed one restart at a time.

- [x] **Step 2: Implement and wire into the same startup hosted service as Task 7**

Resolve names case-insensitively — `ChannelPump` already does, because agent names are typed by humans on phones.

- [x] **Step 3: Run, then commit**

```bash
git add src/Daedalus.Agents tests/Daedalus.Tests.Integration
git commit -m "feat(agents): validate configured agent names at startup"
```

---

### Task 9: `RunSubagentCommand` and its handler

> **SUPERSEDED (2026-09-17).** `IRequest<Unit>` and `INotification` are mediator abstractions that
> do not exist in this solution. The single-`ISubagentRunner`-seam intent survives as
> `ISubagentRunExecutor`; see Task 14 of
> `docs/plans/2026-09-17-scheduled-runs-plan-b-continuation.md`. Do not implement this task.

**Files:**
- Create: `src/Daedalus.Agents/Sagas/RunSubagentCommand.cs`
- Test: `tests/Daedalus.Tests.Unit/Sagas/RunSubagentHandlerTests.cs`

**Interfaces:**
- Produces:
  - `record RunSubagentCommand(ScheduleOccurrence Occurrence, string AgentName, string Task) : IRequest<Unit>`
  - `record SubagentFinished(ScheduleOccurrence Occurrence, string AgentName, string Text) : INotification`
  - `record SubagentFailed(ScheduleOccurrence Occurrence, string AgentName, AgentErrorCode Code, string Message) : INotification`
  - `RunSubagentHandler` — **the only type in Daedalus that touches `ISubagentRunner`**

- [x] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task A_successful_run_publishes_SubagentFinished_with_the_text()
[Fact]
public async Task A_failed_run_publishes_SubagentFailed_carrying_the_error_code()
[Fact]
public async Task The_run_executes_as_the_configured_detached_principal_not_the_creator()
[Fact]
public async Task An_unknown_agent_name_publishes_SubagentFailed_rather_than_throwing()
```

The third is spec D7 and §4 made falsifiable — assert the `ISecurityContext` handed to `ISubagentRunner` has `Id == "schedule:daedalus"` and `Roles` exactly `["reader"]`, not a human's identity. Substitute `ISubagentRunner` and capture the request.

- [x] **Step 2: Implement**

Resolve `AgentName` to an `AgentId` through `IAgentCatalog` case-insensitively. Build the caller as `new ConfiguredSecurityContext(options.PrincipalId, options.Roles)` from the `DetachedRuns` section — **never** from any ambient user context. Call `ISubagentRunner.RunAsync`. Publish `SubagentFinished` on success, `SubagentFailed` on failure.

The handler **never throws** on an agent error: a throw escapes into the saga's step dispatch as an infrastructure fault, while a `SubagentFailed` notification is a domain outcome the saga can compensate on. These are different things and conflating them loses the compensation path.

- [x] **Step 3: Run, then commit**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter FullyQualifiedName~RunSubagentHandlerTests`
Expected: PASS, 4 tests.

```bash
git add src/Daedalus.Agents tests/Daedalus.Tests.Unit
git commit -m "feat(sagas): run a subagent as the configured detached principal"
```

---

> **SUPERSEDED (2026-09-17).** Tasks 10–13 below are replaced in full by
> `docs/plans/2026-09-17-scheduled-runs-plan-b-continuation.md`, which implements the approved
> saga-free design. Do not implement them. They are kept for the record of what the saga approach
> would have been.

### Task 10: `RepoDigestSaga` — SUPERSEDED

**Files:**
- Create: `src/Daedalus.Agents/Sagas/RepoDigestSaga.cs`
- Create: `src/Daedalus.Agents/Sagas/DeliverDigestCommand.cs`
- Test: `tests/Daedalus.Tests.Integration/Sagas/RepoDigestSagaTests.cs`

**Interfaces:**
- Consumes: `ScheduledRunDue`, `SubagentFinished`, `SubagentFailed`, `RunSubagentCommand`
- Produces: `RepoDigestSaga`; `DeliverDigestCommand(ScheduleOccurrence, string ChannelId, string ConversationId, string Text)`; generated `WithRepoDigestSaga()`

- [ ] **Step 1: Write the saga**

```csharp
[Saga]
public partial class RepoDigestSaga
{
    public ScheduleOccurrence Occurrence { get; private set; }
    public string ChannelId { get; private set; } = "";
    public string ConversationId { get; private set; } = "";
    public string Findings { get; private set; } = "";

    [CorrelationKey] public ScheduleOccurrence Correlation(ScheduledRunDue e)   => new(e.ScheduleId, e.OccurrenceAtUtc);
    [CorrelationKey] public ScheduleOccurrence Correlation(SubagentFinished e)  => e.Occurrence;
    [CorrelationKey] public ScheduleOccurrence Correlation(SubagentFailed e)    => e.Occurrence;

    [Step(Order = 1, Compensate = nameof(ReportFailure))]
    public RunSubagentCommand Sweep(ScheduledRunDue e)
    {
        Occurrence = new ScheduleOccurrence(e.ScheduleId, e.OccurrenceAtUtc);
        return new RunSubagentCommand(Occurrence, "scout", SweepPrompt);
    }

    [Step(Order = 2, Compensate = nameof(ReportFailure), CompensateOn = typeof(SubagentFailed))]
    public RunSubagentCommand Summarise(SubagentFinished e)
    {
        Findings = e.Text;
        return new RunSubagentCommand(Occurrence, "writer", SummarisePrompt(Findings));
    }

    [Step(Order = 3)]
    public DeliverDigestCommand Deliver(SubagentFinished e) =>
        new(Occurrence, ChannelId, ConversationId, e.Text);

    public ReportFailureCommand ReportFailure() => new(Occurrence, ChannelId, ConversationId);
}
```

`ChannelId` and `ConversationId` have to reach the saga. `ScheduledRunDue` deliberately carries only ids, so step 1 loads them from the `ScheduledRuns` row. If `[Step]` methods cannot do I/O in this version of `ZeroAlloc.Saga`, widen `ScheduledRunDue` to carry `ChannelId` and `ConversationId` and set them in `Sweep` — **check the library's constraints before choosing**, and record which you picked and why in the commit body.

- [ ] **Step 2: Write the delivery command handler**

`DeliverDigestCommand`'s handler writes `ChannelMessageQueued` to the channel outbox — **this is the writer phase 1.4 has been waiting for**:

```csharp
await _outbox.WriteAsync(
    new ChannelMessageQueued(command.ChannelId, command.ConversationId, command.Text), transaction, ct);
```

`ReportFailureCommand`'s handler does the same with an explanatory text. Spec §4: *the digest arrives, or an explanation does.* A saga that compensates silently reintroduces, in a place with no live turn to notice it, exactly the bug plan A's Task 1 fixes.

Write both through the `ZeroAlloc.Saga.Outbox` bridge so the outbox row commits with the saga state save.

- [ ] **Step 3: Write the integration tests**

```csharp
[Fact]
public async Task A_due_schedule_produces_a_digest_delivered_to_the_fake_adapter()
[Fact]
public async Task Redelivering_the_same_ScheduledRunDue_runs_the_subagents_once()
[Fact]
public async Task A_failure_in_the_summarise_step_delivers_a_failure_notice()
[Fact]
public async Task The_saga_resumes_after_the_host_restarts_mid_workflow()
```

All four against Testcontainers Postgres with `ScriptedChatClient` from `Thalos.NET.Testing` and a fake `IChannelAdapter` recording deliveries. The second proves correlation-key idempotency — the spec's central claim. The fourth proves durability, which is the entire justification for choosing sagas in D1; without it, nothing distinguishes this from an in-memory workflow.

- [ ] **Step 4: Run, then run five times**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~RepoDigestSagaTests`
then the same in a loop of five.
Expected: PASS every time.

- [ ] **Step 5: Commit**

```bash
git add src/Daedalus.Agents tests/Daedalus.Tests.Integration
git commit -m "feat(sagas): add RepoDigestSaga and give the channel outbox its first writer"
```

---

### Task 11: The sweeper job and one DI entry point

**Files:**
- Create: `src/Daedalus.Agents/Scheduling/ScheduleSweeperJob.cs`
- Create: `src/Daedalus.Agents/DaedalusSchedulingServiceCollectionExtensions.cs`
- Test: `tests/Daedalus.Tests.Unit/Scheduling/ScheduleSweeperJobTests.cs`

**Interfaces:**
- Produces: `ScheduleSweeperJob : IJob` with `[Job(Every = Every.Minute)]`; `AddDaedalusScheduling(this IServiceCollection, IConfiguration)`

- [ ] **Step 1: Write the job**

```csharp
/// <summary>Fires every schedule that has come due. All the work is in <see cref="ScheduledRunStore"/>.</summary>
[Job(Every = Every.Minute)]
public sealed partial class ScheduleSweeperJob : IJob
{
    public async ValueTask ExecuteAsync(JobContext ctx, CancellationToken ct)
    {
        var fired = await _store.ClaimAndEnqueueDueAsync(ct).ConfigureAwait(false);
        if (fired > 0) LogFired(_logger, fired);
    }
}
```

Thin on purpose: `ScheduledRunStore` is already covered by transactional integration tests, and logic here would need the scheduler running to exercise.

- [ ] **Step 2: Write the DI extension**

```csharp
public static IServiceCollection AddDaedalusScheduling(this IServiceCollection services, IConfiguration configuration)
{
    services.AddSaga()
            .WithEfCore<ApplicationDbContext>()
            .WithOutbox()
            .WithRepoDigestSaga();

    services.AddScheduling()
            .WithEfCoreStore<ApplicationDbContext>()
            .AddScheduleSweeperJob();

    services.AddScheduledRunDueOutbox();
    services.AddSingleton<ScheduledRunStore>();
    services.AddHostedService<ScheduleStartupValidator>();   // Tasks 7 + 8
    return services;
}
```

Verify every fluent method name against each library's README — `WithEfCore` / `WithEfCoreStore` / `WithOutbox` are from the docs, not from the compiler, and the two libraries do not necessarily spell it the same way.

- [ ] **Step 3: Test the job delegates and logs**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter FullyQualifiedName~ScheduleSweeperJobTests`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Daedalus.Agents tests/Daedalus.Tests.Unit
git commit -m "feat(scheduling): add the sweeper job and one scheduling entry point"
```

---

### Task 12: Host wiring — pin the hosted-service set

Spec §6. Before this phase a host ran the outbox worker and crash recovery. It now also runs the saga store worker, the scheduling worker, and the startup validator. `AddOutbox()` uses a plain `AddHostedService`, not `TryAdd`, so a careless call order produces duplicate pollers contending on one `DbContext`.

**Files:**
- Modify: `src/Daedalus.Api/Program.cs`, `src/Daedalus.Cli/Program.cs`
- Test: `tests/Daedalus.Tests.Integration/ApiHostSchedulingWiringTests.cs`

**Interfaces:** none new.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void The_api_host_registers_exactly_one_of_each_background_worker()
{
    using var host = RealApiHost();           // boots the real host, as ApiHostChannelWiringTests does

    var hosted = host.Services.GetServices<IHostedService>().ToList();

    hosted.Count(h => h is OutboxWorkerService).Should().Be(1, "two pollers would double-dispatch");
    hosted.Count(h => h is SchedulingWorkerService).Should().Be(1);
    hosted.Count(h => h is ScheduleStartupValidator).Should().Be(1);
    hosted.Should().ContainSingle(h => h is AgentSessionCrashRecovery);
}

[Fact]
public void The_cli_host_registers_the_same_set()
```

Model this on the existing `ApiHostChannelWiringTests`, which already boots the real host — read it first and follow its construction rather than inventing a second approach.

- [ ] **Step 2: Wire both hosts and make the tests pass**

Each host calls `AddDaedalusAgents`, `AddDaedalusChannels` **and** `AddDaedalusScheduling`. If two workers of a type appear, fix the registration — do not relax the assertion.

- [ ] **Step 3: Run the whole integration suite**

Run: `dotnet test tests/Daedalus.Tests.Integration`
Expected: green except the 9 environmental `AuthenticationFlowTests` from the Task 2 baseline, and only if `traefik` still holds port 8080.

- [ ] **Step 4: Commit**

```bash
git add src tests
git commit -m "feat(hosts): wire scheduling into the api and cli hosts"
```

---

### Task 13: Architecture rules and the full verification pass

**Files:**
- Modify: `tests/Daedalus.Tests.Unit/Architecture/` — the existing ArchUnit rules
- Modify: `docs/planning/STATE.md` via `pause-work`, not by hand

- [ ] **Step 1: Add architecture rules**

Three rules, matching the three added in phase 1.4:

1. `Daedalus.Domain` must not reference `ZeroAlloc.Saga`, `ZeroAlloc.Scheduling` or `Cronos` — cron parsing lives in the reconciler precisely so this holds.
2. Only `RunSubagentHandler` may reference `ISubagentRunner` — the single-seam claim from Task 9, enforced rather than documented.
3. `Daedalus.Agents.Sagas` must not reference `Daedalus.Api`.

- [ ] **Step 2: Run every test project separately again**

```bash
for p in $(dotnet sln list | grep -i 'tests'); do
  echo "=== $p ==="
  dotnet test "$p" --nologo 2>&1 | tail -3
done
```

Compare against the Task 2 baseline. **Every delta must be explained.** New passes are the point; any new failure blocks the phase. The two known-failing suites must still fail with the *same* counts — a change there means this plan touched something it should not have.

- [ ] **Step 3: Run the new suites five times**

Run the loop over `Daedalus.Tests.Integration` and `Daedalus.Tests.Unit`. Five clean runs each.

- [ ] **Step 4: Verify the real thing actually runs**

Start the AppHost and watch one schedule fire end to end with a `ConversationId` you control.

```bash
dotnet run --project src/Daedalus.AppHost
```

Set a schedule to `Enabled: true` with a cron a minute or two out. Confirm in order: the sweeper claims the row; `NextRunAt` advances; a `ScheduledRunDue` outbox row appears and is dispatched; the saga runs two subagents; a `ChannelMessageQueued` row appears; the adapter delivers.

Phase 1.4's retrospective is explicit that several defects were caught only because someone **ran** something rather than reading it. Do not skip this step because the integration tests are green.

If Aspire appears to hang on start, check for orphaned `dcp.exe` or dashboard processes from a killed run holding ports. If Keycloak ignores a realm change, `docker rm -f daedalus-realm-*` — Aspire reuses the existing container.

- [ ] **Step 5: Pre-push review**

Run the `pre-push-review` skill against the branch. Phase 1.4's review found four pieces of dead API and a test failing 4 runs in 7 while reported green.

- [ ] **Step 6: Finish the branch**

Use `superpowers:finishing-a-development-branch`. Then `complete-phase` marks 1.5 complete in ROADMAP.md and MILESTONE.md, and `pause-work` writes STATE.md.

Do **not** hand-edit the planning files — those sub-skills write and verify them.

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §5 — `ScheduledRuns` table, all columns | Tasks 3, 4 |
| §5 — `Origin` protects future agent rows | Tasks 3, 7 |
| §5 — claim-advance-enqueue in one transaction | Task 6 |
| §5 — correlation-key idempotency | Tasks 5, 10 |
| §5 / D8 — missed occurrences fire once | Tasks 3, 6 |
| §6 — `RepoDigestSaga`, three steps, compensation | Task 10 |
| §6 — `RunSubagentCommand` is the only `ISubagentRunner` seam | Tasks 9, 13 |
| §6 — outbox writer, in the saga's transaction | Task 10 |
| §6 — wiring hazard, four pollers | Task 12 |
| §4 / D7 — narrow detached principal | Tasks 7, 9 |
| §4 — failure notices over the same path | Task 10 |
| §7 — startup validation of cron, trigger, agent names | Tasks 7, 8 |
| §7 — `TimeProvider` everywhere | Global Constraints; Tasks 6, 7 |
| §7 — `Saga.EfCore` spike first | Task 1 |
| §7 — baseline every project | Tasks 2, 13 |
| §7 — run suites repeatedly | Tasks 6, 10, 13 |

**Type consistency:** `ScheduleOccurrence(Guid ScheduleId, DateTime OccurrenceAtUtc)` is constructed identically in Tasks 5, 6, 9 and 10. `ScheduledRunDue(Guid, DateTime)` matches between the producer in Task 6 and the saga in Task 10. `RunSubagentCommand(ScheduleOccurrence, string AgentName, string Task)` has one shape in Tasks 9 and 10. `ScheduledRunStore.ClaimAndEnqueueDueAsync` returns `int` in Tasks 6 and 11.

**Three places where the plan defers to the library over itself**, because they were written from READMEs rather than from compiled code — each says so at the point of use:

1. `IOutboxWriter<T>.WriteAsync`'s transaction parameter and the `GetDbTransaction()` import (Task 6 step 3).
2. Whether `[Step]` methods may do I/O, which decides if `ScheduledRunDue` must carry the delivery target (Task 10 step 1).
3. The fluent wiring names `WithEfCore` / `WithEfCoreStore` / `WithOutbox` (Task 11 step 2).

**One ordering risk, called out rather than buried:** Task 1's spike can invalidate Tasks 10–12. That is exactly why it is Task 1 and why its step 5 lists the fallback options instead of assuming success.
