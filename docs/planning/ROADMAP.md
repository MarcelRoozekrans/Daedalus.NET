# Project Roadmap

> **Direction (2026-08-14):** Daedalus pivots away from the Ralph Loop orchestrator
> model toward a Hermes-like, .NET-oriented autonomous agent framework, integrating
> Rag.NET (RAG pipeline) and AI.Sentinel (LLM security middleware). Pre-pivot work
> (Ralph Loop, brainstorm sessions, costs dashboard, MCP knowledge base) is treated
> as baseline; reusable parts will be identified during design.
>
> **Refinement (2026-08-16):** The agent framework is a standalone library,
> **Thalos.NET** (separate repo + nuget.org, ZeroAlloc-native, built on Microsoft Agent
> Framework 1.17). Daedalus is its first consumer. Ralph is retired via the strangler
> pattern in phase 1.6. See `docs/plans/2026-08-16-thalos-agent-core-design.md`.
>
> **Direction (2026-09-17): Native AOT is a Milestone 2 goal.** Thalos.NET and the Daedalus
> api/console binaries should publish with `PublishAot`. A dependency audit is encouraging —
> `Microsoft.Agents.AI`, `Microsoft.Extensions.AI`, `Npgsql` and `ZeroAlloc.ORM` are all
> trimmable; `AI.Sentinel` is annotated `RequiresUnreferencedCode`; several in-house packages
> are unmarked. The blocker is persistence: **EF Core cannot be published AOT**, so Milestone 2
> carries the migration of the Daedalus data layer from EF Core 10 to **ZeroAlloc.ORM**
> (source-generated, NativeAOT-clean, zero runtime reflection, with its own `MigrationRunner`).
> This is deliberately *not* folded into phase 1.7 — it is larger than the rest of that phase
> combined. Milestone 2 is brainstormed via `new-milestone` once Milestone 1 closes.
>
> **Consequence (2026-09-17): `ZeroAlloc.Saga` is dropped from phase 1.5.** The Task 1 spike
> found that a saga never receives its trigger event, because `ZeroAlloc.Mediator`'s generated
> `Publish` ignores DI-registered `INotificationHandler<T>`. Pinning Saga back does not help.
> See `docs/plans/2026-09-16-saga-efcore-spike.md` and upstream
> [ZeroAlloc.Saga#127](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/127). Two AOT-clean
> stores are also missing upstream —
> [Saga.Orm](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/128) and
> [Outbox.Orm](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/144).
>
> **Consequence (2026-09-18): `ZeroAlloc.Scheduling` is dropped from phase 1.5 as well.** Its EF job
> store requires a separate `SchedulingDbContext` that ships no migrations and cannot be bootstrapped
> with `EnsureCreated`, which no-ops once the database exists and would have left the `Jobs` table
> silently missing. The package cannot be used without that store — `AddScheduling` alone registers no
> `IJobStore` and the host fails at resolution. Since `ScheduledRuns.NextRunAt` is the source of truth
> and the sweep is idempotent, durable job state buys nothing, and a second EF context would double
> Milestone 2's migration away from EF Core for Native AOT. The sweeper is a `BackgroundService` on a
> one-minute `PeriodicTimer`. Both `ZeroAlloc.Saga` and `ZeroAlloc.Scheduling` are now enforced-absent
> by a `.csproj` scan in `CleanArchitectureTests` — an ArchUnit rule would be vacuous for an
> unreferenced package.

## Milestone 1: Hermes-Style Agent Framework [status: active]
**Goal:** Replace the Ralph Loop setup with a Hermes-like .NET agent framework (Thalos.NET) that integrates Rag.NET and AI.Sentinel.
**Started:** 2026-08-14
**Definition of Done:**
- [ ] All planned phases complete
- [ ] All tests passing
- [ ] Regression test PASS
- [ ] Documentation complete

### Phases

GitHub milestone: [Milestone 1](https://github.com/MarcelRoozekrans/daedalus/milestone/1)

| # | Phase | Surface | Status | Depends on | GH issue | Design / Plan |
|---|---|---|---|---|---|---|
| 1.1 | Thalos.NET core + AI.Sentinel + Daedalus HTTP/Blazor channel | Mixed | complete (2026-08-17; Thalos.NET 0.1.1 on nuget.org, #227) | — | #227 | design: `docs/plans/2026-08-16-thalos-agent-core-design.md` · plan A (Thalos.NET repo): `docs/plans/2026-08-16-thalos-net-plan-a.md` · plan B (Daedalus): `docs/plans/2026-08-16-thalos-net-plan-b.md` · regression: `docs/regression-report-2026-08-16.md` |
| 1.2 | Memory: `Thalos.NET.Memory` port + Rag.NET adapter (pgvector), replaces hand-rolled learnings slice | Backend | complete (2026-08-17; Thalos.NET 0.2.0 on nuget.org, #228) | 1.1 | #228 | design: `docs/plans/2026-08-17-thalos-memory-design.md` · plan A (Thalos.NET repo): `docs/plans/2026-08-17-thalos-memory-plan-a.md` · plan B (Daedalus): `docs/plans/2026-08-17-thalos-memory-plan-b.md` |
| 1.3 | Skills: agent-scoped procedure documents (files → DB, catalogue + load/search tools) | Backend | complete (2026-08-19; Thalos.NET 0.3.0 on nuget.org, #229) | 1.2 | #229 | design: `docs/plans/2026-08-18-thalos-skills-design.md` · plan A (Thalos.NET repo): `docs/plans/2026-08-18-thalos-skills-plan-a.md` · plan B (Daedalus): `docs/plans/2026-08-18-thalos-skills-plan-b.md` · review: `docs/pre-push-review-2026-08-19-1830.md` |
| 1.4 | Channels: Telegram (+ CLI) via `IChannelAdapter` + `ZeroAlloc.Outbox` | Backend | complete (2026-08-22) | 1.1 | #230 | design: `docs/plans/2026-08-20-thalos-channels-design.md` · plan A (Thalos.NET repo): `docs/plans/2026-08-20-thalos-channels-plan-a.md` — merged via [Thalos.NET#41](https://github.com/MarcelRoozekrans/Thalos.NET/pull/41), released as **Thalos.NET 0.4.0** (breaking: `IChannelAdapter.DeliverAsync` re-keyed `SessionId` → `ConversationId`) · plan B (Daedalus): `docs/plans/2026-08-20-thalos-channels-plan-b.md` — merged via [#241](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/241) · review: `docs/pre-push-review-2026-08-21-2230.md` |
| 1.5 | Subagents & autonomous runs: `ISubagentRunner` (Thalos 0.5.0) + a `BackgroundService` sweeper, orchestrated directly over `ZeroAlloc.Outbox` (**no `ZeroAlloc.Saga`, and no `ZeroAlloc.Scheduling`** — both are now banned by an architecture test; see Direction 2026-09-17 and Consequence 2026-09-18) | Backend | **complete (2026-09-18)** — plan A released as Thalos.NET 0.5.0; plan B merged via [#242](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/242), 36 commits, +103 tests with no pre-existing failure changed. **Carried forward:** the end-to-end AppHost run and mid-flight-kill resume proof were never performed — both hosts cannot boot on a pre-existing upstream `ZeroAlloc.Authorization` / `ZeroAlloc.Results` version conflict — and the scout agent has no git or GitHub tooling, so its first digest would be empty | 1.1, 1.4 | #231 | design: `docs/plans/2026-09-16-thalos-subagents-design.md` · plan A (Thalos.NET repo): `docs/plans/2026-09-16-thalos-subagents-plan-a.md` — merged via [Thalos.NET#99](https://github.com/MarcelRoozekrans/Thalos.NET/pull/99), released as **0.5.0** · plan B (Daedalus): `docs/plans/2026-09-16-thalos-subagents-plan-b.md` — tasks 1–9 stand, **tasks 10–13 superseded** by `docs/plans/2026-09-17-scheduled-runs-plan-b-continuation.md` (tasks 10–17, written 2026-09-17 against the approved saga-free design) · spike: `docs/plans/2026-09-16-saga-efcore-spike.md` |
| 1.6 | Schedule management: agent tools (`schedule__create` / `list` / `cancel`) + Blazor management page over the `ScheduledRuns` table | UI | pending | 1.5 | — | — |
| 1.7 | Ralph retirement + Daedalus ZeroAlloc migration (CSFE→Results, FluentValidation→Validation, CQRS→Mediator) | Refactor | pending | 1.2–1.6 | #232 | — |
| 1.8 | Thalos.NET 1.0 release, docs, architecture-diagrams rewrite | Docs | pending | all | #233 | — |
