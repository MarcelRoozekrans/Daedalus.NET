# Session State

**Last session:** 2026-08-19
**Current milestone:** 1 — Hermes-Style Agent Framework (3 of 7 phases complete)
**Current phase:** 1.3 — Skills → **complete and merged** (#240); next is **1.4 — Channels** (#230)
**Branch state:** merged into `main` via **#240** (22 commits, branch deleted) and **#239** (the split-out auth fix). CI green on both, including all three container images. `main` is at the #240 merge.
**Release context:** Thalos.NET **0.3.0** on nuget.org (nine packages) — tagged `v0.3.0`, GitHub release cut. Daedalus **0.1.0** released; GitVersion + release-please + commitlint + gated `publish-release`; runbook `docs/release.md`.

## Last completed

### Plan A (Thalos.NET) — complete

`Thalos.NET.Skills` shipped in **0.3.0**: `SkillName`/`SkillDocument`/`ISkillStore`/`SkillQuery`/`SkillOptions` in `Thalos.Skills`, four `AgentErrorCode.Skill*` codes, `SkillCatalogueFailedEvent`, `AgentDefinition.Skills`, `SkillStoreContractTests` + `SkillIndexContractTests` in `Thalos.NET.Testing`. Task 24's pre-release review found three blockers (an unreadable root silently retiring skills, memory text able to forge a `<skills>` block, `skills__load` echoing an unsanitised name) — all fixed before release.

### Plan B (Daedalus) — 21 of 21 tasks complete

- **`Skills` table + `PostgresSkillStore`** — name as primary key, green against the library contract on Testcontainers plus two Daedalus facts (AND tag filtering; over-size body → `SkillValidationFailed`).
- **Migration `AddSkills`** + a test that rolls the chain back past it and forward again.
- **`Skill` aggregate** — Thalos-free, mirroring the library rules.
- **Wiring in `AddDaedalusAgents` only**; a test pins that `AddDaedalusMemory` registers no skills.
- **Two starter procedures** — `skills/daedalus-migrations`, `skills/thalos-release` — copied next to every host by a `Content` item on `Daedalus.Api.csproj`.
- **Agent config** — `skills__*` beside `memory__*`, `Skills: ["*"]`, instruction line.
- **ArchUnit** loads `Thalos.NET.Skills`; both directions proven.

**Suites at merge: build 0 warnings; unit 910 (Domain 275, Application 382, Unit 123, Infrastructure 130); integration 361; browser 99 with `Skipped: 0`.** Pre-push review PASS, 0 blockers, 0 warnings — `docs/pre-push-review-2026-08-19-1830.md`.

## Three bugs this phase found, and what each taught

1. **Dev hosts could not start.** `Thalos:Skills:Roots: ["skills"]` resolved against the *content root*, which under `dotnet run`/Aspire is the **project** directory — but `skills/` is authored at the repo root and only copied to the **output** directory. Every test passed and the container image worked (there content root *is* the output dir), so **only the AppHost smoke run could see it**. Fixed by falling back to `AppContext.BaseDirectory`. *Lesson: the smoke run is not ceremony — it is the only gate that exercises a development host.*
2. **A CI-only regression from this phase's own earlier fix.** Task 12 had pointed `ApiWebApplicationFactory` at `AppContext.BaseDirectory`; because Api, Console and Web each ship an `appsettings.json`, the test host then read whichever copied last. Passed locally 361/361, failed on the CI runner with an empty agent list. Fixed by **reverting** it — bug 1's fallback made it unnecessary. *Lesson: Task 12 fixed a real symptom at the wrong layer; the production path was what needed changing.*
3. **A phase-1.1 auth defect** (below), split to its own PR.

## Auth defect fixed alongside (#239) — was pre-existing, not 1.3

Against real Keycloak, `GET /api/agents` answered 200 while **every `AgentSessionsController` endpoint answered 401** — so the agent UI could not work at all. Diagnosed from the 401 carrying **no `WWW-Authenticate` header**, proving authentication succeeded and the rejection came from `HttpSecurityContextFactory.TryCreate`: the token had no `sub`, so `ClaimsSecurityContext.Id` fell through to `AnonymousId`. Cause: both clients declared `defaultClientScopes: ["profile", "email"]`, omitting Keycloak's built-in **`basic`** scope, where `sub` moved in KC 24+. Fixed and verified (`/api/agents/sessions` → 200).

**Open follow-up:** `AgentEndpointsSmokeTests` substitutes `HeaderTestAuthHandler`, so **the real Keycloak claim shape is exercised by no test** — which is why this survived phases 1.1 and 1.2. A test that boots against real claims would close it. Phase-1.1 scope, not started.

## Open decisions (user)

**None blocking.**

1. Carried over from 1.1: manual sample smoke of Thalos `samples/Thalos.Sample.Console` with a real `ANTHROPIC_API_KEY`; save the transcript under `Thalos.NET/docs/samples/`.
2. Two untracked pre-pivot files (`docs/regression-report-2026-03-01-1800.md` + its screenshots folder) — user chose to leave them alone.

## Known follow-ups

**From the 1.3 design §10, recorded and not started:** skill usage counters; a pgvector `ISkillIndex` for when the corpus outgrows the in-process one (the interface is already the seam); the task runner and mid-run clarification, which belong to 1.4–1.6.

**Accepted limitations in Thalos.NET 0.3.0:** `SyncAsync` is an unguarded read-modify-write, so two hosts syncing concurrently can flap a skill during a rolling deploy (documented in the README's operational notes); `SkillStoreContractTests` has no fact for `GetAsync` returning inactive rows nor for concurrent same-name upsert; the residual search side channel persists at the ceiling; `SkillCatalogue`'s render cache is unbounded and keyed by unvalidated globs.

**Environment (this machine, pre-existing):** the local pgvector data volume is stale — PostgreSQL warns about a collation-version mismatch (created under glibc 2.41, OS provides 2.36). `docker volume rm daedalus_postgres_data` or `REINDEX DATABASE daedalus;` fixes it. It did not block the 1.3 smoke run. **Also note: Aspire reuses an existing Keycloak container, so a `keycloak-realm.json` change only takes effect after `docker rm -f daedalus-realm-*`** — a change can otherwise appear applied while doing nothing.

**Carried over from 1.1/1.2:** `AgentSession.RowVersion` inert on Npgsql; the Keycloak realm has no `developer` role (gates the mutating `roslyn__apply_*`/`rename_*` tools and shared-owner memory delete); 9 Keycloak-container integration tests fail with connection refused on this machine; `#app` loading styles persist after Blazor boot (cosmetic). Memory-phase deferrals M4, M5, M8 and the full-index-rebuild affordance all stand.

## Recommended next step

Start **phase 1.4 — Channels: Telegram (+ CLI) via `IChannelAdapter` + `ZeroAlloc.Outbox`** (#230). It depends on 1.1 only, so nothing from 1.2/1.3 blocks it. No design doc exists yet — `start-next-phase` will route to `superpowers:brainstorming` first.

Worth considering before 1.4: the untested-real-auth gap above. 1.4 adds a second channel, and a channel that cannot authenticate is the same class of failure discovered this phase.

Resume with `resume-work`, or say "continue" and `start-next-phase` will route from the roadmap.
