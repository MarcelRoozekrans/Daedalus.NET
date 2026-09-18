# Phase 1.6 — schedule diagnostics

**Date:** 2026-09-18
**Status:** approved
**Surface:** UI
**Phase:** 1.6 — Schedule management
**Depends on:** 1.5, merged via [#242](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/242)

## Goal

Answer one question well: **a digest didn't arrive — where did it die?**

Phase 1.5 made scheduled runs work. Nothing makes them *visible*. A run that fails at 07:00 leaves a
row in a table nobody reads and, at best, a Telegram notice on a path that has never been verified end
to end. This phase turns that into a page.

## Scope, and what the roadmap said

The roadmap describes 1.6 as "agent tools — `schedule__create` / `list` / `cancel` — plus a Blazor
management page". This design narrows that deliberately:

- **Observability first, control second.** The page diagnoses; it does not author schedules.
- **No `schedule__create`.** An agent that cannot schedule work cannot schedule work for itself. That
  is a privilege surface worth opening on purpose, in its own phase, not as a side effect of building
  a diagnostics page.
- **Read-only agent tools**, mirroring the page rather than extending it.

Explicitly **out of scope**: creating or editing schedules; the stranded-run reaper deferred from 1.5
— this page makes stranded runs visible, which is the precondition for automating them; and the
scout's missing repository tooling.

## The five ways a digest dies

Working back from the brief, a digest can fail in five places, and they do not all live in one table:

1. **Never fired** — schedule disabled, or cron wrong. `ScheduledRuns`.
2. **Sweeper not claiming** — `NextRunAt` in the past with no execution row at all.
3. **Stranded mid-flight** — non-terminal `Step`, stale `UpdatedAt`.
4. **Failed** — `LastError` says why.
5. **Ran fine, delivery died** — execution `Done`, but its `ChannelMessageQueued` dead-lettered.

Case 5 is why this design reaches into the outbox. A run that succeeded and never arrived is precisely
the confusing case the page exists to resolve, and stopping at the execution table would show it as
healthy.

## The verdict model

The service returns a **verdict**, not rows. That is the whole design: the deliverable is an answer.

| Verdict | Condition | Carries |
|---|---|---|
| `NotYetDue` | no execution row, `NextRunAt` in future | `NextRunAt` |
| `Overdue` | no execution row, `NextRunAt` in the past | `NextRunAt`, how long overdue, `MissedOccurrences` |
| `Running` | non-terminal `Step`, recently updated | `Step`, `UpdatedAt` |
| `Stranded` | non-terminal `Step`, stale `UpdatedAt` | `Step`, `UpdatedAt`, how long stuck, `Attempts` |
| `Failed` | `Step == Failed` | `LastError`, `FailedAtStep`, `Attempts` |
| `Undelivered` | `Step == Done`, its channel message dead-lettered | `DeadLetterError`, `RetryCount` |
| `Delivered` | `Step == Done`, no dead letter | `UpdatedAt` |
| `DeliveryUnknown` | `Step == Done`, the outbox could not be read | `UpdatedAt` |

`Stranded`'s threshold is **configurable, defaulting to 15 minutes**. The outbox retries eight times
with exponential backoff — roughly two minutes — so anything non-terminal for more than a few minutes
is stuck rather than slow; fifteen minutes is comfortably clear of that without flagging a slow but
healthy run. The number is configurable because the first surprising workload would invalidate it.

## Two schema changes, both additive

### `ScheduledRunExecution.FailedAtStep`

`Fail()` sets `Step = Failed` and records `LastError`, but never records **which step it was at** —
the store captures `failedStep` locally only to compose the operator notice. The single place "failed
during the Writer step" survives today is the prose of a Telegram message that may itself have
dead-lettered.

For a page whose entire job is *where did it die*, that is the weaker half of the answer. Add a
nullable `FailedAtStep`, set by `Fail()` from the step it is overwriting.

### `ChannelMessageQueued.ExecutionId`

Nothing links a dead-lettered channel message back to the execution that produced it.
`OutboxMessageEntity` has no correlation column, and `IOutboxWriter.WriteAsync` returns `void`, so the
id is never available to the caller.

Add a nullable `Guid? ExecutionId`. Scheduled deliveries and failure notices set it; ordinary channel
replies leave it null, which is honest.

> **Rule for this record:** the outbox stores it as an opaque serialized payload, so rows already
> queued when a field ships deserialize with that field defaulted. That is harmless for a nullable
> addition and fatal for anything else. **Only ever add nullable fields to `ChannelMessageQueued`.**

## Architecture

One diagnosis, three consumers. The page and the agent tool cannot drift, because there is one
implementation and one verdict type.

| Component | Location | Why there |
|---|---|---|
| `RunDiagnosis` DTO and verdict enum | `Daedalus.Application/DTOs/Scheduling/` | the only assembly both `Daedalus.Web` and `Daedalus.Api` reference |
| `IScheduleDiagnostics` | `Daedalus.Application/Abstractions/` | matches the existing `IBrainstormService` / `ILearningsMemory` pattern |
| `ScheduleDiagnostics` | `Daedalus.Agents/Scheduling/` | the only layer holding both the scheduling stores and the outbox |
| `SchedulesController` | `Daedalus.Api/Controllers/` | thin; calls the interface, returns the DTO |
| `Schedules.razor` | `Daedalus.Web/Pages/` | `HttpClient` over OIDC, as every other page |
| `DaedalusScheduleTools` | `Daedalus.Agents/Tools/` | `[ThalosToolType]` under the existing `daedalus__` source |

**Why it is not split differently:** `Daedalus.Web` references only `Daedalus.Application`, so the DTO
must live there. The service needs Infrastructure and `ZeroAlloc.Outbox`, which Application must not
reference — the architecture tests enforce that. Interface in Application, implementation in Agents is
the only split that satisfies both.

## Interface

Two read methods, which is all either consumer needs:

- `GetOverviewAsync` — every schedule with its latest verdict. Backs the landing view and
  `schedule__list`.
- `GetRunHistoryAsync(scheduleId, take)` — recent executions for one schedule, each with a verdict.
  Backs the drill-down and the agent's "why".

Diagnosing one occurrence:

```text
no execution row  ->  NextRunAt in future ? NotYetDue : Overdue
Step == Failed    ->  Failed       + LastError, FailedAtStep, Attempts
Step == Done      ->  dead letter for this ExecutionId ? Undelivered : Delivered
otherwise         ->  UpdatedAt stale ? Stranded : Running
```

## Reading the outbox: EF for reads, the dashboard store for actions

`IOutboxDashboardStore` is the intended seam and exposes `GetSnapshotAsync`, `RequeueAsync`,
`CancelAsync` and `ForceDispatchAsync`. Its `OutboxSnapshot` carries `Pending`, `RetryQueue`,
`DeadLettered` and `Dispatched` lists of `OutboxEntry` — which has `Id`, `TypeName`, `RawPayload`,
`Payload`, `RetryCount` and `CreatedAt`, and **no `DeadLetterError`**.

So the seam can say *which* messages died but never *why*, which is half of what this page exists to
report.

Neither route can filter by `ExecutionId` in SQL — the payload is an opaque blob, so both must fetch
candidates and deserialize. A direct query on `OutboxMessageEntity`, which the library maps into our
own `ApplicationDbContext` through its own `AddOutboxMessages()` and exposes as a public type, narrows
by `TypeName` and `Status` in the database **and** returns `DeadLetterError` in the same pass.

**Decision:** read through EF, bounded by type, status and a time window. Use the dashboard store's
`RequeueAsync` and `CancelAsync` when the page offers to resend. Record this reasoning at the call
site — "why aren't you using the dashboard interface" is the first question a reviewer will ask.

**One query per table, not per schedule.** The overview takes the latest execution per schedule and
one bounded dead-letter fetch. The naive shape is an N+1 that only becomes visible once there are
enough schedules to matter.

## Error handling

Degrade, never blank.

- If the outbox read fails, a completed run returns `DeliveryUnknown` rather than `Delivered` — the
  verdict enum stays the single source of truth, and the page never claims a delivery it could not
  confirm.
  A page that reports four of five cases beats a page that reports nothing.
- A payload that will not deserialize is skipped, not thrown on.
- The page renders an explicit error state. It never shows an empty table, which reads as "no
  schedules" rather than "the request failed".

## Testing

The eight verdicts are the spec. Four carry the real risk:

- **`Delivered` vs `Undelivered`.** Absence of a dead letter must read as delivered. Dispatched outbox
  rows may be pruned, so "no row found" and "no dead letter found" are different questions, and
  conflating them would report every old successful run as undelivered. This boundary gets a mutation
  check, because a wrong answer here is actively misleading rather than merely absent.
- **`Running` vs `Stranded`.** A threshold, driven by injected `TimeProvider` as this codebase
  mandates, tested either side of the boundary rather than only in the middle.
- **`Overdue`.** A schedule due in the past with no execution row — the "is anything running at all"
  verdict, and the one that would have made phase 1.5's boot blocker visible.
- **One verdict, two consumers.** A test asserting the agent tool and the controller return the same
  verdict for the same state. That is the entire justification for this architecture; without it the
  claim is an intention.

Each of these is written to be **seen failing** before it is trusted, consistent with how 1.5 was
executed — where that discipline caught a race test that had been passing without ever racing.

## Sequencing note

Phase 1.5 closed with both hosts unable to boot, on a pre-existing upstream conflict between
`ZeroAlloc.Authorization` and `ZeroAlloc.Results`. Brainstorming, the design system and the UI contract
are all upstream of a running application and proceed regardless. **The dependency conflict is to be
resolved before implementation begins**, so the build-and-review half of this phase lands on an
application that actually starts — and so `ui-review`, which drives Playwright against a running app,
is not degraded.
