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
> **Direction (2026-09-17, renumbered 2026-09-20): Native AOT is a Milestone 3 goal.** Thalos.NET and the Daedalus
> api/console binaries should publish with `PublishAot`. A dependency audit is encouraging —
> `Microsoft.Agents.AI`, `Microsoft.Extensions.AI`, `Npgsql` and `ZeroAlloc.ORM` are all
> trimmable; `AI.Sentinel` is annotated `RequiresUnreferencedCode`; several in-house packages
> are unmarked. The blocker is persistence: **EF Core cannot be published AOT**, so this milestone
> carries the migration of the Daedalus data layer from EF Core 10 to **ZeroAlloc.ORM**
> — source-generated, NativeAOT-clean, zero runtime reflection, with its own `MigrationRunner`.
> This is deliberately *not* folded into phase 1.7 — it is larger than the rest of that phase
> combined.
>
> **Revision (2026-09-20): this direction keeps its content and loses its number.** Native AOT was
> recorded as Milestone 2 on 2026-09-17, before **Software Manufacturing** existed as a goal. That
> goal now takes Milestone 2 and AOT becomes Milestone 3. Nothing about the AOT analysis above is
> withdrawn — the dependency audit and the EF Core blocker still hold, and the ZeroAlloc.ORM
> migration is still the work. Only the ordering changed, and it changed deliberately: AOT is
> infrastructure with no user-visible payoff, so spending the next milestone on it would buy a
> faster binary before the thing that binary is supposed to manufacture. The cost of this ordering
> is a double migration — the manufacturing workflow engine is written on EF Core and migrated to
> ZeroAlloc.ORM in Milestone 3. That cost is bounded: the AOT migration must already rewrite the
> outbox, scheduling and diagnostics tables built in phases 1.4 through 1.9, so the manufacturing
> tables join a migration that was happening regardless.
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
> the Native AOT migration away from EF Core. The sweeper is a `BackgroundService` on a
> one-minute `PeriodicTimer`. Both `ZeroAlloc.Saga` and `ZeroAlloc.Scheduling` are now enforced-absent
> by a `.csproj` scan in `CleanArchitectureTests` — an ArchUnit rule would be vacuous for an
> unreferenced package.
>
> **Correction (2026-09-21): both of the above are superseded. Re-measured, not re-read.**
> All three upstream issues behind these two exclusions are fixed, and each fix was verified by re-running
> the original experiment rather than by trusting the issue status — a closed issue is a claim, not a
> measurement.
> **`ZeroAlloc.Saga` is unblocked.** Saga 3.0.0 with Mediator 5.1.4 on .NET 10: the saga received its
> trigger event through `IMediator.Publish`, ran its `[Step]` and dispatched its command. That is exactly
> the scenario the 2026-09-16 spike proved broken. The architecture ban is **lifted**. Saga is now a live
> candidate for phase 2.2's durable workflow engine alongside `StateMachine` and `EventSourcing` — a design
> question to settle when 2.2 is brainstormed, not a foregone conclusion.
> **`ZeroAlloc.Scheduling` stays excluded, but the stated reason was wrong and is now corrected.** The
> bootstrap objection no longer holds: 1.2.56 ships `ZeroAlloc.Scheduling.InMemory` with a zero-setup
> `WithInMemoryStore`, and `SchedulingDbContext` is public so `dotnet ef migrations add --context
> SchedulingDbContext` works. What survives is the *design* half of the original argument:
> `ScheduledRuns.NextRunAt` is the source of truth and the sweep is idempotent, so durable job state buys
> nothing over the `BackgroundService` that `ScheduleSweeperService` already is. The ban now says that, and
> only that. `AddScheduling` alone does still register no `IJobStore`.
> **The two AOT-clean stores now exist.** `ZeroAlloc.Saga.Orm` 3.0.0 and `ZeroAlloc.Outbox.Orm` 2.6.0 have
> **zero EF Core** anywhere in their dependency trees, built on `ZeroAlloc.ORM` 1.6.6. A real saga was
> wired end to end through `WithOrmStore` against SQLite migrated by ZeroAlloc.ORM's own `MigrationRunner`.
> **This half-retires Milestone 3's premise:** "EF Core cannot publish AOT and no AOT-clean stores exist"
> is now only true in its first clause. The data-layer migration remains the work.

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
| 1.5 | Subagents & autonomous runs: `ISubagentRunner` (Thalos 0.5.0) + a `BackgroundService` sweeper, orchestrated directly over `ZeroAlloc.Outbox` (**no `ZeroAlloc.Saga`, and no `ZeroAlloc.Scheduling`** — both are now banned by an architecture test; see Direction 2026-09-17 and Consequence 2026-09-18) | Backend | **complete (2026-09-18)** — plan A released as Thalos.NET 0.5.0; plan B merged via [#242](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/242), 36 commits, +103 tests with no pre-existing failure changed. **Carried forward:** the end-to-end AppHost run and mid-flight-kill resume proof were never performed — both hosts could not boot — and the scout agent has no git or GitHub tooling, so its first digest would be empty. **Corrected 2026-09-19 by phase 1.6:** the boot failure was *not* a pre-existing `ZeroAlloc.Authorization` conflict. `ZeroAlloc.Results` 1.2.1 ships `AssemblyVersion 0.1.0.0`, a downgrade from 1.2.0's `1.2.0.0`, so this phase's own 1.2.0 → 1.2.1 pin move is what introduced it. Fixed in 1.6; the end-to-end run has since been performed | 1.1, 1.4 | #231 | design: `docs/plans/2026-09-16-thalos-subagents-design.md` · plan A (Thalos.NET repo): `docs/plans/2026-09-16-thalos-subagents-plan-a.md` — merged via [Thalos.NET#99](https://github.com/MarcelRoozekrans/Thalos.NET/pull/99), released as **0.5.0** · plan B (Daedalus): `docs/plans/2026-09-16-thalos-subagents-plan-b.md` — tasks 1–9 stand, **tasks 10–13 superseded** by `docs/plans/2026-09-17-scheduled-runs-plan-b-continuation.md` (tasks 10–17, written 2026-09-17 against the approved saga-free design) · spike: `docs/plans/2026-09-16-saga-efcore-spike.md` |
| 1.6 | Schedule diagnostics: a Blazor page plus read-only agent tools over one shared `IScheduleDiagnostics`, answering "a digest didn't arrive — where did it die?" | UI | **complete (2026-09-19)** — merged via [#244](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/244) and [#245](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/245), 19 commits, 48 files, +50 tests with no pre-existing failure changed. **Verified end to end in a real browser** through Keycloak, with Resend exercised against the database — the proof 1.5 never got. **Scope narrowed deliberately:** `schedule__create` and `cancel` were cut, because an agent that can schedule work can schedule work for itself; that privilege surface gets its own phase. **Carried forward:** "cron wrong" is not representable, so an enabled schedule whose cron never fires reads `NotYetDue` or `Overdue`; the agent write-boundary is enforced by tool-surface absence rather than authorization; and the scout still has no git or GitHub tooling | 1.5 | — | design: `docs/plans/2026-09-18-schedule-diagnostics-design.md` · UI contract: `docs/plans/2026-09-18-phase-1.6-ui-contract.md` · design system: `docs/design/MASTER.md` · plan: `docs/plans/2026-09-18-phase-1.6-schedule-diagnostics-plan.md` · rulings: `docs/plans/2026-09-18-phase-1.6-rulings.md` · spike: `docs/plans/2026-09-18-zeroalloc-results-assembly-version-spike.md` |
| 1.7 | Daedalus ZeroAlloc migration, smallest first: FluentValidation → `ZeroAlloc.Validation` in 11 files; the hand-rolled CQRS layer → `ZeroAlloc.Mediator` 5.1.1 across 12 commands, 2 queries, 14 handlers and 21 dispatch sites; CSFE `Result<T>` → `ZeroAlloc.Results` 1.2.2 across 151 files; CSFE `ValueObject` → `ZeroAlloc.ValueObjects` 2.0.7 for 3 classes; plus a hand-rolled `Entity<TId>` for the 9 entities the org ships no replacement for. Also takes `ZeroAlloc.Analyzers` and `ZeroAlloc.TestHelpers`, which carry no runtime surface | Refactor | pending | 1.2–1.6 | #232 | **Rescoped 2026-09-20: Ralph retirement is no longer part of this phase.** Retiring Ralph had been framed as deletion, but the Ralph Wiggum loop is the seed of the software-manufacturing idea rather than dead weight, so it is redesigned instead of deleted — as phase 2.5. **Scope corrected the same day** against `docs/plans/2026-09-20-zeroalloc-org-adoption-review.md`: the roadmap had understated this phase as 8 commands and 7 projects. It is 12 commands and 12 projects, and "CSFE → Results" alone would not have removed CSFE, because CSFE also supplies the `Entity<TId>` and `ValueObject` base classes the domain model derives from. Removing the dependency outright requires all three moves. Ends with `CSharpFunctionalExtensions` and both `FluentValidation` packages gone, and the two currently-inert `ZeroAlloc.Results` and `ZeroAlloc.Validation` pins finally referenced | review: `docs/plans/2026-09-20-zeroalloc-org-adoption-review.md` |
| 1.8 | Thalos.NET 1.0 release, docs, architecture-diagrams rewrite | Docs | pending | all | #233 | — |
| 1.9 | Scout repository tooling: GitHub read tools for the digest, plus write tools gated behind the developer policy so an unattended run cannot act | Backend | **complete (2026-09-19)** — merged via [#247](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/247), 16 commits, +40 unit tests and +16 integration with no pre-existing failure changed. **A real GitHub read was performed** against a public repository, returning real commits, pull requests, issues and CI runs. The write boundary is enforced by authorization rather than tool naming: `repoaction__*` is bound to the `developer` policy and a scheduled run holds only `reader`, so the authorizer denies it whatever its tool list says. **Carried forward:** "cron wrong" is not representable, so an enabled schedule whose cron never fires reads `NotYetDue` or `Overdue`; the Telegram delivery path was still unverified end to end at merge — **closed 2026-09-20**, proved against a real chat, which surfaced two defects reachable no other way | 1.5, 1.6 | — | design: `docs/plans/2026-09-19-scout-repository-tooling-design.md` · plan: `docs/plans/2026-09-19-scout-repository-tooling-plan.md`. **Executed before 1.7 and 1.8** — it takes the next free number rather than renumbering them and churning their issue references. Exists because 1.4, 1.5 and 1.6 built the channel, the scheduler and the diagnostics page for a digest whose scout cannot observe a commit, a pull request, an issue or a CI run |

## Milestone 2: Software Manufacturing [status: planned]

**Goal:** Turn Daedalus from an agent that answers into a system that *builds* — a durable,
resumable pipeline that takes a unit of work from intent to reviewed pull request, staffed by a
squad of role-scoped agents following an explicit written process.

**Origin:** the Ralph Wiggum loop. Ralph was a single agent looping over a prompt until the work
looked done — cheap, stateless, and unable to say why it did anything. It was scheduled for
deletion in phase 1.7. It is not deleted: the loop was the right instinct with the wrong
substrate. Milestone 2 keeps the instinct — work repeats until it converges — and replaces the
substrate with durable state, distinct roles, and review gates. Ralph's retirement is therefore
the *last* phase here, not the first: it is retired when something demonstrably better runs in
its place, which is the only retirement that proves anything.

**Not started until Milestone 1 closes.** Brainstormed via `new-milestone`.

### Phases

| # | Phase | Surface | Status | Depends on |
|---|---|---|---|---|
| 2.1 | Git write tooling: branch, commit, push and pull-request tools, gated behind the `developer` policy exactly as `repoaction__*` is. Phase 1.9 built the GitHub *read* surface and the authorization boundary; this is the write half that boundary was built for | Backend | planned | 1.9 |
| 2.2 | Durable workflow engine: branching, loops, and human approval gates, resumable across process restarts. The one genuinely new subsystem in this milestone — everything else composes it. Built on `ZeroAlloc.StateMachine` and `ZeroAlloc.EventSourcing`, with `ZeroAlloc.AsyncEvents` for internal dispatch — these are the substrate, not an add-on. `RunStep` is already a hand-written state machine, so the pattern is not speculative here | Backend | planned | 2.1 |
| 2.3 | The manufacturing squad: a role roster, per-role memory scoping so a reviewer does not inherit the implementer's context, and routing between them | Backend | planned | 2.2 |
| 2.4 | The process as skills: the manufacturing steps written as skills rather than code, including an explicit review stage, so the process can change without a redeploy | Mixed | planned | 2.3 |
| 2.5 | Ralph retirement: delete the loop once 2.1–2.4 demonstrably do its job better | Refactor | planned | 2.4 |
| 2.6 | Observability: `ZeroAlloc.Telemetry` source-generated spans and metrics. Five OpenTelemetry packages are pinned today but `src` contains **zero** `ActivitySource` or `Meter`, so nothing emits a custom span. A manufacturing run that cannot be traced cannot be debugged | Backend | planned | 2.2 |
| 2.7 | The scout HTTP path: `ZeroAlloc.Rest` replaces the 6 hand-rolled `HttpClient` API clients, and `ZeroAlloc.Cache` fronts the **rate-limited** GitHub API the scout hits on every digest. One subsystem, so they land together rather than separately | Backend | planned | 2.1 |
| 2.8 | Manufacturing console: `ZeroAlloc.Flux` for Blazor state, covering the approval-gate and run-inspection surfaces 2.2 and 2.3 produce | UI | planned | 2.2, 2.3 |

## Milestone 3: Native AOT [status: planned]

**Goal:** Publish Thalos.NET and the Daedalus api/console binaries with `PublishAot`.

**Carries:** the Daedalus data layer migration from EF Core 10 to `ZeroAlloc.ORM`, because EF Core
cannot be published AOT. See the 2026-09-17 direction at the top of this file for the dependency
audit and the reasoning — this was recorded as Milestone 2 until 2026-09-20 and renumbered when
Software Manufacturing took that slot. The analysis is unchanged; only the ordering moved.

**Also carries, per `docs/plans/2026-09-20-zeroalloc-org-adoption-review.md`:** `ZeroAlloc.Inject`
(compile-time DI is an AOT prerequisite), `ZeroAlloc.Resilience` for the 2 `AddStandardResilience`
sites, `ZeroAlloc.Serialisation` as a deliberate `System.Text.Json` swap rather than the transitive
arrival phase 1.7 gives it, `ZeroAlloc.Collections` once AOT work supplies the benchmarks that
justify pooled collections, and `ZeroAlloc.Specification` alongside the ORM migration that finally
gives query composition a surface.

**Adoption programme.** 23 of the org's 27 libraries now have a named phase. The four that do not:
`Saga` and `Scheduling` are banned by an architecture test on upstream defects and un-banning them
is upstream work; `Templates` is a `dotnet new` template rather than a package reference; `Notify`
has no hook, because Blazor does not use `INotifyPropertyChanged`.
