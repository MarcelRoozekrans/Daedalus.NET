# Session State

**Last session:** 2026-09-17
**Current milestone:** 1 — Hermes-Style Agent Framework (**4 of 8 phases complete**)
**Current phase:** 1.5 — Subagents & autonomous runs. **Plan A done and released as Thalos.NET 0.5.0.** Plan B: Task 1 spike complete (FAIL — saga dropped); Tasks 2–9 ready, Tasks 10–12 need re-planning.
**Branch state:** Daedalus `main` ahead of `origin/main` by 3, **unpushed**. Thalos.NET `main` at `b586290`, pushed and clean; `v0.5.0` tagged and published to nuget.org.

## Where things actually stand

Phase 1.5 was re-scoped and split this session. The roadmap now reads:

| # | Phase | Surface | Status |
|---|---|---|---|
| 1.5 | Subagents & autonomous runs | Backend | active — plan A in PR, plan B not started |
| 1.6 | Schedule management — agent tools + Blazor | **UI** | pending (new) |
| 1.7 | Ralph retirement + ZeroAlloc migration | Refactor | pending (was 1.6) |
| 1.8 | Thalos.NET 1.0 release + docs | Docs | pending (was 1.7) |

A `Surface` column was added to the ROADMAP table so `start-next-phase` routes 1.6 through `ui-design-system` and `ui-workflow` when it activates.

## Immediate next step

1. ✅ **Thalos.NET 0.5.0 is published on nuget.org** — all 11 packages. Plan A is done and released.
2. ✅ **Release pipeline rebuilt.** Cutting a release is now *merge the release PR*, nothing else.
   Thalos.NET [#102](https://github.com/MarcelRoozekrans/Thalos.NET/pull/102) + [#103](https://github.com/MarcelRoozekrans/Thalos.NET/pull/103), both merged. See `docs/release.md` in that repo.
3. ✅ **Task 1 spike done — verdict FAIL.** `ZeroAlloc.Saga` is dropped from this phase.
   `docs/plans/2026-09-16-saga-efcore-spike.md`.
4. ✅ **Replacement design brainstormed and approved by the user 2026-09-17** —
   `docs/plans/2026-09-17-scheduled-runs-without-saga-design.md`. It supersedes §6 of the
   subagents design; §4, §5 and §7 of that document still stand.
5. **Next: `writing-plans` over plan B Tasks 10–13**, against the approved design.
   Tasks 2–9 are unaffected and stand as written. **Nothing has been implemented yet.**
   Docker was UP this session; it is Testcontainers-backed work, so bring it up again first.

## The approved design, in short

`ScheduledRunExecutions`, one row per occurrence, `UNIQUE (ScheduleId, OccurrenceAt)` — that unique
key *is* the saga's correlation key, and is where idempotency now lives via
`INSERT ... ON CONFLICT DO NOTHING`. Steps advance through the **outbox**: each step handler runs
its subagent, then in one transaction persists its output, advances `Step`, and enqueues the next
command. The final step writes `ChannelMessageQueued` transactionally — the guarantee phase 1.4's
dispatcher has been waiting for, which was never the saga's doing, only the transaction's.

Two decisions the user made explicitly:

- **Crash mid-run resumes from the last completed step**, reusing persisted output, rather than
  abandoning the run or retrying it whole. So scout tokens are not re-paid on a writer-stage crash.
- **Steps are driven by outbox events**, not inline and not by the sweeper.

Stated honestly in the design and worth not losing: a crash *after* a subagent returns but *before*
its commit re-runs that step and pays twice. The saga had the same hole. The bounded guarantee is
**one step can be lost, never the whole run**.

`ZeroAlloc.StateMachine` 1.5.2 was considered and **not** adopted — five-value linear enum, a
generated `TryFire` earns little, and it adds a dependency to a phase that just removed one.
Revisit in 1.6 if workflows branch.

Accepted cost: the saga's *"a new workflow is a `[Saga]` class and nothing else"* is gone. New
workflows now need step commands, handlers and a `Trigger` value.

## The Saga finding, in one paragraph

A saga never receives its trigger event. `With{Saga}Saga()` registers its handlers **by
interface**; `ZeroAlloc.Mediator`'s generated `Publish` dispatches to a **closed list of concrete
handler types found in its own compilation** and never enumerates `INotificationHandler<T>` from
DI. In the saga's own assembly no `Publish` is emitted at all; in another assembly it compiles and
silently does nothing. **Not version skew** — Saga 1.6.0 with Mediator 3.0.0 fails identically, so
the plan's "pin Saga back" fallback is dead. The Saga/Saga.EfCore pairing the spike was written to
de-risk is *fine*, and reached real PostgreSQL on EF 10. Upstream:
[ZeroAlloc.Saga#127](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/127).

## Milestone 2 direction — Native AOT

Decided 2026-09-17. Thalos.NET libraries get `IsAotCompatible`; Daedalus api/console publish with
`PublishAot`. The blocker is persistence: **EF Core cannot be published AOT**, so M2 carries the
Daedalus data-layer migration from EF Core 10 to **ZeroAlloc.ORM**. Deliberately its own milestone,
not folded into phase 1.7 — it is larger than the rest of that phase combined. Brainstorm it with
`new-milestone` after M1 closes.

Dependency audit: `Microsoft.Agents.AI`, `Microsoft.Extensions.AI`, `Npgsql`, `ZeroAlloc.ORM` all
trimmable. `AI.Sentinel` annotated `RequiresUnreferencedCode`. `Anthropic`,
`Rag.NET.Abstractions`, `ZeroAlloc.Results`, `ZeroAlloc.Inject` unmarked — expect trim warnings.

Missing upstream, both filed:
[Saga.Orm](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/128),
[Outbox.Orm](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/144). Both saga and outbox
persistence are EF-only today, so neither is usable in an AOT binary.

> **Release-pipeline note (2026-09-17):** `bump-patch-for-minor-pre-major` was removed from
> `release-please-config.json`. It held a `feat:` to a patch bump pre-1.0, so 0.1.0, 0.2.0, 0.3.0
> and 0.5.0 all needed a hand-written `Release-As:` commit to override it — and forgetting it did
> not fail, it shipped the wrong version. 0.5.0 would have gone out as 0.4.1. A `Release-As:`
> footer is still the right tool for a genuinely chosen number, such as the eventual 1.0.0.

Plan B: `docs/plans/2026-09-16-thalos-subagents-plan-b.md`, 13 tasks. **Task 1 is a de-risking spike that can invalidate Tasks 10–12** — see Blockers.

## Plan A — complete, 15 commits

`ISubagentRunner` / `SubagentRunner`: one agent turn with **no live caller**, always closing its session, under three guards — token budget, wall-clock deadline, depth. Plus the two parked 0.4.x defect fixes.

**983/983 tests passing**, all 10 projects, verified after rebasing onto fixed `main` with real dependencies and Docker up.

Key facts to carry:

- **The budget is a post-hoc check.** `RunTurnAsync` is buffered, so nothing can halt a turn mid-flight. It converts an overspend into a reported failure and bounds the next step. The code says so explicitly — **do not let anyone "fix" the docs to claim otherwise.**
- `CloseSessionAsync` in the `finally` takes **`CancellationToken.None`**. A cancellable token there was a real bug; there is a regression test pinning it.
- `SubagentRunRequest.Budget` is nullable; null falls back to `SubagentOptions.DefaultBudget`.
- Rule stated on the API: **a deadline stops work, a budget settles it.**

## Blockers / known issues

- **Plan B Task 1 is a genuine risk, not a formality.** `ZeroAlloc.Saga.EfCore` 1.3.0 declares `ZeroAlloc.Saga >= 1.3.0`, but Saga is at **2.0.0** after a *breaking generator fix*. NuGet resolves it so it will build; that pairing has never shipped together. `Saga.EfCore` also pins EF Core Relational 9.0.4 while Daedalus is on EF 10. If broken, the phase changes shape — fallbacks are listed in the plan's Task 1 step 5.
- **`Daedalus.Tests.Playwright.Api` fails 126/126.** `E2EServerFixture.GlobalSetupAsync()` touches `_factory.Services` before its own `EnsureCreatedAsync()` creates the schema. Pre-existing since phase 1.3; `ci.yml` excludes `~Playwright` so it stays invisible. Not phase 1.5's job, but plan B Task 2 must baseline it so it cannot be misattributed.
- **The Telegram path has still never been exercised end to end** — no bot token. Carried from 1.4.
- **`AgentErrorCode.Validation` is enum member 0**, so `default(AgentError)` is indistinguishable from a real validation failure. This produced false-passing tests **three times on one branch, in three files, from two implementers**. A `ShouldBeFailureWith` helper now immunises new tests, but the trap itself remains. Renumbering is impossible — the enum is serialized. A `None = 0` member is a design decision **awaiting the user**.

## Open decisions (user)

1. **The deadline/budget asymmetry.** An over-deadline turn that succeeds is reported success; an over-budget turn that succeeds is reported failure. The final reviewer argued this is *correct* and should not be unified — a deadline is a stop signal, a budget is a settlement check — and the real defect was that an over-deadline success was invisible, which is now logged. Left as-is; the user may still want them unified.
2. **`AgentErrorCode` gaining a `None = 0` member**, per the trap above.
3. Carried from 1.4: fix the `Playwright.Api` fixture or file it; configure a Telegram bot token; manual sample smoke with a real `ANTHROPIC_API_KEY`.
4. Two untracked pre-pivot files — `docs/regression-report-2026-03-01-1800.md` and its screenshots — still deliberately left alone.

## Environment

**Docker is UP** — started this session, 29.5.2.

**A real finding worth acting on independently of this work:** the global NuGet cache held a **locally-packed `Rag.NET.Abstractions 1.0.0`** whose `.nupkg.metadata` source was a *previous Claude session's scratchpad feed* under `AppData\Local\Temp\claude\c--Projects-Prive-Rag-NET\...`, dated Aug 3. `rag.net.parsers.audio 1.0.0` was contaminated identically. They shadowed the genuine nuget.org packages and made `main` fail to compile locally while CI built it fine. **Both purged**; re-restored from nuget.org. Any project on this machine consuming those versions was affected. An agent packing to a scratchpad feed should not be writing into the global cache.

Aspire reuses an existing Keycloak container, so a `keycloak-realm.json` change needs `docker rm -f daedalus-realm-*`. Orphaned `dcp.exe`/dashboard processes from a killed AppHost run hold ports.

## What happened to main this session

Thalos.NET `main` had been **red for nine consecutive runs over four days** — not caused by this work. Two Renovate bumps changed upstream behaviour and two tests encoded the old behaviour:

- **AI.Sentinel 2.2.0** added a rule layer so SEC-01 and SEC-05 fire *without* embeddings. The obsolete claim appeared in five places including the public `UseAISentinel` XML doc shipped to nuget.org and the console sample, where it understated readers' actual protection.
- **Rag.NET 1.0.0** added a proactive dimension-mismatch guard that throws before Postgres is queried, so the SQL state the test expected no longer exists.

Fixed in **PR #100, merged**. Tests and docs only; no production behaviour changed.

**Recorded, not fixed:** every `throw new InvalidOperationException` site in Rag.NET's `PgVectorStore` discards the original exception — no `innerException` anywhere. That is what destroyed the SQL state and is worth raising upstream against Rag.NET.

## A process lesson worth carrying

**I branched from a local `main` that was 53 commits stale, without fetching.** The baseline I then measured — "928 passing, zero regressions" — was against the wrong tree, with Docker down so 21 RagNet tests were *unrun* rather than passing. Both facts were invisible until CI disagreed. The corrected figure after rebasing onto real `main` with Docker up is 983/983.

**`git fetch` before branching, and treat "tests did not run" as distinct from "tests passed."** A suite that cannot execute is not a green suite.

Also carried from 1.4 and repeatedly vindicated: **four separate tests on this branch initially passed while the bug they named was live.** Every one was caught by reverting the change and confirming the test fails. Assume nothing is pinned until you have seen it fail.

## SDD workspace

The plan A ledger — every ruling, review verdict and fix round — is at
`C:\Projects\Prive\Thalos.NET\.superpowers\sdd\2026-09-16-thalos-subagents-plan-a\progress.md`.
It is git-ignored, so it exists on disk only, and `git clean -fdx` in that repo would destroy it.
**PR #99 has landed and 0.5.0 is released, so its reason for being preserved has expired** — it is
kept only as a reference for how plan A was executed, and may be deleted whenever convenient.
