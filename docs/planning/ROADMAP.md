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
> **Direction (2026-09-21): Thalos.NET is the reusable framework; Daedalus is one consumer.**
> This restates the 2026-08-16 refinement rather than changing it — "Daedalus is its first consumer"
> only means something if there are meant to be others. Reaffirmed because phase 2.1 nearly drifted
> from it: the first draft put generic git write tooling in `Daedalus.Agents` purely because that was
> the simpler single-repo change.
> **The rule:** generic agent capability lands in Thalos.NET. Daedalus keeps only what is genuinely its
> own — its domain, its hosting, its orchestration. Git write tooling is generic: any agent doing
> software work wants to branch, commit, push and open a pull request, and nothing about that is
> specific to tasks and projects.
> **Thalos must never reference Daedalus.** Where a capability needs something host-specific, Thalos
> defines the abstraction and the consumer implements it — never the reverse. That failure would be
> silent until someone tried to consume Thalos standalone, so it is worth a test in that repo.
> **Consequence for Milestone 2, to decide consciously rather than discover:** if the manufacturing
> pipeline is meant to be reusable, most of it belongs in Thalos too — the workflow engine in 2.2, the
> squad roster in 2.3, the process-as-skills in 2.4 — with Daedalus supplying domain and hosting. That
> is a larger claim than 2.1 and is flagged here so each phase does not quietly re-litigate placement.
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

## Milestone 1: Hermes-Style Agent Framework [status: complete]
**Goal:** Replace the Ralph Loop setup with a Hermes-like .NET agent framework (Thalos.NET) that integrates Rag.NET and AI.Sentinel.
**Started:** 2026-08-14
**Completed:** 2026-09-21
**Definition of Done:**
- [x] All planned phases complete — 1.1 through 1.9, all merged. Ralph retirement moved out of scope to phase 2.5 on 2026-09-20; it is redesigned, not deleted, so Milestone 1 closes with Ralph still running by deliberate decision, not by omission.
- [x] All tests passing — phase 1.7's merge left unit 1078, Integration 505, Playwright.Api 126, Playwright.Browser 99 all green, and no phase since changed `src/`. Not re-run for this closing task per instruction.
- [x] Regression test PASS — `docs/regression-report-2026-08-16.md` for phase 1.1, and phase 1.6's end-to-end Keycloak + Resend browser proof for the diagnostics UI. No UI shipped after 1.6 that lacks its own regression evidence.
- [x] Documentation complete, **scoped narrowly to what was checked, not to every document in the repo.** Every phase 1.1–1.9 has a design and plan doc under `docs/plans/`. `docs/architecture-diagrams.md`, the one document the milestone's own history had let drift — wrong interface signatures, the wrong LLM backend named throughout, non-existent entities — was rewritten in phase 1.8 with every symbol and package claim verified against source rather than memory, and every mermaid block confirmed to render. Thalos.NET's 11 packages are documented in its own README. This tick does **not** claim a full-repo documentation audit; it claims the phase-history record and the one document known to have drifted are now both accurate.

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
| 1.7 | Daedalus ZeroAlloc migration, smallest first: FluentValidation → `ZeroAlloc.Validation` in 11 files; the hand-rolled CQRS layer → `ZeroAlloc.Mediator` 5.1.1 across 12 commands, 2 queries, 14 handlers and 21 dispatch sites; CSFE `Result<T>` → `ZeroAlloc.Results` 1.2.2 across 151 files; CSFE `ValueObject` → `ZeroAlloc.ValueObjects` 2.0.7 for 3 classes; plus a hand-rolled `Entity<TId>` for the 9 entities the org ships no replacement for. Also takes `ZeroAlloc.Analyzers` and `ZeroAlloc.TestHelpers`, which carry no runtime surface | Refactor | **complete (2026-09-21)** — merged via [#257](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/257), 36 commits, 265 files. `CSharpFunctionalExtensions` and both `FluentValidation` packages removed and guarded by architecture tests; hand-rolled CQRS replaced by `ZeroAlloc.Mediator` 5.1.1. Suites: unit 1078, Integration 505, Playwright.Api 126, Playwright.Browser 99 — all green. **Scope corrections during execution:** Task 8 was dropped entirely once the 9 entities were found to use a LOCAL `Entity<TId>` predating CSFE, not CSFE's; the Result swap was 892 call-site rewrites rather than a using-directive swap, because ZeroAlloc has no `Result.Success<T>`; and commands had to become `readonly record struct`, which the generator requires. **Carried forward:** two pre-existing `Entity<TId>` equality bugs left deliberately unfixed and pinned as labelled characterisation tests — cross-type equality, and transient entities comparing equal | 1.2–1.6 | #232 | **Rescoped 2026-09-20: Ralph retirement is no longer part of this phase.** Retiring Ralph had been framed as deletion, but the Ralph Wiggum loop is the seed of the software-manufacturing idea rather than dead weight, so it is redesigned instead of deleted — as phase 2.5. **Scope corrected the same day** against `docs/plans/2026-09-20-zeroalloc-org-adoption-review.md`: the roadmap had understated this phase as 8 commands and 7 projects. It is 12 commands and 12 projects, and "CSFE → Results" alone would not have removed CSFE, because CSFE also supplies the `Entity<TId>` and `ValueObject` base classes the domain model derives from. Removing the dependency outright requires all three moves. Ends with `CSharpFunctionalExtensions` and both `FluentValidation` packages gone, and the two currently-inert `ZeroAlloc.Results` and `ZeroAlloc.Validation` pins finally referenced | review: `docs/plans/2026-09-20-zeroalloc-org-adoption-review.md` |
| 1.8 | Docs and architecture-diagrams rewrite | Docs | **complete (2026-09-21)** — `docs/architecture-diagrams.md` restructured around the system that exists: 21 numbered sections, 26 mermaid blocks, all verified to render. Four subsystems absent from the old document were added — channels, scheduling, schedule diagnostics, and the scout's repository tooling. Fabricated content was removed: wrong `IGitRepositoryManager`/`IRepositoryCodeExtractor` signatures, OpenAI/Copilot named as the LLM backend where it is Anthropic Claude via Thalos.NET, three non-persisted ER entities, four symbols that do not exist, and a duplicated tail section. Thalos.NET's 11 packages are documented in that repo's README via [Thalos.NET#138](https://github.com/MarcelRoozekrans/Thalos.NET/pull/138). **No Thalos.NET release. This is a scope correction, not a deferral:** the phase's own plan called for shipping 0.6.0, but Thalos.NET releases via release-please from conventional commits, and since 0.5.1 there are 18 commits — 17 `chore`, 1 `docs`, zero `feat`, zero `fix` — so nothing is releasable without forcibly overriding the tool with `Release-As`. Confirmed with the user: no override. Nothing in the code changed, and shipping a minor for a docs-only change would signal features that do not exist, undercutting the version-as-signal reasoning that deferred 1.0 in this same phase. The docs are merged to Thalos.NET `main` and reach the NuGet package page whenever the next release carries a real change. **Carried forward:** `GetAllTasksQuery` and `GetTaskByIdQuery` are dead — registered handlers, never invoked, because `TasksController` reads bypass the mediator via `ITaskQueryService`; seven older entities carry a `byte[] RowVersion` column that is inert on Npgsql, so only the two newer scheduling entities have working optimistic concurrency; `scripts/pack-local.ps1` in Thalos.NET hard-codes `0.3.0-<suffix>` and never calls GitVersion, and `Directory.Build.props` `VersionPrefix` is stuck at 0.3.0 — real releases are unaffected, local dev feeds are not | all | #233 | design: `docs/plans/2026-09-21-phase-1.8-docs-and-diagrams-design.md` · plan: `docs/plans/2026-09-21-phase-1.8-docs-and-diagrams-plan.md` |
| 1.9 | Scout repository tooling: GitHub read tools for the digest, plus write tools gated behind the developer policy so an unattended run cannot act | Backend | **complete (2026-09-19)** — merged via [#247](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/247), 16 commits, +40 unit tests and +16 integration with no pre-existing failure changed. **A real GitHub read was performed** against a public repository, returning real commits, pull requests, issues and CI runs. The write boundary is enforced by authorization rather than tool naming: `repoaction__*` is bound to the `developer` policy and a scheduled run holds only `reader`, so the authorizer denies it whatever its tool list says. **Carried forward:** "cron wrong" is not representable, so an enabled schedule whose cron never fires reads `NotYetDue` or `Overdue`; the Telegram delivery path was still unverified end to end at merge — **closed 2026-09-20**, proved against a real chat, which surfaced two defects reachable no other way | 1.5, 1.6 | — | design: `docs/plans/2026-09-19-scout-repository-tooling-design.md` · plan: `docs/plans/2026-09-19-scout-repository-tooling-plan.md`. **Executed before 1.7 and 1.8** — it takes the next free number rather than renumbering them and churning their issue references. Exists because 1.4, 1.5 and 1.6 built the channel, the scheduler and the diagnostics page for a digest whose scout cannot observe a commit, a pull request, an issue or a CI run |

### Carried forward from Milestone 1

Real, found during phases 1.7–1.9, deliberately left unfixed. Recorded here together on close so
they are not lost among nine phase rows.

1. **Two pre-existing `Entity<TId>` equality bugs** — cross-type equality, and transient entities
   comparing equal. Pinned as clearly-labelled characterisation tests in phase 1.7 rather than fixed.
2. **A `byte[] RowVersion` column is inert on Npgsql**, on 7 older entities, so they have no working
   optimistic concurrency. Only the 2 newer scheduling entities use the real `xmin` column. Known,
   with a code comment and a design-doc admission — found in phase 1.8's documentation audit.
3. **`GetAllTasksQuery` and `GetTaskByIdQuery` are dead** — registered handlers, never invoked; reads
   bypass the mediator entirely via `ITaskQueryService`. Found in phase 1.8; not fixed because
   deleting them or routing reads through the mediator is a behaviour decision, not a documentation one.
4. **CI excludes three suites** — both Playwright projects outright
   (`FullyQualifiedName!~Playwright`), and the Keycloak-dependent tests by `Category!=AuthenticationFlow`.
   Phase 1.7 proved all three now pass (unit 1078, Integration 505, Playwright.Api 126,
   Playwright.Browser 99), but the CI filter itself is unchanged. This is how `Playwright.Api` sat at
   0 of 126 passing, unnoticed, since phase 1.4.
5. **`ZA0501` (value-type boxing in a loop) is suppressed** in `Directory.Build.props`, pending a
   repo-wide `[LoggerMessage]` migration — the one occurrence is a standard `ILogger.LogWarning` call,
   and fixing it in isolation would mean hand-rolled `[LoggerMessage]` delegates for no benefit.
6. **`benchmarks/` (`Daedalus.Benchmarks`) is absent from `Daedalus.sln`**, so CI never builds it. It
   has two pre-existing compile errors.
7. **Thalos.NET's `scripts/pack-local.ps1`** hard-codes `0.3.0-<suffix>` and never calls GitVersion,
   and `Directory.Build.props` `VersionPrefix` is stuck at 0.3.0 — three releases stale. Real releases
   are unaffected because GitVersion wins in CI; local dev feeds carry wrong version numbers today.

## Milestone 2: Software Manufacturing [status: active]

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

**Milestone 1 closed 2026-09-21.** This milestone starts at phase 2.1, git write tooling, which
builds directly on the authorization boundary phase 1.9 established for `repoaction__*`. Brainstormed
via `new-milestone`.

### Phases

| # | Phase | Surface | Status | Depends on |
|---|---|---|---|---|
| 2.1 | Git write tooling: branch, commit, push and pull-request tools, gated behind the `developer` policy exactly as `repoaction__*` is. Phase 1.9 built the GitHub *read* surface and the authorization boundary; this is the write half that boundary was built for | Backend | **complete (2026-09-21)** — the roadmap described this phase as building branch, commit, push and PR tools from nothing; most of that already existed. **The capability is split**, per the 2026-09-21 direction that Thalos.NET is the reusable framework: generic git write actions (`GitActionTools` — `create_branch`, `commit`, `push`, `open_pull_request`) shipped in **Thalos.NET 0.6.0**, hosting glue (`IPullRequestPublisher` over the existing dispatcher, `git__*` tool-source registration, `developer` policy binding in both `Daedalus.Api` and `Daedalus.Cli`) stayed in Daedalus. The real work was consolidating one GitHub client onto `Daedalus.Infrastructure.Services.GitHub.GitHubApi`, adding branch creation in Thalos, and exposing all four `git__*` tools behind the `developer` policy — the same boundary `repoaction__*` uses, verified live: `git__*` bound to `developer` in both hosts, a detached scheduled run holding only `reader` denied by `DefaultToolAuthorizer` regardless of tool list. **Not proved end to end against a live remote** — creating a branch, pushing, and opening a real pull request is outward-facing and irreversible, and needs a human decision on which repository and what artefacts it is acceptable to leave there; the proof is ready and waiting. **Carried forward:** `IRepositoryAuthenticationProvider` was never registered on `main`, so Azure DevOps PR creation could never have worked — pre-existing, hidden behind resolution order, fixed here; `Daedalus.Console`'s `RalphLoopWorker` does not call the Agents composition root, so agent-registered services (including `git__*`) are absent on that host; the Integration suite test-host-crashed twice under Docker resource contention with zero failures reported; Thalos `scripts/pack-local.ps1` hard-codes `0.3.0-<suffix>` and never calls GitVersion, so local dev feeds carry wrong versions (real releases are unaffected) | 1.9 |
| 2.2 | Durable workflow engine: branching, loops, and human approval gates, resumable across process restarts. The one genuinely new subsystem in this milestone — everything else composes it. **Substrate corrected 2026-09-22, after a spike measured the original claim false.** `ZeroAlloc.StateMachine`'s entire runtime assembly is seven attribute types and **zero** non-attribute types, so a process shape can never be loaded from data; `ZeroAlloc.EventSourcing` has no step or branch vocabulary at all; and `ZeroAlloc.Saga` enforces a contiguous linear step order with neither loops nor branching — the same verdict an earlier phase already reached from a different direction and recorded in `DaedalusSchedulingServiceCollectionExtensions.cs:71`. The whole ZeroAlloc thesis is compile-time resolution, so a runtime-interpreted engine is its negation, not a missing feature. The engine is therefore **written here** — `Thalos.NET.Workflow` plus `Thalos.NET.Workflow.Orm`, on **`ZeroAlloc.ORM` and `ZeroAlloc.Outbox.Orm` rather than EF Core**, which makes 2.2 the pilot for Milestone 3's data-layer migration. `RunStep` was cited as precedent but advances strictly forward, so it is precedent for the linear shape, not for branching. Design: `docs/plans/2026-09-22-phase-2.2-workflow-engine-design.md` | Backend | **complete (2026-09-22)** — 11 tasks across two repos: Part A shipped the engine itself in **Thalos.NET 0.7.0** (`Thalos.NET.Workflow`/`.Orm` — graph model, loader, validator, gate semantics, transactional outbox dispatch, stranded-run sweep, hot-reload with content-hash immutability), Part B wired it into Daedalus (`processes/manufacture.yaml`, the resume/cancel REST boundary, the `WorkflowCaller` identity, a budget decorator). Both parts reviewed clean after several fix rounds each; full history in `.superpowers/sdd/2026-09-22-phase-2.2-workflow-engine-plan/progress.md`. **Task 11 proved it end to end against the real `AppHost`, restart included**: started `processes/manufacture.yaml` (implement → review, `maxVisits: 5`/`onExceeded: adjudicate`, a `human_approval` gate, publish), drove it to the gate, **killed the whole AppHost process tree**, confirmed the parked run survived as a bare Postgres row with the host fully down, restarted, and resumed through the real `POST /api/workflow-runs/{id}/resume` against a real Keycloak-issued admin token — the run completed to `Succeeded`. **That run executed process version 1**, whose `publish` node pointed at the `writer` agent and declared no `outcomes`; the merged `processes/manufacture.yaml` is **version 2**, and v2's corrected `publish` node — `Daedalus Architect` plus `outcomes: [published]` — is proven to load, validate and activate by `ProcessDefinitionSyncEndToEndTests`, but has never been executed by a live run. Commit order is the evidence: `f62514f` carries the restart proof, `2ecbea7` then found that `publish` could not load its skill, and `473e16f` bumped the version. The restart, durability, gate, resume-over-HTTP, sequence, branch and cap claims are unaffected by which agent one node names. A separate run independently exercised the loop-back/cap/`onExceeded` path live: five real `rejected` outcomes then a real redirect to `adjudicate`. The resume boundary's deny paths were reconfirmed live too (no token → 401, an authenticated non-privileged user → 403). **Constraint upheld, not routed around, and two mechanisms do it:** the policy half is `appsettings.json`'s `ToolPolicies` binding `git__*` and `repoaction__*` to `developer`, asserted by `ApiThalosConfigurationTests.Appsettings_binds_anthropic_defaults_tool_policies_and_sentinel_actions`, against a `WorkflowCaller` whose only role is `workflow` — which `DeveloperPolicy` rejects. That binding is what denies those tools to any workflow run, and it is verified independently of this run. In the run that actually happened, `publish` executed as `writer`, whose `Tools` list is `memory__*` alone, so that turn additionally had no `git__*`/`repoaction__*` tool to attempt: its never attempting them is tool absence first and policy denial second. No branch was pushed and no PR was opened against a real repository for the workflow's own content — that stays a deliberate, human-triggered step, same as phase 2.1. **Honest limit, observed rather than argued:** reaching `Succeeded` proves each node called the outcome tool the schema demanded, not that the work was real — and this run demonstrates the gap rather than papering over it: `review`'s `approved` outcome in the successful run almost certainly came from a documented fallback ("no visible prior note → approve") rather than a real reading of `implement`'s output, because every `AgentMemories` row observed in this environment stayed `IndexPending = true` — semantic recall had nothing to search. **Carried forward (fuller list in `docs/planning/STATE.md`):** the `AGENTS.md` self-improvement loop in `RalphPromptTemplateBuilder` must move before phase 2.5 deletes Ralph; no "start a workflow run" surface exists yet in Daedalus (Task 11's proof used a throwaway console harness calling `OrmWorkflowStore.StartAsync` directly); cross-node handoff via `memory__remember`/`recall` does not work at a live run's latency; multi-instance duplicate dispatch remains open if the API host is ever scaled past one replica (`FetchPendingAsync` has no `FOR UPDATE SKIP LOCKED`); and the publish node cannot open a real pull request by design (Option C — a host-code `IPullRequestPublisher` call outside the model's trust path — is recorded as the preferred fix for a later phase, not built now) | 2.1 |
| 2.3 | The manufacturing squad: a role roster, per-role memory scoping so a reviewer does not inherit the implementer's context, and routing between them | Backend | **complete (2026-09-23)** — two roles: `implementer` on Sonnet 5 and `reviewer` on **Opus 5**, with per-role memory partitions that survive across runs, a reviewer holding **no write tool at all**, a handoff that withholds the implementer's narrative, outcomes that must carry evidence, and three adversarial lenses that short-circuit on first rejection. `Thalos:Squad:Enabled` falls back to the single-agent configuration and **records that it did**. Two Thalos releases were built inside the phase to make it possible: **0.8.0** for a memory owner that outlives a run plus recall that degrades instead of going silent, and **0.9.0** to carry variables through the dispatch path. Rulings: `docs/plans/2026-09-23-phase-2.3-rulings.md`. **The phase's central claim was false twice and neither time was it the design that caught it:** the implementer's memory was retrieved in the reviewer's recall at similarity 1.00, and later the projection withheld nothing the moment variables became real, because it sat downstream of a dispatcher that renders the whole bag. Both fixed, the second by moving the projection between the store and the dispatcher — absence, not filtering — and verified by falsification. **Not proved end to end:** Anthropic credits ran out after one node completed a real turn, so no reviewer turn, lens pass, `checked[]`, gate or squad-disabled run was ever observed live, and **process version 4 has never been executed at all**. **Carried forward:** `work_intent` has no producer because nothing in `src` calls `StartAsync`; `MaxTotalTokens` of 150000 cannot complete one turn, measured at 358703 input tokens; `apply_code_action` applies only a refactoring Roslyn already offers, not arbitrary edits; and agent turn usage is recorded but never aggregated into cost analytics, which phase 2.5 makes urgent by deleting the only writer of `TaskExecutions` | 2.2 |
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
`Saga` and `Scheduling` are excluded, and as of 2026-09-22 the reason for `Saga` is **architectural,
not a defect upstream could fix** — its generated step graph is contiguous, linear and fixed at
compile time, so it supports neither the loops nor the branching a manufacturing process needs;
`ZeroAlloc.ORM` and `ZeroAlloc.Outbox.Orm` do gain a phase here, in 2.2; `Templates` is a `dotnet new` template rather than a package reference; `Notify`
has no hook, because Blazor does not use `INotifyPropertyChanged`.
