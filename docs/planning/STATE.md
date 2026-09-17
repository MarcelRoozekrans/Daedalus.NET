# Session State

**Last session:** 2026-09-17
**Current milestone:** 1 — Hermes-Style Agent Framework (**4 of 8 phases complete**)
**Current phase:** 1.5 — Subagents & autonomous runs. Design done, both plans written, **plan A merged as Thalos.NET PR #99**. Plan B not started.
**Branch state:** Daedalus `main` in sync with `origin/main`. Thalos.NET `main` at `edadac9`, pushed; PR #99 merged as `be07faf`; **release PR [#101](https://github.com/MarcelRoozekrans/Thalos.NET/pull/101) `chore(main): release 0.5.0` is open and awaiting merge**.

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

1. ✅ **PR #99 merged** — all five checks green, squashed to `main` as `be07faf`.
2. ✅ **Release PR [#101](https://github.com/MarcelRoozekrans/Thalos.NET/pull/101) `chore(main): release 0.5.0` opened.** Changelog verified to contain the `feat(subagents)` entry; manifest bumped `0.4.0` → `0.5.0`.
3. **Merge #101**, then finish the release by hand — three steps, none automatic:
   - `gh workflow run release-please.yml --ref main` — second dispatch, creates the GitHub release and the `v0.5.0` tag
   - `gh workflow run ci.yml --ref v0.5.0 -f publish_to_nuget=true` — publishes to nuget.org
   - Confirm `Thalos.NET 0.5.0` is listed on nuget.org before touching plan B
4. **Plan B cannot start until 0.5.0 is live on nuget.** Its Task 1 step 1 hard-stops otherwise. It also needs **Docker running** — Tasks 1, 6, 7 and 10 are all Testcontainers-backed.

> **Corrected 2026-09-17:** an earlier revision of this file said merging #99 would *let release-please open a release PR* on its own. It does not — `release-please.yml` is `workflow_dispatch` only, by design. It also needed an empty `Release-As: 0.5.0` commit first: `release-please-config.json` sets `bump-patch-for-minor-pre-major`, so pre-1.0 a `feat:` bumps the **patch**, and an undirected dispatch would have proposed **0.4.1**. That commit is `edadac9`. The same footer is required for every future deliberate minor — see `docs/release.md` in the Thalos.NET repo.

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
It is git-ignored, so it exists on disk only. Preserved deliberately until PR #99 lands; `git clean -fdx` in that repo would destroy it.
