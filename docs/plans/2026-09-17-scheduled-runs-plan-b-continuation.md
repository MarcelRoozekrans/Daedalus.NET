# Phase 1.5 Plan B — continuation: scheduled runs without `ZeroAlloc.Saga`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Drive a scheduled run from "cron fired" to "the digest arrived in Telegram" through the outbox alone — one durable row per occurrence, one outbox message per step — and give `ChannelMessageQueued` the writer it has been waiting for since phase 1.4.

**Architecture:** A `ScheduledRunExecutions` row is created once per occurrence under `UNIQUE (ScheduleId, OccurrenceAt)`; that unique key is where idempotency lives. Each step is an `[OutboxMessage]` with an `IOutboxDispatcher<T>`. A step handler runs its subagent, then in **one transaction** persists the output, advances `Step`, and enqueues the next command. The terminal step writes `ChannelMessageQueued` in that same transaction.

**Tech Stack:** .NET 10, EF Core 10 + PostgreSQL, `ZeroAlloc.Outbox` 2.5.2 + `ZeroAlloc.Outbox.EfCore`, `ZeroAlloc.Scheduling` + `.EfCore`, `ZeroAlloc.Authorization`, `ZeroAlloc.Results`, Cronos, **Thalos.NET 0.5.0**. Tests: xunit, NSubstitute, AwesomeAssertions, Testcontainers.

**Spec:** `docs/plans/2026-09-17-scheduled-runs-without-saga-design.md` (approved 2026-09-17), which supersedes §6 of `docs/plans/2026-09-16-thalos-subagents-design.md`. §4 identity, §5 scheduling and §7 errors/testing in that older document still stand and are still binding.

---

## What this document replaces

It replaces **Tasks 10, 11, 12 and 13** of `docs/plans/2026-09-16-thalos-subagents-plan-b.md` outright, and **patches Tasks 5, 7 and 9** of that plan. Tasks 1–4, 6 and 8 stand unchanged. Task 1 is complete — verdict FAIL, which is why this document exists.

Numbering continues from that plan, so "Task 10" here is the task that follows its Task 9.

### Corrections found while grounding this plan against the code

The approved design's *Impact on plan B* table says Tasks 2–9 are unaffected. Three of them are affected, and the reasons are not cosmetic. Each was found by reading the repository and the shipped packages rather than the READMEs.

1. **Plan B Tasks 5 and 9 are written in mediator terms — and there is no mediator in this solution.**
   Task 5's `ScheduledRunDueDispatcher` test substitutes `IMediator`; Task 9 declares `RunSubagentCommand : IRequest<Unit>` and `SubagentFinished : INotification`. `grep` over `src/` finds no `IMediator`, no `IRequest<`, no `INotification`, and no `ZeroAlloc.Mediator` package reference anywhere. `Daedalus.Application` hand-rolls CQRS with `ICommand`, `ICommandHandler` and `CommandQueryHandlerFactory`; the roadmap lists *CQRS→Mediator* as **phase 1.7** work, still pending. Those abstractions were written from the saga library's idiom, not from this codebase.
   **Consequence:** dropping the saga also drops the only thing that wanted a mediator. Steps become outbox messages with `IOutboxDispatcher<T>`, which is the pattern `ChannelMessageQueued` already established here. Task 14 below replaces Task 9's handler with a plain injected service; Task 15 replaces Task 5's dispatcher body. Task 5's `ScheduledRunDue` record and `ScheduleOccurrence` struct survive unchanged.

2. **The design says the host runs "three pollers — the channel outbox, the agent outbox, and the scheduling worker". There is one outbox and one poller.**
   `AddOutbox()` is called exactly once in the entire solution, in `ChannelOutboxServiceCollectionExtensions.AddChannelOutbox`, over one `OutboxMessages` table, registering one `OutboxWorkerService` that drains every `[OutboxMessage]` type by type name. `ApiHostChannelWiringTests` already asserts `ContainSingle(s => s is OutboxWorkerService)`. There is no separate "agent outbox".
   **Consequence:** new message types chain onto the **existing** builder. The hosted-service set to pin in Task 16 is one outbox worker, one scheduling worker, one startup validator, one crash recovery — not three pollers.

3. **Plan B Task 11 step 2 calls the obsolete, poller-duplicating overload.**
   It writes `services.AddScheduledRunDueOutbox();`. Reading the generator's emitted source — `ZeroAlloc.Outbox.Generator`, verified by compiling a probe with `EmitCompilerGeneratedFiles` — that `IServiceCollection` overload is:

   ```csharp
   [Obsolete("Use AddOutbox().AddScheduledRunDueOutbox() instead. Will be removed in the next major.", DiagnosticId = "ZAOBOX010")]
   public static IServiceCollection AddScheduledRunDueOutbox(this IServiceCollection services)
   {
       services.AddOutbox().AddScheduledRunDueOutbox();   // <-- a SECOND AddOutbox
       return services;
   }
   ```

   `AddOutbox` registers its poller with a plain `AddHostedService`, not `TryAdd`. Calling it would have started a second `OutboxWorkerService` racing the same table and broken `ApiHostChannelWiringTests`. The supported form is the `IOutboxBuilder` extension. Task 12 uses it and adds a regression test.

**These are corrections to the plan, not to the approved design's decisions.** The design's four rulings — execution table, `ON CONFLICT DO NOTHING` idempotency, resume-from-last-completed-step, outbox-driven steps — are implemented here exactly as approved.

### API facts verified by compilation, not by README

Every signature below was read off the shipped assemblies — `Thalos.NET` 0.5.0 restored from nuget.org into a scratch project — and the constructing snippet in Task 14 was compiled clean before being written down. Plan B's self-review listed three places where it deferred to the library; all three are now resolved.

```csharp
// ZeroAlloc.Outbox 2.5.2 — the transaction parameter is NULLABLE
ValueTask IOutboxWriter<T>.WriteAsync(T message, DbTransaction? transaction, CancellationToken ct);

// Thalos.NET 0.5.0
ValueTask<Result<AgentTurnResult, AgentError>> ISubagentRunner.RunAsync(SubagentRunRequest request, CancellationToken ct);

// SubagentRunRequest has a parameterless constructor and init properties — object-initializer syntax, not positional:
//   AgentId AgentId · string Task · ISecurityContext Caller · SubagentBudget? Budget · int Depth · SessionId? ParentSessionId
// SubagentBudget is positional:  SubagentBudget(int MaxTotalTokens, TimeSpan Deadline)
// AgentTurnResult.Text is the assistant text.
// ISecurityContext is ZeroAlloc.Authorization.ISecurityContext:
//   string Id · IReadOnlySet<string> Roles · IReadOnlyDictionary<string,string> Claims

// IAgentCatalog has NO name-based lookup:
//   IReadOnlyList<AgentDefinition> Agents;  bool TryGet(AgentId id, out AgentDefinition definition);
// Resolving an agent NAME means scanning Agents. AgentDefinition is a record class, so FirstOrDefault returns null.
```

---

## Global Constraints

Every task's requirements implicitly include this section. It restates plan B's constraints that still bind, plus what this redesign adds.

- **Thalos.NET 0.5.0.** `ISubagentRunner`, `SubagentRunRequest`, `SubagentBudget` come from the package. Daedalus is currently pinned to 0.4.0 — the bump is part of Task 14.
- **Time comes from `TimeProvider`, injected.** Never `DateTime.UtcNow` or `DateTimeOffset.UtcNow` in scheduling, cron, deadline or catch-up code. Spec §7.
- **Domain stays framework-free.** `Daedalus.Domain` references no EF Core, no Thalos, no ZeroAlloc.Outbox, no Cronos. Entities derive from `Entity<Guid>` and validate through a `static Result<T> Create(...)` factory using `CSharpFunctionalExtensions`.
- **Store lifetime depends on whether the store writes an outbox row inside its own transaction.**
  - A store that does **not** write to the outbox is a singleton taking `IDbContextFactory<ApplicationDbContext>`, creating a fresh short-lived context per call. Follow `PostgresConversationMap`.
  - A store that **does** write an outbox row inside its own transaction is **scoped**, and injects `ApplicationDbContext` directly. `EfCoreOutboxStore<TContext>`'s only constructor is `ctor(TContext db)` — it resolves the context from DI, not from a factory — and `EnqueueAsync` calls `db.Database.UseTransactionAsync(transaction)`, which requires the store's connection to be *the same object* as the transaction's. A factory-minted context can never satisfy that, and the failure is a runtime `InvalidOperationException: The specified transaction is not associated with the current connection`, not a compile error. `AddDbContextPool<ApplicationDbContext>` in `AspireExtensions.cs` already registers the context scoped, so two resolutions in one scope return the same instance. Callers resolve such a store per unit of work from an `IServiceScope`.
- **One `AddOutbox()` in the solution.** New `[OutboxMessage]` types chain onto the existing `IOutboxBuilder` inside `AddChannelOutbox`. Never call the generated `IServiceCollection` overload — it is `[Obsolete]` under `ZAOBOX010`, and under `TreatWarningsAsErrors` that obsolete diagnostic **is the guard**: the build fails outright.
  - **Correction, verified 2026-09-18:** the hazard is *not* a second `OutboxWorkerService` racing the first. `AddHostedService<T>()` in .NET 10 uses `TryAddEnumerable`, which dedupes by service-plus-implementation type, so calling `AddOutbox()` twice yields one poller — confirmed empirically, three calls produce one registration. Earlier text in this plan and in `DaedalusChannelsServiceCollectionExtensions`' phase 1.4 remarks claims a runtime race that does not reproduce on these versions. The poller-count assertion in `ApiHostChannelWiringTests` is therefore defence in depth against a shape DI dedupe would *not* catch — a factory-lambda or wrapper-type registration — rather than a reproduction of a live hazard. Do not restate the racing claim.
- **An outbox type key is the fully-qualified type name string** — `"Daedalus.Agents.Scheduling.RunScoutStep"`, emitted into the generated dispatcher as `TypeName`. Moving or renaming one of these records orphans any pending rows carrying the old name. Do not move them casually; if you must, drain the table first.
- **A permanent condition is logged at `Error` and treated as handled, never thrown.** This is `ChannelMessageQueuedDispatcher`'s policy and it is now load-bearing for five more dispatchers: throwing burns the retry budget before dead-lettering something that could never succeed.
- **Migrations:** `dotnet ef migrations add <Name> --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations`. Apply with `dotnet run --project src/Daedalus.Migrations`. EF 10 throws on `Migrate()` when the model has pending changes not in the snapshot — always regenerate, never hand-edit the snapshot.
- **Never reintroduce `IConversationMap.GetBySessionAsync`.** It was deliberately removed in 0.4.0. A schedule carries its delivery target; a run copies it at claim time.
- **The guarantee is bounded, and the docs must say so.** A crash *after* a subagent returns but *before* its step commits re-runs that step and pays its tokens twice. **One step can be lost, never the whole run.** Do not let anyone "improve" the XML docs into claiming exactly-once.
- **Assume nothing is pinned until you have seen it fail.** Four tests on the phase 1.4 branch passed while the bug they named was live. Every test in this plan has an explicit "verify it fails" step; do not skip it because the implementation is obviously absent.
- **Conventional commits, free scopes.** Commit bodies must not contain nested parentheses — release-please silently drops such commits.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Daedalus.Domain/Entities/ScheduledRunExecution.cs` | Create — the per-occurrence aggregate, `RunStep`, step transitions |
| `src/Daedalus.Infrastructure/Persistence/Configurations/ScheduledRunExecutionConfiguration.cs` | Create — table, `UNIQUE (ScheduleId, OccurrenceAt)`, `xmin` |
| `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs` | Modify — add `DbSet<ScheduledRunExecution>` |
| `src/Daedalus.Agents/Scheduling/RunSteps.cs` | Create — `RunScoutStep`, `RunWriterStep`, `DeliverDigest` outbox messages |
| `src/Daedalus.Agents/Channels/ChannelOutboxServiceCollectionExtensions.cs` | Modify — chain the four new types onto the one builder |
| `src/Daedalus.Agents/Scheduling/ScheduledRunExecutionStore.cs` | Create — begin / advance / fail, each one transaction |
| `src/Daedalus.Agents/Scheduling/RepoDigestPrompts.cs` | Create — agent names and prompt text for the one workflow |
| `src/Daedalus.Agents/Scheduling/DetachedRunOptions.cs` | Create — principal, roles, budget for detached runs |
| `src/Daedalus.Agents/Scheduling/DetachedPrincipal.cs` | Create — `ISecurityContext` over configuration, not over a user |
| `src/Daedalus.Agents/Scheduling/ISubagentRunExecutor.cs` | Create — the single `ISubagentRunner` seam |
| `src/Daedalus.Agents/Scheduling/SubagentRunExecutor.cs` | Create — its only implementation |
| `src/Daedalus.Agents/Scheduling/ScheduledRunDueDispatcher.cs` | Modify — supersedes plan B Task 5's saga-start body |
| `src/Daedalus.Agents/Scheduling/StepDispatchers.cs` | Create — scout / writer / deliver dispatchers |
| `src/Daedalus.Agents/Scheduling/ScheduleSweeperJob.cs` | Create — the `[Job]`, unchanged from plan B Task 11 |
| `src/Daedalus.Agents/DaedalusSchedulingServiceCollectionExtensions.cs` | Create — one entry point |
| `src/Daedalus.Api/Program.cs`, `src/Daedalus.Cli/Program.cs` | Modify — call `AddDaedalusScheduling` |

Scheduling lives in its own folder under `Daedalus.Agents` because it changes when triggering changes. The `Sagas/` folder plan B proposed is not created at all.

---

### Task 10: The `ScheduledRunExecution` aggregate

The saga's in-memory state becomes a row. This task is the row's shape and its legal transitions, with no persistence and no framework.

**Files:**
- Create: `src/Daedalus.Domain/Entities/ScheduledRunExecution.cs`
- Test: `tests/Daedalus.Tests.Unit.Domain/ScheduledRunExecutionTests.cs`

**Interfaces:**
- Consumes: `Entity<Guid>` from `Daedalus.Domain.Entities`; `Result<T>` from `CSharpFunctionalExtensions`.
- Produces:
  - `enum RunStep { Pending = 0, Scout = 1, Writer = 2, Deliver = 3, Done = 4, Failed = 5 }`
  - `ScheduledRunExecution.Create(Guid scheduleId, DateTime occurrenceAtUtc, string channelId, string conversationId, string principalId, IReadOnlyList<string> roles, DateTime nowUtc) -> Result<ScheduledRunExecution>`
  - Properties: `ScheduleId`, `OccurrenceAt`, `Step`, `Findings`, `Digest`, `ChannelId`, `ConversationId`, `PrincipalId`, `Roles`, `Attempts`, `LastError`, `CreatedAt`, `UpdatedAt`
  - `void BeginScout(DateTime nowUtc)`, `void RecordFindings(string findings, DateTime nowUtc)`, `void RecordDigest(string digest, DateTime nowUtc)`, `void Complete(DateTime nowUtc)`, `void Fail(string error, DateTime nowUtc)`
  - `const int MaxChannelIdLength = 32`, `MaxConversationIdLength = 128`, `MaxPrincipalIdLength = 128`

- [x] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain;

public class ScheduledRunExecutionTests
{
    private static readonly Guid ScheduleId = Guid.Parse("0f1d8a2c-5e6b-4a71-9c3d-8b2f4e6a1c07");
    private static readonly DateTime Occurrence = new(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 17, 7, 0, 5, DateTimeKind.Utc);

    private static ScheduledRunExecution NewExecution() =>
        ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus", ["reader"], Now).Value;

    [Fact]
    public void Create_starts_Pending_with_no_output_and_no_attempts()
    {
        var result = ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus", ["reader"], Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.Step.Should().Be(RunStep.Pending);
        result.Value.Findings.Should().BeNull();
        result.Value.Digest.Should().BeNull();
        result.Value.Attempts.Should().Be(0);
        result.Value.LastError.Should().BeNull();
        result.Value.CreatedAt.Should().Be(Now);
        result.Value.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void Create_rejects_an_empty_conversation_id()
    {
        // a detached run has no live turn to fall back on: with no delivery target the digest
        // would be produced, paid for, and have nowhere to go
        var result = ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "", "schedule:daedalus", ["reader"], Now);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_an_empty_role_set()
    {
        // a principal with no roles can do nothing; that is a configuration error, not a narrow principal
        var result = ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus", [], Now);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_a_non_utc_occurrence()
    {
        // the unique key is (ScheduleId, OccurrenceAt); a local-kind value would make the same
        // instant collide or not depending on the host's timezone
        var result = ScheduledRunExecution.Create(
            ScheduleId, new DateTime(2026, 9, 17, 7, 0, 0, DateTimeKind.Local),
            "telegram", "123456", "schedule:daedalus", ["reader"], Now);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void The_happy_path_walks_Pending_to_Done_carrying_both_outputs()
    {
        var execution = NewExecution();

        execution.BeginScout(Now);
        execution.Step.Should().Be(RunStep.Scout);

        execution.RecordFindings("three open PRs", Now.AddSeconds(10));
        execution.Step.Should().Be(RunStep.Writer);
        execution.Findings.Should().Be("three open PRs");

        execution.RecordDigest("Here is your digest.", Now.AddSeconds(20));
        execution.Step.Should().Be(RunStep.Deliver);
        execution.Digest.Should().Be("Here is your digest.");

        execution.Complete(Now.AddSeconds(30));
        execution.Step.Should().Be(RunStep.Done);
        execution.UpdatedAt.Should().Be(Now.AddSeconds(30));
    }

    [Fact]
    public void Persisted_output_survives_the_transitions_that_follow_it()
    {
        // this is the whole point of resume: a crash in the writer stage must not re-pay for the scout
        var execution = NewExecution();
        execution.BeginScout(Now);
        execution.RecordFindings("three open PRs", Now);
        execution.RecordDigest("Here is your digest.", Now);
        execution.Complete(Now);

        execution.Findings.Should().Be("three open PRs");
    }

    [Fact]
    public void Fail_records_the_error_and_counts_the_attempt()
    {
        var execution = NewExecution();
        execution.BeginScout(Now);

        execution.Fail("provider returned 529", Now.AddSeconds(3));

        execution.Step.Should().Be(RunStep.Failed);
        execution.LastError.Should().Be("provider returned 529");
        execution.Attempts.Should().Be(1);
        execution.UpdatedAt.Should().Be(Now.AddSeconds(3));
    }

    [Fact]
    public void Fail_twice_accumulates_attempts_and_keeps_the_latest_error()
    {
        var execution = NewExecution();
        execution.BeginScout(Now);

        execution.Fail("first", Now);
        execution.Fail("second", Now.AddSeconds(1));

        execution.Attempts.Should().Be(2);
        execution.LastError.Should().Be("second");
    }

    [Fact]
    public void A_default_RunStep_is_Pending_and_therefore_never_a_real_step()
    {
        // AgentErrorCode.Validation being member 0 produced false-passing tests three times on one
        // branch. RunStep's zero value is deliberately the state no dispatcher acts on, so a
        // default(RunStep) can never be mistaken for "the scout should run".
        default(RunStep).Should().Be(RunStep.Pending);
    }

    [Theory]
    [InlineData(RunStep.Pending)]
    [InlineData(RunStep.Writer)]
    [InlineData(RunStep.Deliver)]
    [InlineData(RunStep.Done)]
    [InlineData(RunStep.Failed)]
    public void RecordFindings_throws_unless_the_row_is_in_the_Scout_step(RunStep step)
    {
        var execution = NewExecution();
        Drive(execution, step);

        var act = () => execution.RecordFindings("x", Now);

        act.Should().Throw<InvalidOperationException>(
            "the dispatcher guards the step before calling; reaching here means the guard is gone");
    }

    private static void Drive(ScheduledRunExecution execution, RunStep target)
    {
        if (target is RunStep.Pending) return;
        execution.BeginScout(Now);
        if (target is RunStep.Scout) return;
        execution.RecordFindings("seed", Now);
        if (target is RunStep.Writer) return;
        execution.RecordDigest("seed", Now);
        if (target is RunStep.Deliver) return;
        if (target is RunStep.Done) { execution.Complete(Now); return; }
        execution.Fail("seed", Now);
    }
}
```

- [x] **Step 2: Run and verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit.Domain --filter FullyQualifiedName~ScheduledRunExecutionTests`
Expected: FAIL — `ScheduledRunExecution` does not exist.

- [x] **Step 3: Write the enum**

```csharp
namespace Daedalus.Domain.Entities;

/// <summary>
///     Which step of a scheduled run should happen next. Advances strictly forward:
///     <see cref="Pending"/> to <see cref="Scout"/> to <see cref="Writer"/> to <see cref="Deliver"/> to
///     <see cref="Done"/>, or to <see cref="Failed"/> from any of them.
/// </summary>
/// <remarks>
///     <see cref="Pending"/> is member 0 on purpose. A <c>default(RunStep)</c> then means "no step has been
///     enqueued yet" — a state no dispatcher acts on — rather than aliasing a real step. <c>AgentErrorCode</c>
///     numbers <c>Validation</c> as 0 and that produced false-passing tests three times on one branch, in three
///     files, from two implementers. The value is persisted as a string, so this numbering costs nothing to keep.
/// </remarks>
public enum RunStep
{
    /// <summary>The row exists but nothing has been enqueued. Never observable outside its creating transaction.</summary>
    Pending = 0,

    /// <summary>A <c>RunScoutStep</c> is queued or running.</summary>
    Scout = 1,

    /// <summary>The scout's findings are persisted; a <c>RunWriterStep</c> is queued or running.</summary>
    Writer = 2,

    /// <summary>The digest is persisted; a <c>DeliverDigest</c> is queued or running.</summary>
    Deliver = 3,

    /// <summary>Delivered. Terminal.</summary>
    Done = 4,

    /// <summary>A step failed; <see cref="ScheduledRunExecution.LastError"/> says which and why. Terminal.</summary>
    Failed = 5,
}
```

- [x] **Step 4: Write the aggregate**

Follow `ChannelConversation` and `ScheduledRun` for shape: `sealed class ... : Entity<Guid>`, private setters, a static `Create` returning `Result<T>`, `const int Max*Length` fields, XML docs on every public member.

- `Create` validates: `scheduleId` is not `Guid.Empty`; `occurrenceAtUtc.Kind == DateTimeKind.Utc`; `channelId`, `conversationId` and `principalId` are non-blank and within their length caps; `roles` is non-empty. It sets `Id = Guid.CreateVersion7()`, `Step = RunStep.Pending`, `Attempts = 0`, `CreatedAt = UpdatedAt = nowUtc`.
- `BeginScout(now)` requires `Step == Pending`, sets `Step = Scout` and `UpdatedAt`.
- `RecordFindings(findings, now)` requires `Step == Scout`, sets `Findings`, `Step = Writer`, `UpdatedAt`.
- `RecordDigest(digest, now)` requires `Step == Writer`, sets `Digest`, `Step = Deliver`, `UpdatedAt`.
- `Complete(now)` requires `Step == Deliver`, sets `Step = Done` and `UpdatedAt`.
- `Fail(error, now)` is legal from any non-terminal step: sets `Step = Failed`, `LastError = error`, `Attempts += 1`, `UpdatedAt`.

Each "requires" is a guard clause naming both steps:

```csharp
private void RequireStep(RunStep expected, string operation)
{
    if (Step != expected)
    {
        throw new InvalidOperationException(
            $"{operation} requires step {expected}, but this execution is at {Step}. " +
            "The dispatcher is expected to have checked the step before calling; reaching here means that check is missing.");
    }
}
```

The throw is a **programming-error** signal, not a redelivery path: redelivery is caught by the store's step check in Task 13 and never reaches here. Keeping it a throw is what makes a missing check loud instead of silently corrupting a row.

- [x] **Step 5: Run and verify it passes**

Run: `dotnet test tests/Daedalus.Tests.Unit.Domain --filter FullyQualifiedName~ScheduledRunExecutionTests`
Expected: PASS, 14 tests — 9 facts plus a 5-case theory.

- [x] **Step 6: Commit**

```bash
git add src/Daedalus.Domain tests/Daedalus.Tests.Unit.Domain
git commit -m "feat(domain): add the ScheduledRunExecution aggregate"
```

---

### Task 11: Persist `ScheduledRunExecutions`

**Files:**
- Create: `src/Daedalus.Infrastructure/Persistence/Configurations/ScheduledRunExecutionConfiguration.cs`
- Modify: `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs`
- Create: migration `AddScheduledRunExecutions`
- Test: `tests/Daedalus.Tests.Integration/Scheduling/ScheduledRunExecutionPersistenceTests.cs`

**Interfaces:**
- Consumes: `ScheduledRunExecution` and `RunStep` from Task 10.
- Produces: `ApplicationDbContext.ScheduledRunExecutions` as `DbSet<ScheduledRunExecution>`; table `ScheduledRunExecutions`; unique index `IX_ScheduledRunExecution_Schedule_Occurrence` on `(ScheduleId, OccurrenceAt)`.

- [x] **Step 1: Write the configuration**

Model it on `ScheduledRunConfiguration` from plan B Task 4.

```csharp
builder.ToTable("ScheduledRunExecutions");
builder.HasKey(e => e.Id);

builder.Property(e => e.ScheduleId).IsRequired();
builder.Property(e => e.OccurrenceAt).IsRequired();
builder.Property(e => e.Step).IsRequired().HasConversion<string>().HasMaxLength(16);
builder.Property(e => e.Findings);
builder.Property(e => e.Digest);
builder.Property(e => e.ChannelId).IsRequired().HasMaxLength(ScheduledRunExecution.MaxChannelIdLength);
builder.Property(e => e.ConversationId).IsRequired().HasMaxLength(ScheduledRunExecution.MaxConversationIdLength);
builder.Property(e => e.PrincipalId).IsRequired().HasMaxLength(ScheduledRunExecution.MaxPrincipalIdLength);
builder.Property(e => e.Attempts).IsRequired();
builder.Property(e => e.LastError);
builder.Property(e => e.CreatedAt).IsRequired();
builder.Property(e => e.UpdatedAt).IsRequired();

// Same delimited-column treatment as ScheduledRun.Roles, and the same ValueComparer requirement:
// without it EF cannot detect changes to a converted collection and the column silently never updates.
builder.Property(e => e.Roles)
    .IsRequired()
    .HasMaxLength(512)
    .HasConversion(
        v => string.Join(',', v),
        v => v.Split(',', StringSplitOptions.RemoveEmptyEntries))
    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
        (a, b) => a!.SequenceEqual(b!),
        v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
        v => v.ToList()));

// THE correctness constraint of this phase. The saga's correlation key, expressed in the schema:
// one execution per schedule-and-occurrence, enforced by the database rather than by any handler's
// diligence. Task 13's INSERT ... ON CONFLICT names this index.
builder.HasIndex(e => new { e.ScheduleId, e.OccurrenceAt })
    .IsUnique()
    .HasDatabaseName("IX_ScheduledRunExecution_Schedule_Occurrence");

// Optimistic concurrency between two pollers racing one step. xmin is Postgres's own system column:
// no extra column, and the database maintains it rather than the application.
builder.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");
```

- [x] **Step 2: Add the `DbSet`**

In `ApplicationDbContext`, beside `ScheduledRuns`:

```csharp
    /// <summary>One row per firing of a <see cref="ScheduledRun"/>; see <see cref="ScheduledRunExecution"/>.</summary>
    public DbSet<ScheduledRunExecution> ScheduledRunExecutions => Set<ScheduledRunExecution>();
```

- [x] **Step 3: Generate the migration**

```bash
dotnet ef migrations add AddScheduledRunExecutions --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations
```

Read the generated `Up` before continuing. Expected: exactly one `CreateTable` and one `CreateIndex` with `unique: true`. If it touches any other table, the model has drifted — stop and find out why rather than applying it.

- [x] **Step 4: Apply and verify against a real database**

```bash
docker ps --format '{{.Names}}' | grep -i postgres     # Docker must be UP; this is Testcontainers-backed work
dotnet run --project src/Daedalus.Migrations
docker exec -i <postgres-container> psql -U postgres -d daedalus -c '\d "ScheduledRunExecutions"'
```

Expected: the table, and `"IX_ScheduledRunExecution_Schedule_Occurrence" UNIQUE, btree ("ScheduleId", "OccurrenceAt")`. Confirm the word `UNIQUE` is actually there — the index existing is not the same claim.

If Postgres complains about a collation version mismatch, that is the known local issue: `docker volume rm daedalus_postgres_data`, or `REINDEX DATABASE daedalus;`.

- [x] **Step 5: Write the failing persistence tests**

`xmin` is Postgres-specific, so these live in `Daedalus.Tests.Integration`, not `Daedalus.Tests.Unit.Infrastructure`.

```csharp
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunExecutionPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid ScheduleId = Guid.Parse("0f1d8a2c-5e6b-4a71-9c3d-8b2f4e6a1c07");
    private static readonly DateTime Occurrence = new(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 17, 7, 0, 5, DateTimeKind.Utc);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_execution_round_trips_including_its_roles_and_its_step()
    {
        await using (var db = fixture.CreateContext())
        {
            db.ScheduledRunExecutions.Add(ScheduledRunExecution.Create(
                ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus",
                ["reader", "digest"], Now).Value);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var loaded = await read.ScheduledRunExecutions.SingleAsync(e => e.ScheduleId == ScheduleId);

        loaded.Roles.Should().BeEquivalentTo(["reader", "digest"]);
        loaded.Step.Should().Be(RunStep.Pending);
        loaded.OccurrenceAt.Should().Be(Occurrence);
    }

    [Fact]
    public async Task A_second_row_for_the_same_schedule_and_occurrence_is_rejected_by_the_database()
    {
        // the idempotency guarantee, enforced where no handler can forget it
        await using var db = fixture.CreateContext();
        db.ScheduledRunExecutions.Add(Execution());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.ScheduledRunExecutions.Add(Execution());
        var act = async () => await db.SaveChangesAsync();

        (await act.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<DbUpdateException, PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_different_occurrence_of_the_same_schedule_is_allowed()
    {
        await using var db = fixture.CreateContext();
        db.ScheduledRunExecutions.Add(Execution());
        db.ScheduledRunExecutions.Add(ScheduledRunExecution.Create(
            ScheduleId, Occurrence.AddDays(1), "telegram", "123456", "schedule:daedalus", ["reader"], Now).Value);

        await db.SaveChangesAsync();

        (await db.ScheduledRunExecutions.CountAsync()).Should().Be(2);
    }

    private static ScheduledRunExecution Execution() =>
        ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus", ["reader"], Now).Value;
}
```

`PostgresFixture` and `DatabaseCollection` already exist in `tests/Daedalus.Tests.Integration/Fixtures` — read them first and follow their construction rather than inventing a second approach. If `PostgresFixture` exposes no `CreateContext()`, add one there rather than newing a `DbContextOptionsBuilder` in this file.

- [x] **Step 6: Run the tests, then see the constraint test fail**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ScheduledRunExecutionPersistenceTests`
Expected: PASS, 3 tests.

Then prove the second test is pinned: temporarily drop `.IsUnique()` from the configuration, regenerate the migration, re-run, and confirm it **fails**. Revert both. A unique-constraint test that passes against a non-unique index is the exact shape of the four false-passing tests phase 1.4 shipped.

- [x] **Step 7: Commit**

```bash
git add src/Daedalus.Infrastructure tests/Daedalus.Tests.Integration
git commit -m "feat(persistence): add the ScheduledRunExecutions table with its occurrence uniqueness constraint"
```

---

### Task 12: The step messages, on the one outbox builder

**Files:**
- Create: `src/Daedalus.Agents/Scheduling/RunSteps.cs`
- Modify: `src/Daedalus.Agents/Channels/ChannelOutboxServiceCollectionExtensions.cs`
- Test: `tests/Daedalus.Tests.Integration/Channels/ApiHostChannelWiringTests.cs` — extend

**Interfaces:**
- Consumes: `ScheduledRunDue` from plan B Task 5.
- Produces: `RunScoutStep(Guid ExecutionId)`, `RunWriterStep(Guid ExecutionId)`, `DeliverDigest(Guid ExecutionId)`, each `[OutboxMessage]`; the generated `IOutboxWriter<T>` for each, and the generated `IOutboxBuilder` extensions `AddRunScoutStepOutbox()`, `AddRunWriterStepOutbox()`, `AddDeliverDigestOutbox()`.

- [x] **Step 1: Create the step messages**

```csharp
using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     Run the scout subagent for one execution. Written inside the transaction that created the
///     <c>ScheduledRunExecutions</c> row, so the row and its first step commit together.
/// </summary>
/// <remarks>
///     <para>
///     Carries only the execution id: everything the step needs — delivery target, principal, roles — was copied
///     onto the row at claim time, and re-reading it there is what makes a redelivery see the current step rather
///     than a stale snapshot of one. Outbox delivery is at-least-once; the step check in
///     <c>ScheduledRunExecutionStore</c> makes a second delivery a no-op.
///     </para>
///     <para>
///     <b>The guarantee is bounded.</b> A crash after the subagent returns but before the step commits re-runs
///     that step and pays its tokens twice. This is inherent without a two-phase protocol with the model provider,
///     which does not exist. One step can be lost; the run cannot.
///     </para>
/// </remarks>
[OutboxMessage]
public sealed record RunScoutStep(Guid ExecutionId);

/// <summary>Run the writer subagent over the persisted findings. See <see cref="RunScoutStep"/> for the redelivery contract.</summary>
[OutboxMessage]
public sealed record RunWriterStep(Guid ExecutionId);

/// <summary>
///     Deliver the persisted digest. Its dispatcher writes <see cref="Channels.ChannelMessageQueued"/> in the same
///     transaction that sets <c>Step = Done</c> — the guarantee <c>ChannelMessageQueuedDispatcher</c> has been
///     waiting for since phase 1.4, which was never the saga's doing, only the transaction's.
/// </summary>
[OutboxMessage]
public sealed record DeliverDigest(Guid ExecutionId);
```

- [x] **Step 2: Chain them onto the existing builder**

In `ChannelOutboxServiceCollectionExtensions.AddChannelOutbox`, extend the one chain. **Do not add a second `AddOutbox()`.**

```csharp
        services.AddOutbox(o =>
            {
                o.PollingInterval = TimeSpan.FromSeconds(2);
                o.BatchSize = 20;
                o.MaxAttempts = 8;
                o.RetryBaseDelay = TimeSpan.FromSeconds(1);
            })
            .WithEfCore<ApplicationDbContext>()
            .AddChannelMessageQueuedOutbox()
            // Scheduling rides the same table and the same poller. The generated IServiceCollection
            // overload of each of these is [Obsolete] ZAOBOX010 and calls AddOutbox again, which would
            // register a second OutboxWorkerService racing this one; the IOutboxBuilder form does not.
            .AddScheduledRunDueOutbox()
            .AddRunScoutStepOutbox()
            .AddRunWriterStepOutbox()
            .AddDeliverDigestOutbox();
```

Update the method's XML summary to say it registers the channel **and scheduling** message types over one poller, and delete the "Nothing writes a `ChannelMessageQueued` yet in this phase" sentence — as of Task 15, something does.

> Registering the four new writers here rather than in `AddDaedalusScheduling` is deliberate, and is the point of correction 3 above: the builder exists only inside this method, and the only way to reach it from elsewhere is the overload that duplicates the poller.

- [x] **Step 3: Write the failing regression test**

Extend `ApiHostChannelWiringTests` — it already boots the real API host and is the established home for this assertion.

```csharp
    [Fact]
    public void The_API_host_runs_exactly_one_outbox_poller_for_every_message_type()
    {
        var services = _factory.Services;

        services.GetServices<IHostedService>().Count(s => s is OutboxWorkerService).Should().Be(1,
            "every [OutboxMessage] type rides one AddOutbox call and one OutboxMessages table; a second " +
            "AddOutbox — which the generated IServiceCollection overloads perform, ZAOBOX010 — would start a " +
            "second poller racing this one on the same rows");

        services.GetRequiredService<IOutboxWriter<ScheduledRunDue>>().Should().NotBeNull();
        services.GetRequiredService<IOutboxWriter<RunScoutStep>>().Should().NotBeNull();
        services.GetRequiredService<IOutboxWriter<RunWriterStep>>().Should().NotBeNull();
        services.GetRequiredService<IOutboxWriter<DeliverDigest>>().Should().NotBeNull();
    }
```

- [x] **Step 4: Run, then see it fail**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ApiHostChannelWiringTests`
Expected: PASS, 2 tests.

Then temporarily replace one `.AddRunScoutStepOutbox()` in the chain with a standalone `services.AddRunScoutStepOutbox();` after the chain, re-run, and confirm the poller-count assertion fails with 2. Revert. This is the one regression the corrections above exist to prevent, so it must be demonstrated rather than assumed.

- [x] **Step 5: Commit**

```bash
git add src/Daedalus.Agents tests/Daedalus.Tests.Integration
git commit -m "feat(scheduling): add the step outbox messages on the single outbox builder"
```

---

### Task 13: `ScheduledRunExecutionStore` — begin, advance, fail

The correctness core. Every method is exactly one transaction, and every method reports whether it did the work, so its caller can tell "advanced" from "someone already did".

**Files:**
- Create: `src/Daedalus.Agents/Scheduling/ScheduledRunExecutionStore.cs`
- Test: `tests/Daedalus.Tests.Integration/Scheduling/ScheduledRunExecutionStoreTests.cs`

**Interfaces:**
- Consumes: `ApplicationDbContext` **injected directly, not via `IDbContextFactory`** — this store writes outbox rows inside its own transactions, so it must share the scoped context the outbox writers resolve; see Global Constraints. `IOutboxWriter<RunScoutStep>`, `IOutboxWriter<RunWriterStep>`, `IOutboxWriter<DeliverDigest>`, `IOutboxWriter<ChannelMessageQueued>`, `TimeProvider`, `ILogger<ScheduledRunExecutionStore>`.
- **Registered scoped**, and resolved per unit of work from an `IServiceScope` by its dispatchers.
- Produces:

```csharp
ValueTask<bool> TryBeginAsync(ScheduledRunDue due, CancellationToken ct);
ValueTask<bool> TryCompleteScoutAsync(Guid executionId, string findings, CancellationToken ct);
ValueTask<bool> TryCompleteWriterAsync(Guid executionId, string digest, CancellationToken ct);
ValueTask<bool> TryCompleteDeliveryAsync(Guid executionId, CancellationToken ct);
ValueTask FailAsync(Guid executionId, string error, CancellationToken ct);
ValueTask<ScheduledRunExecution?> FindAsync(Guid executionId, CancellationToken ct);
internal static readonly string[] InsertColumnNames;   // read by the column-drift guard in step 6
```

`false` means "this execution was not in the step this method advances" — a redelivery, or a lost race. It is a normal outcome, not an error.

- [x] **Step 1: Write the failing tests**

```csharp
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunExecutionStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime Occurrence = new(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 17, 7, 0, 5, TimeSpan.Zero));
    private Guid _scheduleId;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _scheduleId = await SeedScheduleAsync("daily-digest", "telegram", "123456", "schedule:daedalus", ["reader"]);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
    public async Task Delivery_writes_the_channel_message_in_the_same_transaction_as_Done()
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

        await act.Should().ThrowAsync<Exception>();
        (await SingleExecutionAsync()).Step.Should().Be(RunStep.Deliver,
            "a digest marked delivered that was never queued is the one outcome with no recovery path");
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
        var store = Store();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, Occurrence), default);
        var id = (await SingleExecutionAsync()).Id;

        var results = await Task.WhenAll(
            Store().TryCompleteScoutAsync(id, "a", default).AsTask(),
            Store().TryCompleteScoutAsync(id, "b", default).AsTask());

        results.Count(r => r).Should().Be(1, "one wins; the other must no-op rather than throw or double-advance");
        (await OutboxRowsAsync<RunWriterStep>()).Should().ContainSingle();
    }
}
```

Write the helpers out rather than leaving them implied. `Store()` builds a `ScheduledRunExecutionStore` over `fixture`'s context factory, the real generated outbox writers, and `_time`. `StoreWithFailingChannelWriter()` substitutes `IOutboxWriter<ChannelMessageQueued>` to throw. `OutboxRowsAsync<T>()` counts `OutboxMessages` rows whose `TypeName` equals `typeof(T).FullName`; `OutboxPayloadsAsync<T>()` deserializes them with the same `IOutboxSerializer` the host registers. `SeedScheduleAsync` inserts a `ScheduledRun` through `ScheduledRun.Create` with `NextRunAt = Occurrence`.

- [x] **Step 2: Run and verify they fail**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ScheduledRunExecutionStoreTests`
Expected: FAIL — the store does not exist.

- [x] **Step 3: Implement `TryBeginAsync`**

```csharp
internal static readonly string[] InsertColumnNames =
[
    "Id", "ScheduleId", "OccurrenceAt", "Step", "Findings", "Digest", "ChannelId",
    "ConversationId", "PrincipalId", "Roles", "Attempts", "LastError", "CreatedAt", "UpdatedAt",
];

private static readonly string InsertColumns =
    string.Join(',', InsertColumnNames.Select(c => $"\"{c}\""));

public async ValueTask<bool> TryBeginAsync(ScheduledRunDue due, CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(due);

    var now = _time.GetUtcNow().UtcDateTime;
    var db = _db;   // injected scoped context, shared with the outbox writers — see Global Constraints
    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

    var schedule = await db.ScheduledRuns
        .AsNoTracking()
        .SingleOrDefaultAsync(r => r.Id == due.ScheduleId, ct)
        .ConfigureAwait(false);

    if (schedule is null)
    {
        // Permanent: retrying cannot make a deleted schedule reappear. Same policy as
        // ChannelMessageQueuedDispatcher's unknown-channel branch — log loudly, treat as handled.
        LogUnknownSchedule(_logger, due.ScheduleId, due.OccurrenceAtUtc);
        return false;
    }

    var created = ScheduledRunExecution.Create(
        schedule.Id, due.OccurrenceAtUtc, schedule.ChannelId, schedule.ConversationId,
        schedule.PrincipalId, schedule.Roles, now);

    if (created.IsFailure)
    {
        LogInvalidSchedule(_logger, schedule.Name, created.Error);
        return false;
    }

    var row = created.Value;
    row.BeginScout(now);

    // INSERT ... ON CONFLICT DO NOTHING rather than SaveChanges-and-catch-23505: in PostgreSQL a
    // constraint violation aborts the whole transaction, so the catch could not then go on to write
    // the outbox row in the same transaction — it would have to roll back and start again. One
    // statement, no exception control flow, and the unique index is the only arbiter.
    // CORRECTION (2026-09-18): EF has no `:raw` format specifier — ExecuteSqlInterpolatedAsync
    // parameterizes EVERY hole, so the column list would be sent as a bound parameter and Postgres
    // would reject the statement as a syntax error. Verified live. Use ExecuteSqlRawAsync with the
    // column list composed into the command text (it is a compile-time constant) and {0}..{n}
    // placeholders for the values, which keeps every value parameterized.
    var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
        $"""
         INSERT INTO "ScheduledRunExecutions" ({InsertColumns:raw})
         VALUES ({row.Id}, {row.ScheduleId}, {row.OccurrenceAt}, {row.Step.ToString()}, {row.Findings},
                 {row.Digest}, {row.ChannelId}, {row.ConversationId}, {row.PrincipalId},
                 {string.Join(',', row.Roles)}, {row.Attempts}, {row.LastError}, {row.CreatedAt}, {row.UpdatedAt})
         ON CONFLICT ("ScheduleId", "OccurrenceAt") DO NOTHING
         """, ct).ConfigureAwait(false);

    if (inserted == 0)
    {
        LogRedelivered(_logger, due.ScheduleId, due.OccurrenceAtUtc);
        await tx.RollbackAsync(ct).ConfigureAwait(false);
        return false;
    }

    await _scoutWriter.WriteAsync(new RunScoutStep(row.Id), tx.GetDbTransaction(), ct).ConfigureAwait(false);
    await tx.CommitAsync(ct).ConfigureAwait(false);
    return true;
}
```

`GetDbTransaction()` needs `using Microsoft.EntityFrameworkCore.Storage;`. The writer's transaction parameter is `DbTransaction?` — passing the ambient transaction is what makes the row and the message commit together, and passing `null` would silently lose that while still compiling.

- [x] **Step 4: Implement the advance methods**

All three share one shape. Write it once as a private helper rather than copying the transaction handling three times:

```csharp
private async ValueTask<bool> TryAdvanceAsync(
    Guid executionId,
    RunStep expected,
    Action<ScheduledRunExecution, DateTime> mutate,
    Func<ScheduledRunExecution, IDbContextTransaction, CancellationToken, ValueTask> enqueueNext,
    CancellationToken ct)
{
    var now = _time.GetUtcNow().UtcDateTime;
    var db = _db;   // injected scoped context, shared with the outbox writers — see Global Constraints
    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

    var row = await db.ScheduledRunExecutions.SingleOrDefaultAsync(e => e.Id == executionId, ct).ConfigureAwait(false);
    if (row is null)
    {
        LogUnknownExecution(_logger, executionId);
        return false;
    }

    if (row.Step != expected)
    {
        // Redelivery, or the other poller won. Not an error: at-least-once delivery makes this routine.
        LogStepAlreadyPast(_logger, executionId, expected, row.Step);
        return false;
    }

    // CORRECTION (2026-09-18): the try MUST also wrap enqueueNext, not only SaveChangesAsync.
    // The outbox writer shares this scoped context, so its internal SaveChangesAsync can flush this
    // row's pending update and raise the concurrency loss from inside the enqueue call. Reproduced
    // under concurrent load in a full-suite run. ScheduledRunStore reached the same conclusion for
    // the same reason; this plan failed to carry that lesson across.
    mutate(row, now);

    try
    {
        await enqueueNext(row, tx, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
    catch (DbUpdateConcurrencyException)
    {
        // xmin moved between the read and the save: the other poller advanced this step first.
        // Roll back and report no-op, exactly as the step check above would have.
        LogLostStepRace(_logger, executionId, expected);
        await tx.RollbackAsync(ct).ConfigureAwait(false);
        return false;
    }

    await tx.CommitAsync(ct).ConfigureAwait(false);
    return true;
}
```

Then the three callers:

```csharp
public ValueTask<bool> TryCompleteScoutAsync(Guid executionId, string findings, CancellationToken ct) =>
    TryAdvanceAsync(executionId, RunStep.Scout,
        (row, now) => row.RecordFindings(findings, now),
        (row, tx, token) => _writerWriter.WriteAsync(new RunWriterStep(row.Id), tx.GetDbTransaction(), token),
        ct);

public ValueTask<bool> TryCompleteWriterAsync(Guid executionId, string digest, CancellationToken ct) =>
    TryAdvanceAsync(executionId, RunStep.Writer,
        (row, now) => row.RecordDigest(digest, now),
        (row, tx, token) => _deliverWriter.WriteAsync(new DeliverDigest(row.Id), tx.GetDbTransaction(), token),
        ct);

public ValueTask<bool> TryCompleteDeliveryAsync(Guid executionId, CancellationToken ct) =>
    TryAdvanceAsync(executionId, RunStep.Deliver,
        (row, now) => row.Complete(now),
        (row, tx, token) => _channelWriter.WriteAsync(
            new ChannelMessageQueued(row.ChannelId, row.ConversationId, row.Digest!), tx.GetDbTransaction(), token),
        ct);
```

`row.Digest!` is safe precisely because `Step == Deliver` was checked: `RecordDigest` is the only transition into `Deliver`, and it sets `Digest`.

- [x] **Step 5: Implement `FailAsync` and `FindAsync`**

`FailAsync` mirrors the helper but is legal from any non-terminal step, and its "next message" is the operator notice:

```csharp
public async ValueTask FailAsync(Guid executionId, string error, CancellationToken ct)
{
    var now = _time.GetUtcNow().UtcDateTime;
    var db = _db;   // injected scoped context, shared with the outbox writers — see Global Constraints
    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

    var row = await db.ScheduledRunExecutions.SingleOrDefaultAsync(e => e.Id == executionId, ct).ConfigureAwait(false);
    if (row is null) { LogUnknownExecution(_logger, executionId); return; }
    if (row.Step is RunStep.Done or RunStep.Failed) { return; }

    var schedule = await db.ScheduledRuns.AsNoTracking()
        .SingleOrDefaultAsync(r => r.Id == row.ScheduleId, ct).ConfigureAwait(false);
    var name = schedule?.Name ?? row.ScheduleId.ToString();
    var failedStep = row.Step;

    row.Fail(error, now);

    // The channels design's standing rule, and the subject of both parked phase 1.4 defects:
    // the operator is always told something. A run that fails silently at 07:00 with no live turn
    // to notice is indistinguishable from one that never fired.
    var notice = $"Scheduled run \"{name}\" failed during the {failedStep} step: {error}";
    await _channelWriter.WriteAsync(
        new ChannelMessageQueued(row.ChannelId, row.ConversationId, notice), tx.GetDbTransaction(), ct)
        .ConfigureAwait(false);

    await db.SaveChangesAsync(ct).ConfigureAwait(false);
    await tx.CommitAsync(ct).ConfigureAwait(false);
}

public async ValueTask<ScheduledRunExecution?> FindAsync(Guid executionId, CancellationToken ct)
{
    return await _db.ScheduledRunExecutions.AsNoTracking()
        .SingleOrDefaultAsync(e => e.Id == executionId, ct).ConfigureAwait(false);
}
```

- [x] **Step 6: Add the column-drift guard**

The raw `INSERT` lists columns by hand, so adding a property to `ScheduledRunExecution` without touching it inserts a NULL or fails — and only at run time. Pin the coupling:

```csharp
[Fact]
public void The_insert_statement_lists_every_mapped_column()
{
    using var db = fixture.CreateContext();

    var mapped = db.Model.FindEntityType(typeof(ScheduledRunExecution))!
        .GetProperties()
        .Select(p => p.GetColumnName())
        .Where(c => !string.Equals(c, "xmin", StringComparison.Ordinal))   // system column; the database maintains it
        .ToHashSet(StringComparer.Ordinal);

    ScheduledRunExecutionStore.InsertColumnNames.ToHashSet(StringComparer.Ordinal)
        .Should().BeEquivalentTo(mapped,
            "TryBeginAsync writes this table with hand-written SQL; a property added to the entity without a " +
            "matching column here is inserted as NULL or rejected at run time, and only at run time");
}
```

Add `[assembly: InternalsVisibleTo("Daedalus.Tests.Integration")]` to `Daedalus.Agents` if it does not already have one — check before adding a second.

- [x] **Step 7: Run all the tests, then run them five times**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ScheduledRunExecutionStoreTests`
Expected: PASS, 11 tests.

```bash
for i in 1 2 3 4 5; do
  dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ScheduledRunExecutionStoreTests --nologo || echo "FAILED run $i"
done
```

Expected: five clean runs. The racing test is genuinely concurrent, and transactional tests against containers are exactly where intermittent failures hide — a suite that passes four times in five is a failing suite.

- [x] **Step 8: See the two guards fail**

Both are claims the phase rests on, and both are the shape that has false-passed here before.

1. Delete the `if (row.Step != expected)` check. Re-run: `Redelivering_a_step_command_does_not_re_run_the_step` must fail. Restore.
2. Change `ON CONFLICT ("ScheduleId", "OccurrenceAt") DO NOTHING` to a plain `INSERT`. Re-run: `Redelivering_the_same_ScheduledRunDue_creates_exactly_one_execution` must fail. Restore.

- [x] **Step 9: Commit**

```bash
git add src/Daedalus.Agents tests/Daedalus.Tests.Integration
git commit -m "feat(scheduling): add the execution store with per-occurrence idempotency and per-step advance"
```

---

### Task 14: `SubagentRunExecutor` — the single `ISubagentRunner` seam

Replaces plan B Task 9. Same intent — **one type in Daedalus touches `ISubagentRunner`** — with the mediator removed and the real 0.5.0 signatures.

**Files:**
- Modify: the Thalos.NET version pin — `Directory.Packages.props` if central package management is on, otherwise each `.csproj`
- Create: `src/Daedalus.Agents/Scheduling/DetachedRunOptions.cs`
- Create: `src/Daedalus.Agents/Scheduling/DetachedPrincipal.cs`
- Create: `src/Daedalus.Agents/Scheduling/ISubagentRunExecutor.cs`
- Create: `src/Daedalus.Agents/Scheduling/SubagentRunExecutor.cs`
- Create: `src/Daedalus.Agents/Scheduling/RepoDigestPrompts.cs`
- Test: `tests/Daedalus.Tests.Unit/Scheduling/SubagentRunExecutorTests.cs`

**Interfaces:**
- Consumes: `ISubagentRunner`, `IAgentCatalog`, `IOptions<DetachedRunOptions>`.
- Produces:
  - `interface ISubagentRunExecutor { ValueTask<Result<string, AgentError>> RunAsync(string agentName, string task, string principalId, IReadOnlyList<string> roles, CancellationToken ct); }`
  - `sealed class DetachedRunOptions { string PrincipalId; IReadOnlyList<string> Roles; int MaxTotalTokens; int DeadlineSeconds; }`
  - `sealed class DetachedPrincipal : ISecurityContext`
  - `static class RepoDigestPrompts { const string ScoutAgent = "scout"; const string WriterAgent = "writer"; const string ScoutTask; static string WriterTask(string findings); static IReadOnlyList<string> AgentNames; }`

- [x] **Step 1: Bump Thalos.NET to 0.5.0**

Move every `Thalos.NET*` package to `0.5.0`, then:

```bash
dotnet restore && dotnet build --nologo
```

Expected: clean. 0.4.0 to 0.5.0 is additive for what Daedalus consumes; if anything breaks, fix it here rather than inside a later task.

> If the build fails resolving a `Rag.NET.Abstractions` or `rag.net.parsers.audio` **1.0.0**, check `%USERPROFILE%\.nuget\packages\<id>\1.0.0\.nupkg.metadata` for a `source` under a `claude` scratchpad path. Locally-packed copies shadowing the nuget.org packages have contaminated this machine's cache before, making `main` fail to compile locally while CI built it fine. Purge the directory and re-restore rather than working around it.

- [x] **Step 2: Write the failing tests**

```csharp
public class SubagentRunExecutorTests
{
    private static readonly AgentId ScoutId = new(Guid.Parse("7c2a1f40-9b3e-4d58-8a16-2e5c9f0b4d71"));

    [Fact]
    public async Task A_successful_run_returns_the_turn_text()
    {
        var runner = Substitute.For<ISubagentRunner>();
        runner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
              .Returns(Result<AgentTurnResult, AgentError>.Success(TurnWith("three open PRs")));

        var result = await Executor(runner).RunAsync("scout", "sweep", "schedule:daedalus", ["reader"], default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("three open PRs");
    }

    [Fact]
    public async Task A_failed_run_returns_the_error_and_does_not_throw()
    {
        // a throw would escape into the outbox dispatcher as an infrastructure fault and burn eight
        // retries re-running the same failing turn; an AgentError is an outcome the caller reports
        var runner = Substitute.For<ISubagentRunner>();
        runner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
              .Returns(Result<AgentTurnResult, AgentError>.Failure(AgentError.ProviderError("529", "overloaded")));

        var result = await Executor(runner).RunAsync("scout", "sweep", "schedule:daedalus", ["reader"], default);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        result.Error.Message.Should().Be("529");
    }

    [Fact]
    public async Task The_run_executes_as_the_supplied_detached_principal_not_a_human()
    {
        // spec D7 and §4, made falsifiable
        var runner = Substitute.For<ISubagentRunner>();
        SubagentRunRequest? captured = null;
        runner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
              .Returns(Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        await Executor(runner).RunAsync("scout", "sweep", "schedule:daedalus", ["reader"], default);

        captured!.Caller.Id.Should().Be("schedule:daedalus");
        captured.Caller.Roles.Should().BeEquivalentTo(["reader"]);
        captured.ParentSessionId.Should().BeNull("a scheduled run has no parent turn");
        captured.Depth.Should().Be(0);
    }

    [Fact]
    public async Task The_configured_budget_is_applied_to_every_run()
    {
        var runner = Substitute.For<ISubagentRunner>();
        SubagentRunRequest? captured = null;
        runner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
              .Returns(Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        await Executor(runner, maxTokens: 50_000, deadlineSeconds: 300)
            .RunAsync("scout", "sweep", "schedule:daedalus", ["reader"], default);

        captured!.Budget.Should().Be(new SubagentBudget(50_000, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task An_unknown_agent_name_returns_a_failure_naming_the_agent_rather_than_throwing()
    {
        var result = await Executor(Substitute.For<ISubagentRunner>())
            .RunAsync("nope", "sweep", "schedule:daedalus", ["reader"], default);

        result.IsFailure.Should().BeTrue();
        // asserting on Message as well as Code is deliberate: AgentErrorCode.Validation is member 0, so
        // a Code-only assertion passes against a default(AgentError) that no code ever produced
        result.Error.Message.Should().Contain("nope");
    }

    [Fact]
    public async Task Agent_names_resolve_case_insensitively()
    {
        // agent names are typed by humans on phones; ChannelPump already resolves them this way
        var runner = Substitute.For<ISubagentRunner>();
        runner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
              .Returns(Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        var result = await Executor(runner).RunAsync("SCOUT", "sweep", "schedule:daedalus", ["reader"], default);

        result.IsSuccess.Should().BeTrue();
    }

    private static AgentTurnResult TurnWith(string text) =>
        new(TurnId.New(), new SessionId(Guid.Empty), text, default, [], TimeSpan.Zero);
}
```

`Executor(...)` builds a `SubagentRunExecutor` over the substituted runner, an `IAgentCatalog` whose `Agents` contains one `AgentDefinition` named `"scout"` with `Id = ScoutId`, and a `DetachedRunOptions`. Replace `TurnWith`'s `default` usage with whatever `TurnUsage` actually requires once it is in front of you — no assertion reads it.

**On the `AgentError` trap:** `AgentErrorCode.Validation` is enum member 0, so `default(AgentError)` is indistinguishable from a real validation failure. That produced false-passing tests three times on one branch, in three files, from two implementers. If this suite has the `ShouldBeFailureWith` helper, use it here. If not, every failure assertion in this file must check `Message` as well as `Code` — as the unknown-agent test above does.

- [x] **Step 3: Run and verify they fail**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter FullyQualifiedName~SubagentRunExecutorTests`
Expected: FAIL — the type does not exist.

- [x] **Step 4: Implement `DetachedPrincipal`**

It sits beside `ClaimsSecurityContext` in shape, but is built from configuration and never from an ambient user:

```csharp
using System.Collections.Frozen;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The identity a detached run executes under. Constructed from <see cref="DetachedRunOptions"/> and from the
///     principal copied onto the execution row at claim time — never from an ambient <c>ClaimsPrincipal</c>.
/// </summary>
/// <remarks>
///     A scheduled run has no human behind it. Borrowing the identity of whoever created the schedule would let a
///     run act with that person's authority hours later, after their access may have changed, with no live turn to
///     notice. Spec §4 and D7.
/// </remarks>
public sealed class DetachedPrincipal(string id, IReadOnlyList<string> roles) : ISecurityContext
{
    /// <inheritdoc />
    public string Id { get; } = id;

    /// <inheritdoc />
    public IReadOnlySet<string> Roles { get; } = roles.ToHashSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Claims { get; } = FrozenDictionary<string, string>.Empty;
}
```

- [x] **Step 5: Implement `SubagentRunExecutor`**

```csharp
public async ValueTask<Result<string, AgentError>> RunAsync(
    string agentName, string task, string principalId, IReadOnlyList<string> roles, CancellationToken ct)
{
    var definition = _catalog.Agents
        .FirstOrDefault(a => string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));

    if (definition is null)
    {
        // IAgentCatalog has no name-based lookup — only TryGet(AgentId, out _) — so this scan is the
        // whole resolution. Returning rather than throwing keeps a misconfigured schedule a reportable
        // outcome instead of an outbox retry storm.
        LogUnknownAgent(_logger, agentName);
        return Result<string, AgentError>.Failure(
            AgentError.Validation($"No agent named '{agentName}' is registered."));
    }

    var request = new SubagentRunRequest
    {
        AgentId = definition.Id,
        Task = task,
        Caller = new DetachedPrincipal(principalId, roles),
        Budget = new SubagentBudget(_options.MaxTotalTokens, TimeSpan.FromSeconds(_options.DeadlineSeconds)),
        Depth = 0,                 // no parent turn: the depth guard is about a subagent spawning subagents
        ParentSessionId = null,
    };

    var result = await _runner.RunAsync(request, ct).ConfigureAwait(false);
    return result.IsSuccess
        ? Result<string, AgentError>.Success(result.Value.Text)
        : Result<string, AgentError>.Failure(result.Error);
}
```

Plan A's rule stands and must not be re-litigated in the docs here: **a deadline stops work, a budget settles it.** The budget is a post-hoc check because `RunTurnAsync` is buffered and nothing can halt a turn mid-flight; it converts an overspend into a reported failure and bounds the next step.

`RepoDigestPrompts` holds the two agent names and the two prompt texts. Write real prompt text, not a placeholder — the scout sweeps the repository for what changed since the last digest; the writer turns those findings into a short, chat-shaped summary. `AgentNames` returns `[ScoutAgent, WriterAgent]` so plan B Task 8's startup validator can check both without duplicating the strings.

- [x] **Step 6: Run, then see the identity test fail**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter FullyQualifiedName~SubagentRunExecutorTests`
Expected: PASS, 6 tests.

Then replace `Caller` with a hard-coded `new DetachedPrincipal("someone", ["admin"])`, re-run, and confirm `The_run_executes_as_the_supplied_detached_principal_not_a_human` fails. Revert.

- [x] **Step 7: Commit**

```bash
git add src tests/Daedalus.Tests.Unit
git commit -m "feat(scheduling): run subagents as the configured detached principal through one seam"
```

Body: note the Thalos.NET bump to 0.5.0 and that this replaces plan B Task 9's mediator-based handler. Keep parentheses unnested — release-please drops commits containing a nested `(`.

---

### Task 15: The step dispatchers

Five dispatchers, each thin: load, call, hand the outcome to the store. All the transactional work is in Task 13; all the subagent work is in Task 14.

**Files:**
- Modify: `src/Daedalus.Agents/Scheduling/ScheduledRunDueDispatcher.cs` — supersedes plan B Task 5 steps 3 and 4
- Create: `src/Daedalus.Agents/Scheduling/StepDispatchers.cs`
- Test: `tests/Daedalus.Tests.Integration/Scheduling/ScheduledRunFlowTests.cs`

**Interfaces:**
- Consumes: `ScheduledRunExecutionStore`, `ISubagentRunExecutor`, `RepoDigestPrompts`.
- Produces: `ScheduledRunDueDispatcher : IOutboxDispatcher<ScheduledRunDue>`, `RunScoutStepDispatcher : IOutboxDispatcher<RunScoutStep>`, `RunWriterStepDispatcher : IOutboxDispatcher<RunWriterStep>`, `DeliverDigestDispatcher : IOutboxDispatcher<DeliverDigest>`.

- [x] **Step 1: Write the dispatchers**

```csharp
/// <summary>Starts one execution per occurrence. Idempotency is the store's unique key, not this type's memory.</summary>
public sealed class ScheduledRunDueDispatcher(ScheduledRunExecutionStore store) : IOutboxDispatcher<ScheduledRunDue>
{
    /// <inheritdoc />
    public async ValueTask DispatchAsync(ScheduledRunDue message, CancellationToken ct) =>
        await store.TryBeginAsync(message, ct).ConfigureAwait(false);
}

/// <summary>Runs the scout and hands its findings to the store, which persists them and enqueues the writer step.</summary>
public sealed partial class RunScoutStepDispatcher(
    ScheduledRunExecutionStore store,
    ISubagentRunExecutor executor,
    ILogger<RunScoutStepDispatcher> logger) : IOutboxDispatcher<RunScoutStep>
{
    /// <inheritdoc />
    public async ValueTask DispatchAsync(RunScoutStep message, CancellationToken ct)
    {
        var execution = await store.FindAsync(message.ExecutionId, ct).ConfigureAwait(false);
        if (execution is null)
        {
            LogUnknownExecution(logger, message.ExecutionId);
            return;
        }

        // Cheap pre-check before paying for a turn. The store re-checks inside its transaction, which is
        // what actually settles a race; this one only avoids billing for a step that is already done.
        if (execution.Step != RunStep.Scout) { return; }

        var result = await executor.RunAsync(
            RepoDigestPrompts.ScoutAgent, RepoDigestPrompts.ScoutTask,
            execution.PrincipalId, execution.Roles, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            await store.FailAsync(message.ExecutionId, Describe(result.Error), ct).ConfigureAwait(false);
            return;
        }

        await store.TryCompleteScoutAsync(message.ExecutionId, result.Value, ct).ConfigureAwait(false);
    }

    private static string Describe(AgentError error) => $"{error.Code}: {error.Message}";
}
```

`RunWriterStepDispatcher` is the same shape against `RunStep.Writer`, calling `RepoDigestPrompts.WriterTask(execution.Findings!)` and `TryCompleteWriterAsync`. `DeliverDigestDispatcher` makes no subagent call at all — it checks `RunStep.Deliver` and calls `TryCompleteDeliveryAsync`.

**No dispatcher throws.** Every failure path ends in `FailAsync`, which records the error and queues the operator notice. A throw would hand the message back to the outbox for eight retries with exponential backoff, re-running the subagent each time — paying for the same failing turn eight times over, with nobody told until it dead-letters.

- [x] **Step 2: Note how they get registered**

Registration lives in `AddDaedalusScheduling` (Task 16). The generated `AddXOutbox()` calls `TryAddTransient` for `IOutboxDispatcher<T>` with ZeroAlloc.Outbox's throwing `DefaultOutboxDispatcher<T>`, so these must be installed with `Replace` — exactly as `AddDaedalusChannels` does for `ChannelMessageQueuedDispatcher`. Read that method and copy its approach rather than inventing a second one; Task 16's test pins the outcome.

- [x] **Step 3: Write the failing end-to-end tests**

These are the phase's acceptance tests. They drive the real dispatchers over Testcontainers Postgres with `ScriptedChatClient` from `Thalos.NET.Testing` and a fake `IChannelAdapter` recording deliveries.

```csharp
[Fact]
public async Task A_due_schedule_produces_a_digest_delivered_to_the_fake_adapter()

[Fact]
public async Task Redelivering_the_ScheduledRunDue_runs_the_subagents_once()

[Fact]
public async Task A_failure_in_the_writer_step_delivers_a_failure_notice_naming_the_schedule()

[Fact]
public async Task A_restart_between_the_scout_and_writer_steps_resumes_without_re_running_the_scout()

[Fact]
public async Task The_delivered_text_is_the_writer_output_not_the_scout_findings()
```

Write each body out. The second and fourth are the two claims that justify the whole redesign, so they must assert on the **scripted client's call count**, not only on the final delivery: a resume that silently re-ran the scout still delivers exactly one digest, and would pass a delivery-only assertion while paying twice. The fourth models a restart the way Task 13's test does — construct fresh dispatchers over the same database between steps, so nothing in memory carries over.

- [x] **Step 4: Run, then run five times**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~ScheduledRunFlowTests`
then the same in a loop of five. Expected: PASS every time.

- [x] **Step 5: Commit**

```bash
git add src/Daedalus.Agents tests/Daedalus.Tests.Integration
git commit -m "feat(scheduling): drive scheduled runs step by step through the outbox"
```

Body: note that this gives `ChannelMessageQueued` its first writer, closing what phase 1.4 left open. Keep parentheses unnested.

---

### Task 16: The sweeper job, one DI entry point, and host wiring

Folds plan B Tasks 11 and 12. The sweeper job is unchanged; the DI extension loses the saga registration; the host test pins the real hosted-service set rather than the four pollers the old spec assumed.

**Files:**
- Create: `src/Daedalus.Agents/Scheduling/ScheduleSweeperJob.cs`
- Create: `src/Daedalus.Agents/DaedalusSchedulingServiceCollectionExtensions.cs`
- Modify: `src/Daedalus.Api/Program.cs`, `src/Daedalus.Cli/Program.cs`
- Test: `tests/Daedalus.Tests.Unit/Scheduling/ScheduleSweeperJobTests.cs`
- Test: `tests/Daedalus.Tests.Integration/Scheduling/ApiHostSchedulingWiringTests.cs`

**Interfaces:**
- Consumes: `ScheduledRunStore` from plan B Task 6; everything produced by Tasks 12–15.
- Produces: `ScheduleSweeperJob : IJob` with `[Job(Every = Every.Minute)]`; `AddDaedalusScheduling(this IServiceCollection, IConfiguration)`.

- [x] **Step 1: Write the job**

Unchanged from plan B Task 11 — thin on purpose, because `ScheduledRunStore` is already covered by transactional integration tests and logic here would need the scheduler running to exercise.

```csharp
/// <summary>Fires every schedule that has come due. All the work is in <see cref="ScheduledRunStore"/>.</summary>
[Job(Every = Every.Minute)]
public sealed partial class ScheduleSweeperJob : IJob
{
    /// <inheritdoc />
    public async ValueTask ExecuteAsync(JobContext ctx, CancellationToken ct)
    {
        var fired = await _store.ClaimAndEnqueueDueAsync(ct).ConfigureAwait(false);
        if (fired > 0) LogFired(_logger, fired);
    }
}
```

Its unit test asserts the job delegates to the store and logs only when something fired.

- [x] **Step 2: Write the DI extension**

```csharp
public static IServiceCollection AddDaedalusScheduling(this IServiceCollection services, IConfiguration configuration)
{
    services.Configure<DetachedRunOptions>(configuration.GetSection("DetachedRuns"));

    // No saga registration: ZeroAlloc.Saga is not referenced by this solution. See
    // docs/plans/2026-09-17-scheduled-runs-without-saga-design.md.
    // CORRECTION (2026-09-18) — the three lines below were written from a README and NONE of them
    // exist. Probed off ZeroAlloc.Scheduling 1.2.46 and .EfCore 1.2.46:
    //   AddScheduling(IServiceCollection, Action<SchedulingOptions> configure)  -- configure is REQUIRED
    //   WithEfCore(ISchedulingBuilder, Action<...> configure)                   -- NOT WithEfCoreStore,
    //                                                                              and NOT generic
    //   the generator emits Add{TypeName}Job, so a class named ScheduleSweeperJob yields the
    //   doubled-suffix AddScheduleSweeperJobJob(). That is correct, not a typo.
    //
    // THE HEADLINE: EfCoreJobStore's constructor is ctor(SchedulingDbContext db). Scheduling state
    // does NOT live in ApplicationDbContext the way the outbox does — the package ships its own
    // SchedulingDbContext, so this needs its own connection wiring and its own migration story.
    // Resolve that before writing the DI extension.
    //
    // Confirmed good news: SchedulingWorkerService's ctor is
    //   (IServiceScopeFactory, IOptionsMonitor<SchedulingOptions>, ILogger<...>, IEnumerable<IJobTypeExecutor>)
    // so jobs ARE resolved per execution from a DI scope, and the scoped ScheduledRunStore works
    // inside a job without hand-rolled scope management. The hosted-service type name this plan
    // guessed for the host-wiring test is also right.
    services.AddScheduling(/* configure */)
            .WithEfCore(/* configure */)
            .AddScheduleSweeperJobJob();

    services.AddSingleton<ScheduledRunStore>();
    services.AddSingleton<ScheduledRunExecutionStore>();
    services.AddSingleton<ISubagentRunExecutor, SubagentRunExecutor>();

    // Replace, not Add: the generated AddXOutbox calls TryAddTransient with ZeroAlloc.Outbox's throwing
    // DefaultOutboxDispatcher, so a plain Add would leave registration order deciding which one wins.
    services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<ScheduledRunDue>, ScheduledRunDueDispatcher>());
    services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<RunScoutStep>, RunScoutStepDispatcher>());
    services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<RunWriterStep>, RunWriterStepDispatcher>());
    services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<DeliverDigest>, DeliverDigestDispatcher>());

    services.AddHostedService<ScheduleStartupValidator>();   // plan B Tasks 7 and 8
    return services;
}
```

**Do not call `AddChannelOutbox` or any `AddOutbox` here.** The outbox — including the four scheduling writers — is wired by `AddDaedalusAgents`, and a second call would start a second poller. Document that on the method, the way `AddDaedalusChannels` already documents the same hazard.

Verify `AddScheduling`, `WithEfCoreStore` and `AddScheduleSweeperJob` against `ZeroAlloc.Scheduling`'s README **and** against its shipped assembly before relying on the names. The outbox library's fluent names did not match what its README implied, and that is the mistake this plan has already had to correct once. The package is not in the local NuGet cache, so it could not be probed while this plan was written.

- [x] **Step 3: Write the failing host-wiring test**

Model it on `ApiHostChannelWiringTests`, which already boots the real host.

```csharp
[Fact]
public void The_api_host_registers_exactly_one_of_each_background_worker()
{
    var hosted = _factory.Services.GetServices<IHostedService>().ToList();

    hosted.Count(h => h is OutboxWorkerService).Should().Be(1,
        "one AddOutbox call, one OutboxMessages table, one poller — channel and scheduling messages share it");
    hosted.Count(h => h is SchedulingWorkerService).Should().Be(1);
    hosted.Count(h => h is ScheduleStartupValidator).Should().Be(1);
    hosted.Count(h => h is AgentSessionCrashRecovery).Should().Be(1);
}

[Fact]
public void The_api_host_resolves_every_scheduling_dispatcher_as_the_real_implementation()
{
    var services = _factory.Services;

    services.GetRequiredService<IOutboxDispatcher<ScheduledRunDue>>().Should().BeOfType<ScheduledRunDueDispatcher>();
    services.GetRequiredService<IOutboxDispatcher<RunScoutStep>>().Should().BeOfType<RunScoutStepDispatcher>();
    services.GetRequiredService<IOutboxDispatcher<RunWriterStep>>().Should().BeOfType<RunWriterStepDispatcher>();
    services.GetRequiredService<IOutboxDispatcher<DeliverDigest>>().Should().BeOfType<DeliverDigestDispatcher>(
        "leaving DefaultOutboxDispatcher in place would dead-letter every step instead of running it");
}

[Fact]
public void The_cli_host_registers_the_same_set()
```

Replace `SchedulingWorkerService` with `ZeroAlloc.Scheduling`'s actual hosted-service type name once you have read the package. Do not guess it.

- [x] **Step 4: Wire both hosts**

Each host calls `AddDaedalusAgents`, `AddDaedalusChannels` **and** `AddDaedalusScheduling`. If two workers of a type appear, fix the registration — do not relax the assertion.

- [x] **Step 5: Run the whole integration suite**

Run: `dotnet test tests/Daedalus.Tests.Integration`
Expected: green except the environmental `AuthenticationFlowTests` from plan B's Task 2 baseline, and only if `traefik` still holds port 8080. Any other delta must be explained before continuing.

- [x] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(hosts): wire scheduling into the api and cli hosts"
```

---

### Task 17: Architecture rules, config, and the full verification pass

Replaces plan B Task 13. The Saga ban changes meaning: the rule is no longer "keep the saga library out of the domain" but "this solution does not reference `ZeroAlloc.Saga` at all".

**Files:**
- Modify: `tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs`
- Modify: `src/Daedalus.Api/appsettings.json`, `src/Daedalus.Cli/appsettings.json`
- Modify: `src/Daedalus.Agents/Scheduling/ScheduleReconciler.cs` — the `Trigger` validation from plan B Task 7
- Modify: `docs/planning/STATE.md` via `pause-work`, not by hand

- [x] **Step 1: Correct the configuration from plan B Task 7**

`Trigger` named a saga class. There are no sagas. The value becomes the workflow name, and `DetachedRuns` grows the budget the executor reads.

```json
"DetachedRuns": {
  "PrincipalId": "schedule:daedalus",
  "Roles": [ "reader" ],
  "MaxTotalTokens": 50000,
  "DeadlineSeconds": 300
},
"ScheduledRuns": [
  {
    "Name": "daily-digest",
    "Cron": "0 7 * * *",
    "Trigger": "RepoDigest",
    "ChannelId": "telegram",
    "ConversationId": "REPLACE_WITH_CHAT_ID",
    "Enabled": false
  }
]
```

`Enabled: false` stays: the Telegram path has still never been exercised end to end, so a fresh clone must not start pushing to an unverified chat id on a timer.

Plan B Task 7's reconciler validates that `Trigger` "resolves to a registered saga name". Change it to validate against a `KnownTriggers` set containing `"RepoDigest"`, with the error listing the known values. One workflow exists; the validation exists so that adding a second is a compile-and-config change rather than an 07:00 surprise.

- [x] **Step 2: Add the architecture rules**

Three rules, in the shape of the existing ones in `CleanArchitectureTests`:

1. **No project in the solution references `ZeroAlloc.Saga`.**
2. **`Daedalus.Domain` must not depend on `ZeroAlloc.Scheduling`, `ZeroAlloc.Outbox` or `Cronos`** — anchored namespace patterns, following `ThalosNamespacePattern`'s `^Thalos(\.|$)` style. Cron parsing lives in the reconciler and step messages live in `Daedalus.Agents` precisely so this holds.
3. **Only `SubagentRunExecutor` may depend on `ISubagentRunner`** — the single-seam claim from Task 14, enforced rather than documented.

Rule 1 cannot be an ArchUnitNET rule. ArchUnitNET only sees loaded assemblies, and `ZeroAlloc.Saga` is not referenced — so a namespace rule against it would be **vacuously true**, which is the exact trap the existing `DomainLayer_ShouldNotDependOn_EfCore` test documents by explicitly loading EF Core to stay non-vacuous. Since the package must not be referenced at all, assert on the project files:

```csharp
[Fact]
public void No_project_references_ZeroAlloc_Saga()
{
    var offenders = Directory
        .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(p => File.ReadAllText(p).Contains("ZeroAlloc.Saga", StringComparison.OrdinalIgnoreCase))
        .ToList();

    offenders.Should().BeEmpty(
        "a saga never receives its trigger event: ZeroAlloc.Mediator's generated Publish dispatches to a closed " +
        "list of concrete handler types in its own compilation and never enumerates INotificationHandler<T> from " +
        "DI. See docs/plans/2026-09-16-saga-efcore-spike.md and ZeroAlloc.Saga issue 127. Phase 1.5 removed the " +
        "dependency; re-adding it compiles and then silently does nothing at run time.");
}
```

Find `RepositoryRoot` the way the suite already locates solution-relative paths if it has a helper; otherwise walk up from `AppContext.BaseDirectory` to the directory containing the `.sln`. Confirm the test is non-vacuous by adding a `ZeroAlloc.Saga` `PackageReference` to a scratch `.csproj` under the repo, watching it fail, then deleting it.

- [x] **Step 3: Run every test project separately**

```bash
for p in $(dotnet sln list | grep -i 'tests'); do
  echo "=== $p ==="
  dotnet test "$p" --nologo 2>&1 | tail -3
done
```

Compare against plan B Task 2's baseline. **Every delta must be explained.** New passes are the point; any new failure blocks the phase. The two known-failing suites must still fail with the *same* counts — `Daedalus.Tests.Playwright.Api` at 126/126 from its pre-existing `E2EServerFixture` ordering bug, and the environmental `AuthenticationFlowTests`. A change there means this phase touched something it should not have.

Treat "did not run" as distinct from "passed". A suite that cannot execute — Docker down, image missing — is not a green suite, and reporting it as one is exactly how a 53-commit-stale baseline went unnoticed on the plan A branch.

- [x] **Step 4: Run the new suites five times**

Loop over `Daedalus.Tests.Integration` and `Daedalus.Tests.Unit`. Five clean runs each.

- [x] **Step 5: Verify the real thing actually runs**

```bash
dotnet run --project src/Daedalus.AppHost
```

Set `daily-digest` to `Enabled: true` with a `ConversationId` you control and a cron a minute or two out. Confirm, in order:

1. the sweeper claims the row and `NextRunAt` advances;
2. a `ScheduledRunDue` row appears in `OutboxMessages` and is dispatched;
3. exactly one `ScheduledRunExecutions` row exists for the occurrence;
4. `Step` walks `Scout` to `Writer` to `Deliver` to `Done`, with `Findings` and `Digest` filled in as it goes;
5. a `ChannelMessageQueued` row appears in the same transaction as `Done`;
6. the adapter delivers.

Then **kill the host between 4 and 5 and restart it**, and confirm the run resumes at its persisted step without re-running the earlier subagents. That is the redesign's central claim, and this is the only place it meets a real crash rather than a modelled one.

Phase 1.4's retrospective is explicit that several defects were caught only because someone **ran** something rather than reading it. Do not skip this because the integration tests are green.

If Aspire appears to hang on start, check for orphaned `dcp.exe` or dashboard processes from a killed run holding ports. If Keycloak ignores a realm change, `docker rm -f daedalus-realm-*` — Aspire reuses the existing container.

- [x] **Step 6: Write down the two things the design accepted**

Both belong in code, where an implementer will meet them, not only in the design document.

1. The bounded guarantee, on `ScheduledRunExecution` and on `RunScoutStep`: **a crash between a subagent returning and its step committing re-runs that step and pays its tokens twice — one step can be lost, never the whole run.**
2. The extensibility cost, on `DaedalusSchedulingServiceCollectionExtensions`: a new workflow now needs new step commands, their dispatchers and a `Trigger` value, where the saga promised a single `[Saga]` class. This was accepted because the alternative is a library that cannot be driven at all.

- [x] **Step 7: Pre-push review**

Run the `pre-push-review` skill against the branch. Phase 1.4's review found four pieces of dead API and a test failing 4 runs in 7 while reported green.

- [x] **Step 8: Finish the branch**

Use `superpowers:finishing-a-development-branch`. Then `complete-phase` marks 1.5 complete in ROADMAP.md and MILESTONE.md, and `pause-work` writes STATE.md.

Do **not** hand-edit the planning files — those sub-skills write and verify them.

---

## Self-Review

**Spec coverage** — against `docs/plans/2026-09-17-scheduled-runs-without-saga-design.md`:

| Design section | Task |
|---|---|
| `ScheduledRunExecutions`, every column | 10, 11 |
| `UNIQUE (ScheduleId, OccurrenceAt)` as the correlation key | 11 |
| Target and principal **copied** at claim time, not joined at run time | 10, 13 |
| Flow: due to scout to writer to deliver, one transaction per step | 13, 15 |
| Idempotency layer 1 — `ON CONFLICT DO NOTHING` | 13 |
| Idempotency layer 2 — step check plus `RowVersion` OCC | 13 |
| Terminal step writes `ChannelMessageQueued` in its own transaction | 13, 15 |
| `ISubagentRunner` reached through exactly one seam | 14, 17 |
| Compensation replaced by `Failed` plus `LastError` plus operator notice | 13, 15 |
| `ZeroAlloc.StateMachine` not adopted | n/a — no dependency added; `RunStep` is a plain enum |
| Wiring: no saga store worker; host-boot test pins the set | 16 |
| Every test case the design lists | 11, 13, 15 |
| Bounded guarantee stated in the docs | Global Constraints; 12 step 1; 17 step 6 |
| Extensibility cost recorded | 17 step 6 |
| Impact table rows 10 / 11 / 12 / 13 | 10–13 / 16 / 16 / 17 |

**Where this plan departs from the design, and why:** the design's "three pollers", and its claim that plan B Tasks 2–9 are unaffected, are both contradicted by the code. Corrections 1–3 at the top give the evidence. No approved *decision* is changed.

**Placeholder scan.** One defer-to-the-library remains: `ZeroAlloc.Scheduling`'s fluent method names and its hosted-service type name, in Task 16 steps 2 and 3, flagged at both points of use. The package is not in the local NuGet cache, so it could not be probed the way `ZeroAlloc.Outbox` and `Thalos.NET` were. Plan B's other two deferrals are resolved: `IOutboxWriter.WriteAsync`'s signature is verified, and the "may `[Step]` methods do I/O" question dissolved with the saga.

Two sets of test bodies are specified by name and assertion rather than written out: Task 15 step 3's five flow tests, and Task 16 step 3's CLI-host test. Each names the exact assertion that matters and why a weaker one would false-pass. That is a deliberate limit — those bodies depend on `ScriptedChatClient`'s and `PostgresFixture`'s real shape, which the implementer will have open and this author did not.

**Type consistency.** `ScheduledRunExecution.Create(Guid, DateTime, string, string, string, IReadOnlyList<string>, DateTime) -> Result<ScheduledRunExecution>` has one shape in Tasks 10, 11 and 13. `RunStep`'s members are spelled identically in 10, 11, 13 and 15. `ScheduledRunDue(Guid ScheduleId, DateTime OccurrenceAtUtc)` matches plan B Task 6's producer. `RunScoutStep`, `RunWriterStep` and `DeliverDigest` each carry exactly `Guid ExecutionId` in 12, 13 and 15. `ISubagentRunExecutor.RunAsync(string, string, string, IReadOnlyList<string>, CancellationToken) -> ValueTask<Result<string, AgentError>>` has one shape in 14 and 15. `ScheduledRunExecutionStore`'s six methods are spelled identically in 13, 15 and 16.

**One ordering risk.** Task 14 bumps Thalos.NET from 0.4.0 to 0.5.0 across the solution. It sits there rather than first because nothing before it needs 0.5.0, so a failure in the bump cannot invalidate Tasks 10–13. If the bump breaks unrelated code, that is its own fix and does not reshape the phase.
