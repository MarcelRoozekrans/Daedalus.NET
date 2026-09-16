# Phase 1.5 — Subagents & autonomous runs: `ISubagentRunner` + `ZeroAlloc.Saga` + `ZeroAlloc.Scheduling`

**Date:** 2026-09-16 · **Milestone:** 1 (Hermes-style agent framework) · **Issue:** #231 · **Depends on:** phase 1.1
(design `docs/plans/2026-08-16-thalos-agent-core-design.md`) and phase 1.4
(design `docs/plans/2026-08-20-thalos-channels-design.md`, Thalos.NET 0.4.0 on nuget.org)

## 1. Goal

Give Thalos agents the ability to **act without being asked**. Phases 1.1–1.4 built an agent that answers when
spoken to: every turn has a live caller holding a socket open — an HTTP request, a Telegram message, a console
prompt. This phase makes the opposite case first-class: a turn with **no live caller**, triggered by a clock
rather than a person, whose result has to find its own way back to a human.

That single idea — *a turn with no live caller* — unifies both halves of the phase name. A subagent step inside
an orchestrated workflow and a cron-fired autonomous run are the same primitive triggered differently, and the
design treats them as one thing rather than two.

It also supplies the writer the previous phase deliberately left missing. Phase 1.4 built, tested and wired
`ZeroAlloc.Outbox` for durable outbound delivery, then discovered during execution that routing *terminal* turn
messages through it would break the edit-in-place streaming and post every reply twice (see §9's correction in
the channels design). The infrastructure was kept because unsolicited pushes — which have no live turn to stream
into and therefore no conflict — were always going to be its real writer. This is that phase.

### In scope

- `ISubagentRunner` in Thalos.NET — run one agent turn detached from any live caller, with budget, deadline and
  depth guards. Ships as **Thalos.NET 0.5.0**.
- Two parked upstream defects folded into the same release (see §2, D6).
- Developer-authored `[Saga]` workflows in Daedalus, orchestrating subagent steps with compensation.
- A `ScheduledRuns` table, startup reconciliation from configuration, and a sweeper job that fires due runs.
- The first real workflow: `RepoDigestSaga` — sweep repositories, summarise, deliver to Telegram.
- The outbox writer that closes phase 1.4's loop, for both success and failure.

### Out of scope (decisions, not omissions)

- **Agent-authored workflows.** Sagas are compile-time: the generator emits the FSM from `[Step]` methods, which
  is what makes them AOT-clean and reflection-free. An agent triggers a workflow you wrote; it cannot compose one.
  Decided in brainstorming (§2, D2).
- **Agent tools to create, list or cancel schedules**, and the Blazor page to manage them. Split into **phase 1.6**
  (§2, D5). The table and sweeper land here and are exercised by config-seeded rows, so 1.6 is additive.
- **A synchronous in-turn delegation tool.** A parent agent cannot call a subagent mid-turn and await it. Only
  saga steps run subagents in this phase. This is why the depth guard is insurance rather than load-bearing (§3).
- **Fixing `Daedalus.Tests.Playwright.Api`.** Pre-existing since phase 1.3 and untouched here; it is baselined,
  not repaired (§7).

## 2. Decisions taken during brainstorming

| # | Decision | Rationale |
|---|---|---|
| D1 | Subagents are **full saga orchestration**, not a delegation tool or fire-and-forget spawn | Chosen for durability across restarts and compensation on partial failure |
| D2 | **Developer-authored sagas only** — no runtime-composed plans | `[Saga]` is compile-time by construction; a generic "plan saga" whose steps are data would use the generator for almost nothing |
| D3 | Thalos gets **the primitive only**; no dependency on `ZeroAlloc.Saga` or `.Scheduling` | The library is on nuget for general consumption; a saga store, a job store and two hosted services are host policy, not framework policy. Mirrors how `Memory` holds the port and `Memory.RagNet` the opinionated adapter |
| D4 | Schedules are **rows in a Daedalus table** with a sweeper, not `[Job(Cron=…)]` classes alone | `IScheduler` offers only one-off and delayed enqueue; recurring is compile-time. A table is required for phase 1.6's agent-created schedules, and building it now avoids a migration later |
| D5 | Phase **split at the backend/UI seam** — 1.5 backend, 1.6 agent tools + Blazor | Seven work items across two repos including a UI surface is materially larger than 1.4, which took 31 tasks. Ralph retirement and the 1.0 release renumber to 1.7 and 1.8 |
| D6 | The two parked **0.4.x defects ship in 0.5.0** rather than a separate 0.4.1 | Plan A is cutting a release anyway. The `CreateAndBindAsync` fix is directly relevant: this phase adds a second path that delivers to a conversation without a live turn |
| D7 | A detached run executes as its **own narrow principal**, not as its creator | An unattended agent should not hold a human's full authority with nobody watching. Audit trails distinguish "Marcel asked" from "the 07:00 job did it" |
| D8 | Missed occurrences **fire once**, not once per missed slot | Yesterday's digest has no value today, and catch-up-all bills three subagent runs to deliver two documents nobody wants |
| D9 | First real workflow is a **repo/project digest** | Its sweep step can genuinely half-succeed across N repositories, so compensation is exercised rather than decorative |

### Package versions

Pinned at design time; confirmed published on nuget.org.

| Package | Version | Note |
|---|---|---|
| `ZeroAlloc.Saga` | 2.0.0 | 2.0.0 was a breaking generator fix |
| `ZeroAlloc.Saga.EfCore` | 1.3.0 | **Declares `ZeroAlloc.Saga >= 1.3.0`** — see the risk in §7 |
| `ZeroAlloc.Saga.Outbox` | 2.0.0 | Atomic step dispatch + state save |
| `ZeroAlloc.Scheduling` | 1.2.46 | |
| `ZeroAlloc.Scheduling.EfCore` | 1.2.46 | |

## 3. Architecture

```
TRIGGER          ScheduleSweeperJob (cron)        ──┐
                 API call / agent tool  (phase 1.6)──┤
                                                     ▼
ORCHESTRATION    [Saga] RepoDigestSaga                 Daedalus
                   Step 1 Sweep      ─┐
                   Step 2 Summarise  ─┼─ RunSubagentCommand
                   Step 3 Deliver    ─┘      │
                                             ▼
PRIMITIVE                          ISubagentRunner     Thalos.NET 0.5.0
                                     └─ fresh session, derived context,
                                        budget + deadline guards
                                             │
DELIVERY         outbox ─→ ChannelMessageQueuedDispatcher ─→ Telegram
                 (built in phase 1.4 — this phase supplies the writer)
```

### The Thalos primitive

```csharp
namespace Thalos;

/// <summary>Runs one agent turn with no live caller. Never streams; returns the finished result.</summary>
public interface ISubagentRunner
{
    ValueTask<Result<SubagentRunResult, AgentError>> RunAsync(
        SubagentRunRequest request, CancellationToken ct);
}

public sealed record SubagentRunRequest
{
    public required AgentId AgentId { get; init; }
    public required string Task { get; init; }
    public required ISecurityContext Caller { get; init; }
    public SubagentBudget Budget { get; init; }        // max tokens + deadline
    public int Depth { get; init; }                     // refused above configured max
    public SessionId? ParentSessionId { get; init; }    // lineage for telemetry only
}

public sealed record SubagentRunResult(SessionId SessionId, TurnId TurnId, string Text, TurnUsage Usage);
```

Internally: `CreateSessionAsync` → `RunTurnAsync` → `CloseSessionAsync`, with guards applied before the first
model call. It resolves agents through the existing `IAgentCatalog` — **a subagent is an ordinary
`AgentDefinition`**. No new catalogue, no "subagent" concept in configuration.

### What the guards are actually for

Being explicit, because two of the three are cheap insurance and one is load-bearing:

- **Budget (tokens) and deadline are load-bearing.** A cron firing an unattended multi-subagent saga every
  morning is precisely where a runaway loop costs real money with nobody watching it happen.
- **Depth is insurance.** Sagas are compile-time, so the step list is fixed and there is no unbounded recursion
  to prevent in this phase. It costs about five lines and forecloses the footgun on the day a delegation tool
  lands. It is not relied upon here, and the design does not pretend otherwise.
- **Fan-out is not guarded at all.** The saga decides how many steps it has, at compile time. A runner-side
  fan-out guard would be guarding a constant.

## 4. Identity: who is the caller when nobody is there

`IAgentRuntime.CreateSessionAsync` requires an `ISecurityContext`, and session ownership keys on `.Id`. A
cron-fired run has no inbound request to derive one from. Phase 1.4 already solved this shape:
`ConfiguredSecurityContext(id, roles)` exists so that every non-HTTP channel manufactures its identity the same
way rather than each inventing one. This phase reuses it; it does not add a third mechanism.

Three invariants:

1. **A detached run never invents authority.** Its context is reconstructed from a stored principal — the
   configured one for config-seeded schedules, and in phase 1.6 the creator recorded on the `ScheduledRuns` row.
2. **Sentinel stays in the loop, unchanged.** Tool authorization runs exactly as for a live turn. An unattended
   run is the last place to weaken the security layer; it is the case the layer was built for.
3. **Subagents inherit, they never widen.** A saga step runs as the saga's principal. No path exists by which a
   child ends up able to do something its parent could not.

Per D7 the run also executes as a **distinct, narrower principal**:

```json
"DetachedRuns": {
  "PrincipalId": "schedule:daily-digest",
  "Roles": [ "reader" ]
}
```

Read-only by default; widening is an explicit configuration edit. The audit trail then shows
`session.owner = schedule:daily-digest`, never `marcel`, so "the job did it" and "a human asked for it" are
distinguishable after the fact rather than by inference.

## 5. Scheduling

### The `ScheduledRuns` table

| Column | Purpose |
|---|---|
| `Id`, `Name` | `Name` is the natural key reconciliation matches on |
| `Cron`, `NextRunAt`, `LastRunAt` | Cronos computes `NextRunAt` from `Cron` |
| `Trigger` | Which saga to start |
| `ChannelId`, `ConversationId` | The delivery target — **carried, not looked up** |
| `Origin` | `Config` or `Agent` |
| `PrincipalId`, `Roles` | The §4 detached principal |
| `Enabled`, `RowVersion` | OCC |

`ChannelId` and `ConversationId` sit on the row because they must. `IConversationMap` answers only "which
session serves *this* conversation" — it cannot enumerate, and `GetBySessionAsync` was deliberately removed in
0.4.0 and must not be reintroduced. A schedule therefore cannot discover where to talk; it has to be told.

`Origin` earns its column in this phase despite nothing writing `Agent` yet. Startup reconciliation upserts
config-declared rows by `Name` and disables config rows that have vanished from configuration — and it must not
touch the rows phase 1.6's agent tools will create. Establishing that boundary now costs one column; retrofitting
it costs a migration plus a reconciliation bug found in production.

### The sweeper, and the double-fire problem

`[Job(Every = Every.Minute)] ScheduleSweeperJob` claims due rows. The naive implementation has a bad fork:
advance `NextRunAt` *before* publishing and a crash loses the run; advance *after* and a crash re-fires it,
which for an unattended multi-subagent run means paying twice.

Neither is necessary. One transaction does all three:

```
BEGIN
  claim row       (WHERE NextRunAt <= now AND Enabled, OCC on RowVersion)
  advance         NextRunAt = Cronos.GetNextOccurrence(...), LastRunAt = now
  write outbox    ScheduledRunDue(ScheduleId, OccurrenceAt)
COMMIT
```

All three commit or none do. The outbox then delivers at-least-once, and the duplicate is absorbed by the
saga's own correlation key:

```csharp
[CorrelationKey] public ScheduleOccurrence Correlation(ScheduledRunDue e)
    => new(e.ScheduleId, e.OccurrenceAt);
```

A redelivered `ScheduledRunDue` correlates to the **existing** saga instance instead of starting a second one.
Idempotency comes from the library's own mechanism, not a hand-rolled dedupe table.

### Missed occurrences

If the host is down from Friday to Monday, the sweeper finds an overdue row and fires **once**, advancing to the
next future occurrence and recording the skipped count for observability (D8).

## 6. The saga

```csharp
[Saga]
public partial class RepoDigestSaga
{
    public ScheduleOccurrence Occurrence { get; private set; }
    public string ChannelId { get; private set; } = "";
    public string ConversationId { get; private set; } = "";
    public string Findings { get; private set; } = "";

    [CorrelationKey] public ScheduleOccurrence Correlation(ScheduledRunDue e)
        => new(e.ScheduleId, e.OccurrenceAt);

    [Step(Order = 1, Compensate = nameof(DiscardFindings))]
    public RunSubagentCommand Sweep(ScheduledRunDue e)      => /* agent "scout"  */;

    [Step(Order = 2, Compensate = nameof(DiscardFindings))]
    public RunSubagentCommand Summarise(SubagentFinished e) => /* agent "writer" */;

    [Step(Order = 3)]
    public DeliverDigestCommand Deliver(SubagentFinished e) => /* → outbox */;

    public DiscardFindingsCommand DiscardFindings() => new(Occurrence);
}
```

`RunSubagentCommand`'s handler is the **only** code that touches `ISubagentRunner`. One handler serves every
saga, so adding a workflow means adding a `[Saga]` class and nothing else.

Step 3 writes `ChannelMessageQueued` into the outbox **in the same transaction as the saga state save**, through
the `ZeroAlloc.Saga.Outbox` bridge. That is the writer `ChannelMessageQueuedDispatcher` has been waiting for
since phase 1.4. Because it is an unsolicited push with no live turn, it does not collide with the edit-in-place
streaming that the channels design's §9 had to yield to.

### Wiring hazard

`AddDaedalusChannels` does **not** wire the channel outbox; `AddDaedalusAgents` does, and the library's
`AddOutbox()` uses a plain `AddHostedService`, so a host must call both and calling them carelessly runs two
pollers. This phase adds the saga store worker and the scheduling worker on top — four background pollers
contending on one `DbContext` if wired without care. The intended wiring is pinned by a host-boot test in the
shape of the existing `ApiHostChannelWiringTests`, asserting the exact hosted-service set rather than trusting
the registration code to read correctly.

## 7. Errors, edge cases and testing

Three `AgentErrorCode` members appended — additive and non-breaking, following how `Memory*` and `Skill*` were
added in 0.2.0 and 0.3.0: `SubagentBudgetExceeded`, `SubagentDeadlineExceeded`, `SubagentDepthExceeded`.

| Failure | Handling |
|---|---|
| Subagent run fails — provider error, budget, deadline | Step fails → saga compensates in reverse → **failure notice pushed to the conversation** |
| Saga store OCC conflict | `ZeroAlloc.Saga` retry-on-conflict; each attempt in a fresh `IServiceScope` |
| Outbox delivery fails | Retry with backoff, then dead-letter — built and proven in phase 1.4 |
| Unknown agent or invalid cron in a schedule | Rejected at **startup reconciliation**, not at 07:00 |
| Host down across one or more occurrences | Fire once, skip the backlog (D8) |

### A failed saga must still say something

The channels design set the rule that *the operator is always told something*, and the phase-1.4 review found the
upstream bug where `CreateAndBindAsync` silently violated it. A saga that compensates and dies quietly
reintroduces that failure in a new place — and worse, because there is no live turn where the silence would be
noticed. Nothing happening at 07:00 is indistinguishable from nothing being wrong. Compensation's terminal step
therefore writes a failure notice to the same outbox a success would. The digest arrives, or an explanation does.

### Startup validation

`DefaultAgent` is an agent **name**, not an id; `AgentId` is ULID-backed with no string constructor. This is
pinned by `DefaultAgentConfigurationTests` against each host's real `appsettings.json`, but there is still no
startup validation. Schedules make that gap materially worse — a typo'd reference would now fail unattended,
hours later, surfacing as a compensation notice rather than a boot error.

Startup therefore validates three distinct things, and the distinction matters because a schedule's `Trigger`
names a **saga**, not an agent — the saga is what names agents:

1. **Every schedule's `Trigger` resolves to a registered saga**, and its `Cron` parses under Cronos.
2. **Every agent name referenced by a registered saga resolves against `IAgentCatalog`.** This is where a
   typo'd `"scout"` is caught.
3. **Every configured agent name resolves**, including `DefaultAgent` — the thread carried since phase 1.4,
   closed here as a side effect of work this phase needs regardless.

Any failure refuses the boot rather than deferring to 07:00.

### `TimeProvider` is a requirement, not a preference

Everything time-dependent — `NextRunAt`, Cronos evaluation, deadline enforcement, the missed-occurrence skip —
goes through `TimeProvider`, never `DateTimeOffset.UtcNow`. With `FakeTimeProvider`, "the host was down over the
weekend" becomes an assertion instead of a thought experiment. Without it the D8 catch-up rule is untestable,
which in practice means untested.

### Test cases that carry risk

New suite `Thalos.NET.Tests.Subagents` (plan A); Daedalus coverage across the existing unit and integration
projects.

- **Claim-advance-enqueue is atomic** — kill the transaction mid-flight; assert `NextRunAt` did not advance and
  no outbox row exists. §5's central claim, made falsifiable.
- **Redelivery does not double-run** — publish the same `ScheduledRunDue` twice; assert one saga instance and one
  set of subagent runs.
- **Compensation notifies** — force step 2 to fail; assert a failure notice reaches the fake adapter.
- **Missed occurrences fire once** — `FakeTimeProvider` jumps three days; assert one run, skipped count of three.
- **Budget and deadline stop a run** — assert `SubagentBudgetExceeded` *and* that the model stops being called,
  not merely that the guard is reachable.
- **End-to-end on Testcontainers Postgres** — cron due → saga → two subagent runs against a fake chat client →
  outbox → fake adapter receives the digest. Real schema, real migration, real outbox worker.
- **Host wiring** — assert the exact hosted-service set (the four-poller hazard in §6).

### Two tasks at the top of plan B, before any feature work

1. **De-risk the `Saga.EfCore` 1.3.0 / `Saga` 2.0.0 skew.** `Saga.EfCore` 1.3.0 declares `ZeroAlloc.Saga >= 1.3.0`,
   so NuGet resolves it happily against 2.0.0 and it will build — but 2.0.0 was a *breaking generator fix* and
   that combination has never shipped together. It also pins EF Core Relational 9.0.4 while Daedalus is on
   EF Core 10. A throwaway spike boots the EfCore store against Saga 2.0.0 on Postgres and drives one saga to
   completion. If the generator change broke the 1.3.0-era store, that is a phase-shaping discovery, and the
   only thing worse than finding it is finding it at task 20.
2. **Baseline every test project, not just the ones this work touches.** Phase 1.4's retrospective names this
   exact omission as what hid a 126-test Playwright failure for an entire plan.
   `Daedalus.Tests.Playwright.Api` currently fails 126/126 from the phase-1.3 fixture bug, and `ci.yml` excludes
   `~Playwright` from both test steps, so it stays invisible unless deliberately looked at. **Repairing it is not
   this phase's job**, but the baseline must record it up front so it cannot be misattributed to phase 1.5 at
   review time. Suites are run repeatedly rather than once, given the 4-in-7 flake found last phase.

## 8. Delivery

Two plans, as in phases 1.1–1.4.

**Plan A — Thalos.NET, released as 0.5.0.** The two parked 0.4.x defects first (D6): `ChannelPump.CreateAndBindAsync`
discarding `BindAsync`'s failure `Result` — which leaves the operator with no reply at all while at-most-once has
already discarded the message — and `LogRejectedSender` taking a pre-formatted string, defeating structured
logging of `SenderId`. Then `ISubagentRunner`, `SubagentRunRequest`/`Result`/`Budget`, the three error codes, and
`Thalos.NET.Tests.Subagents`. No dependency on `ZeroAlloc.Saga` or `ZeroAlloc.Scheduling` (D3).

**Plan B — Daedalus.** The de-risking spike and the full baseline first (§7). Then the `ScheduledRuns` table and
migration, startup reconciliation with validation, the sweeper, `RunSubagentCommand` and its handler,
`RepoDigestSaga`, the outbox writer for success and failure, and the host wiring test.

## 9. Follow-ups recorded for later phases

- **Phase 1.6** — agent tools (`schedule__create` / `list` / `cancel`) and the Blazor management page over the
  table this phase creates. `ZeroAlloc.Scheduling` ships its own dashboard and a `<JobsDashboard>` Blazor
  component; evaluate both before hand-rolling a page.
- A synchronous in-turn delegation tool, if a parent agent turns out to need a subagent's answer mid-turn.
- Runtime-composed plans (a generic saga whose steps are data), if developer-authored workflows prove too rigid.
- Fan-out subagent steps — several subagents in parallel within one step — once a workflow needs them. The
  runner is already shaped for it; the guards would need a real fan-out cap at that point.
- `Daedalus.Tests.Playwright.Api`'s fixture ordering bug: `E2EServerFixture.GlobalSetupAsync()` touches
  `_factory.Services` — starting the host, including `SkillSyncService` querying the `Skills` table — before its
  own `EnsureCreatedAsync()` creates the schema. Pre-existing since phase 1.3; worth its own fix.
