# Handover: what Daedalus phase 3.5 needs from ZeroAlloc.Outbox.Orm

**From:** the Daedalus session running milestone 3, phase 3.5 (EF Core to ZeroAlloc.ORM)
**To:** the agent working on ZeroAlloc.Outbox in the ZeroAlloc-Net org
**Date:** 2026-11-05
**Owner decision, 2026-11-05:** the gap is fixed upstream, not worked around in Daedalus.
**Source:** gap 4.5 of `docs/plans/2026-10-17-zeroalloc-orm-gap-audit.md`; design
`docs/superpowers/specs/2026-11-05-phase-3.5-zeroalloc-orm-design.md`, section 5.

## The situation in Daedalus

One database, one process, two outbox pipelines:

| Pipeline | Table | Store today | Poller |
|---|---|---|---|
| Channel delivery and the RepoDigest scheduling chain | `"OutboxMessages"` (quoted, mixed case), created by EF | `ZeroAlloc.Outbox.EfCore` 4.0.1, `AddOutbox().WithEfCore<ApplicationDbContext>()` | stock `OutboxWorkerService` |
| Workflow engine | `outboxmessages` (unquoted, folded), created by `OutboxOrmMigrations` | `ZeroAlloc.Outbox.Orm` 4.4.6 via Thalos.NET.Workflow.Orm | Daedalus' own `WorkflowOutboxDispatchService`, because a second `AddOutbox()` breaks the first |

Phase 3.5 removes EF Core, so the first pipeline has to move to `ZeroAlloc.Outbox.Orm` while keeping its own table:
its pending rows and leases must survive the switch untouched, and the two pipelines must stay independent.

Daedalus also resends a dead-lettered channel message: `ScheduleDeliveryActions` calls
`IOutboxDashboardStore.RequeueAsync`, which today `EfCoreOutboxStore` provides.

## What 4.4.6 lacks

1. **A table name per store.** Every statement names `OutboxMessages` literally (`OutboxMessageRepository.cs`,
   `ClaimPostgresPendingOutboxQuery.cs`), so two ORM pipelines in one database share one table.
2. **More than one ORM pipeline per container.** `WithOrm()` builds the store from the unkeyed
   `IAsyncDbConnection`, and a second `WithOrm()` on the same connection throws (`OrmOutboxServiceCollectionExtensions`
   remarks; issue #256 part 2, closed with the connection-factory overload, which points a pipeline at another
   database, not at another table in the same one).
3. **`RequeueAsync`.** `OrmOutboxStore` implements `IOutboxStore` only; no `IOutboxDashboardStore`.

## What Daedalus needs, smallest form

- A named ORM pipeline can name its table, for example `WithOrm(o => o.TableName = "\"OutboxMessages\"")` or an
  options property, and two named ORM pipelines on the same connection then work side by side, each claiming only
  its own table. Quoting is part of the contract: Daedalus' existing table is the quoted, mixed-case one. The
  existing default (`OutboxMessages`, unquoted) must not change, or the workflow engine's table moves.
- The table's columns are EF's `OutboxMessageEntity` layout as `ZeroAlloc.Outbox.EfCore` 4.0.1 created it, including
  the lease columns. If the ORM store's column set differs, Daedalus needs either the store to accept that layout or
  a documented migration from it that keeps pending and leased rows.
- `RequeueAsync(id)` on the ORM store, reachable for a named pipeline (keyed `IOutboxDashboardStore`, or a narrower
  interface). The rest of `IOutboxDashboardStore` only if it comes cheaply; Daedalus does not mount the dashboard.
- Optional, not required: if the change makes two stock `OutboxWorkerService`s in one container work, Daedalus can
  later retire `WorkflowOutboxDispatchService`. Phase 3.5 does not depend on it.

## How Daedalus will verify it

An integration test leaves one pending and one leased row in `"OutboxMessages"`, switches the pipeline to the ORM
store, and sees both delivered once; a dead-lettered row requeued through `RequeueAsync` is delivered; the workflow
engine's `outboxmessages` is untouched throughout.

Please reply with the released package version; Daedalus pins it in phase 3.5's outbox task.
