# Phase 1.5 — scheduled runs without `ZeroAlloc.Saga`

**Date:** 2026-09-17
**Status:** approved
**Supersedes:** §6 of `docs/plans/2026-09-16-thalos-subagents-design.md`. Everything else in that
document — §4 identity, §5 scheduling and the `ScheduledRuns` table, §7 errors and testing — stands
unchanged.
**Why:** `ZeroAlloc.Saga` cannot be driven at all. See
`docs/plans/2026-09-16-saga-efcore-spike.md` and
[ZeroAlloc.Saga#127](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/127).

## What the saga was providing

Four things, and each needs an explicit replacement:

| Saga property | Replacement |
|---|---|
| multi-step state across scout → writer → deliver | `ScheduledRunExecutions` row |
| idempotency on redelivered `ScheduledRunDue`, via the correlation key | `UNIQUE (ScheduleId, OccurrenceAt)` + `ON CONFLICT DO NOTHING` |
| compensation — `DiscardFindings` | not needed; see *Compensation* below |
| final step's outbox write in the state-save transaction | unchanged — each step still commits its own transaction |

## `ScheduledRunExecutions`

One row per occurrence.

| Column | Purpose |
|---|---|
| `Id` | surrogate key |
| `ScheduleId`, `OccurrenceAt` | **`UNIQUE` together.** This is the saga's correlation key expressed in the schema, and it is where idempotency now lives |
| `Step` | `Pending` → `Scout` → `Writer` → `Deliver` → `Done` \| `Failed` |
| `Findings` | scout output, persisted so a resume never re-pays for it |
| `Digest` | writer output, same reason |
| `ChannelId`, `ConversationId` | copied from the schedule row at claim time |
| `PrincipalId`, `Roles` | the §4 detached principal, copied at claim time |
| `Attempts`, `LastError` | diagnosis |
| `RowVersion` | OCC between concurrent pollers |
| `CreatedAt`, `UpdatedAt` | observability |

`ChannelId` / `ConversationId` / `PrincipalId` / `Roles` are **copied, not joined**, for exactly the
reason §5 gives for carrying them on `ScheduledRuns`: a detached run cannot discover where to talk
or who it is, and `IConversationMap.GetBySessionAsync` was deliberately removed in 0.4.0 and must
not be reintroduced. Copying also freezes the identity a run executes under at claim time, so
editing a schedule mid-run cannot change the principal of a run already in flight.

## Flow

```text
ScheduleSweeperJob   --- one txn --->  claim row, advance NextRunAt,
                                       write ScheduledRunDue          -> outbox
outbox -> ScheduledRunDue handler
             INSERT ... ON CONFLICT (ScheduleId, OccurrenceAt) DO NOTHING
             inserted -> write RunScoutStep                           -> outbox   (one txn)
             conflict -> redelivery, no-op

outbox -> RunScoutStep handler
             ISubagentRunner.RunAsync  (agent "scout")
             one txn: Findings, Step=Writer, write RunWriterStep      -> outbox

outbox -> RunWriterStep handler
             ISubagentRunner.RunAsync  (agent "writer")
             one txn: Digest, Step=Deliver, write DeliverDigest       -> outbox

outbox -> DeliverDigest handler
             one txn: Step=Done, write ChannelMessageQueued           -> outbox
```

The sweeper's single transaction is unchanged from §5 and still does claim + advance + enqueue
atomically.

The final step writing `ChannelMessageQueued` inside its own transaction is the guarantee
`ChannelMessageQueuedDispatcher` has been waiting for since phase 1.4. It survives this redesign
intact — it was never the saga that provided it, only the transaction.

`RunSubagentCommand`'s handler remains the **only** code that touches `ISubagentRunner` (design §6).
The step handlers call through it rather than reaching for the runner directly.

## Idempotency

Two layers, because the outbox is at-least-once at both hops:

1. **Occurrence level.** `INSERT ... ON CONFLICT (ScheduleId, OccurrenceAt) DO NOTHING`. A
   redelivered `ScheduledRunDue` finds the row already present and stops. Exactly one execution per
   occurrence, which is what the correlation key bought.
2. **Step level.** Each step handler loads the row and returns without work if `Step` is already
   past its own step. `RowVersion` OCC settles two pollers racing the same step.

## What this does not guarantee

A crash **after** a subagent returns but **before** the step's commit loses that step's work, and
redelivery re-runs it — paying its tokens twice. This hole existed in the saga design too; it is
inherent without a two-phase protocol with the LLM provider, which does not exist.

The guarantee is therefore bounded, and should be stated that way in the phase's docs: **a crash
can cost one step, never the whole run.** That is a real improvement over the
abandon-and-wait-for-next-occurrence alternative, which was the reason for choosing resume.

## Compensation

The saga carried `DiscardFindings` to unwind its own state. Nothing external is mutated before the
delivery step — the scout and writer runs produce text and nothing else — so there is nothing to
unwind.

A failed step sets `Step = Failed` and `LastError`, and writes a channel notice to the outbox so
the operator is told. That satisfies the channels design's standing rule that *the operator is
always told something*, which phase 1.4's two parked defects were both about.

## `ZeroAlloc.StateMachine` — considered, not adopted

The org ships `ZeroAlloc.StateMachine` 1.5.2, source-generated and AOT-safe, and it would be the
natural FSM here. It is **not** adopted for this phase: the machine is a five-value enum with
strictly linear transitions, so a generated `TryFire` buys very little over a `Step` column and one
handler per step, and it adds a dependency to a phase whose whole purpose right now is removing
one.

Revisit in phase 1.6 if workflows start branching — that is when an FSM earns its place.

## Cost accepted

The saga's claim was that *"adding a workflow means adding a `[Saga]` class and nothing else."*
That is lost. A new workflow now needs new step commands, their handlers, and a `Trigger` value.
This is a genuine regression in extensibility, accepted because the alternative is a library that
cannot be driven at all.

## Wiring

Design §6's wiring hazard shrinks by one: there is no saga store worker. The host runs **three**
pollers — the channel outbox, the agent outbox, and the scheduling worker. The host-boot test still
pins the exact hosted-service set, in the shape of `ApiHostChannelWiringTests`, rather than trusting
registration code to read correctly.

## Testing

Cases that carry risk and must be pinned:

- redelivered `ScheduledRunDue` produces **exactly one** `ScheduledRunExecutions` row
- redelivered step command does **not** re-run its step
- crash between steps resumes at the correct step and reuses the persisted output
- a failed step sets `Failed`, records `LastError`, and the operator receives a notice
- the delivery step's `ChannelMessageQueued` is written in the same transaction as `Step = Done`
- two pollers racing one step: exactly one wins, the other no-ops on `RowVersion`

Per the process lesson carried since 1.4 — every one of these must be **seen to fail** with its
guard removed before it is trusted.

## Impact on plan B

| Task | Status |
|---|---|
| 2–9 | unaffected, stand as written |
| 10 | rewritten — `RepoDigestSaga` becomes the execution table plus step handlers |
| 11 | sweeper and DI stand, minus the saga store registration |
| 12 | host wiring pins three pollers, not four |
| 13 | architecture rules updated — the Saga ban changes meaning |
