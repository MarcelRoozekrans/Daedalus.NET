# Session State

**Last session:** 2026-08-17
**Current milestone:** 1 — Hermes-Style Agent Framework
**Current phase:** 1.2 — Memory (`Thalos.NET.Memory` port + Rag.NET adapter) → **implementation complete**, closing steps (regression, pre-push review, merge, `complete 1.2`) with the coordinator
**Branch state:** `feature/thalos-memory` off `main` (28 commits, not pushed). Thalos.NET **0.2.0** is on nuget.org and consumed directly — there was never a local pack this phase, so plan B's "switch pins to nuget.org / delete `packages-local`" step (Task 21.1) was already satisfied at Task 1 and nothing remains of it.
**Release context (from 1.1, unchanged):** repo `MarcelRoozekrans/Daedalus.NET`; Daedalus **0.1.0** released (tag `v0.1.0`, ghcr.io images `0.1.0`/`0.1`/`latest`). GitVersion + release-please + commitlint + gated `publish-release`; runbook `docs/release.md`.

## Last completed

- **Plan A** (Thalos.NET, `C:\Projects\Prive\Thalos.NET`): memory shipped in **0.2.0** — eight packages on nuget.org, incl. the new `Thalos.NET.Memory` (`IMemoryStore`/`IMemoryIndex`/`IMemoryService`, `memory__*` tools, five `Memory*Event`s, six `AgentErrorCode.Memory*`) and `Thalos.NET.Memory.RagNet` (Rag.NET pgvector adapter). `Thalos.NET.Testing` gained `MemoryStoreContractTests` (21 facts) and `MemoryIndexContractTests`.
- **Plan B** (Daedalus, `docs/plans/2026-08-17-thalos-memory-plan-b.md`): tasks 1–19 done, every group spec- and quality-reviewed with the findings fixed and recorded in the plan's §0.7. What shipped:
  - Curated memory on **Thalos.NET.Memory 0.2.0 + `.Memory.RagNet`**: `AgentMemories` table + `PostgresMemoryStore` (row-value keyset streaming) as the source of truth, Rag.NET `rag_chunks` on the *app* database (768-dim `nomic-embed-text` via Ollama) as a rebuildable index.
  - `AddDaedalusAgents` (API: agents + memory, owns the Rag.NET schema, runs the reindex sweeper) vs `AddDaedalusMemory` (console/Ralph: memory only, creates no schema) — **mutually exclusive and enforced** at registration.
  - Ralph learnings persist and recall through the Application port `ILearningsMemory` (adapter `ThalosLearningsMemory`, shared owner `daedalus`, kind `learning`). The MCP `search_learnings` tool is a thin recall; Thalos agents get the `memory__*` tools instead.
  - Migration `AddAgentMemories` copies every `StructuredLearnings` row (`index_pending = true`, embedded later by the sweeper) and drops the table. The whole legacy slice, `Pgvector.EntityFrameworkCore` and every `UseVector()` are gone; the pgvector **image** and `CREATE EXTENSION` stay, for Rag.NET.
  - API `GET /api/agent-memories`, `GET /{id}` (404s on archived), `DELETE /{id}?hard=` — own-only forget, shared-owner forget behind `DeveloperPolicy`; reads post-filtered by `MemoryScope.Includes` with `agentId` as *caller context*, not a filter.
  - Blazor **Memories panel** on `/agent` (recalled-this-turn from SSE + paged browse/forget); five new SSE kinds `memory-recalled | memory-stored | memory-recall-failed | memory-index-pending | memory-quarantined`.
  - Two branch fixes worth knowing: the AppHost now uses `.WaitForCompletion(migrations)` (it previously started hosts before migrations applied), and the Playwright browser fixture fails loudly instead of reporting `Inconclusive` (exit 0) when host start fails — that had been hiding the Agent category since Task 6.
  - Task 19: ArchUnit gained the memory assemblies and a `^Rag(\.|$)` boundary rule (Domain/Application/Infrastructure/API/Web); README and `docs/architecture-diagrams.md` §14 rewritten for memory.
- Suites at the end of Task 19: build 0 warnings; unit **868** (Domain 255, Application 368, Unit 115, Infrastructure 130); integration 343; browser 99 (Agent category 5, `Skipped: 0`) as of the Task 18 review.

## Open decisions (user)

**None blocking.** Everything phase 1.2 needed was decided during execution and recorded in the plan's §0.7.

1. Carried over from 1.1: manual sample smoke of Thalos `samples/Thalos.Sample.Console` with a real `ANTHROPIC_API_KEY`; save the transcript under `Thalos.NET/docs/samples/`.
2. Carried over from 1.1: `tests/Daedalus.Tests.Unit` is excluded from the Release solution configuration — that was fixed in `01253dc`; re-confirm on the next CI run that both unit and integration projects build in Release.

## Known follow-ups (recorded in the plans)

**Phase 1.2, deliberately deferred (plan B §0.7, "Accepted / deferred"):**

- **M4 — reindex log ramp.** `ReindexPendingMemoriesHostedService` logs "index unavailable" at `Information` on every retry. It fires at most once per `RetryInterval` (2 min default), so the noise ceiling is low; ramp the level down after N consecutive unavailable probes only if an operator complains.
- **M5 — command timeout on the migration copy.** The `INSERT … SELECT` that copies `StructuredLearnings` runs under EF's default 30 s. It is one set-based statement over a table holding thousands of rows at most, and a timeout fails the migration loudly and rolls back — a knob would add configuration surface with no failure mode to protect.
- **Full index rebuild has no operator affordance.** The reindex sweeper runs with `PendingOnly = true`, so recovering from a dropped or dimension-mismatched `rag_chunks` needs a manual `UPDATE "AgentMemories" SET "IndexPending" = true …` beside the `DROP TABLE` (documented in the README's operational notes). `ReindexOptions` also has a `PendingOnly = false` mode; exposing it as an admin endpoint or a one-shot startup flag would remove the SQL step.
- **M8 — crash-consistency check mid-sweep.** Killing the host during a reindex sweep leaves rows `index_pending` and the next sweep repeats them; that property belongs to Thalos' `ReindexAsync` and is covered by its own suite. A Daedalus-side test would exercise `BackgroundService`, not our code.

**AppHost environment findings on this machine (not caused by phase 1.2, out of its scope):**

- The local pgvector **data volume is stale**: PostgreSQL logs collation-version mismatch warnings (created under glibc 2.41, the OS now provides 2.36). `docker volume rm daedalus_postgres_data` (or `REINDEX DATABASE daedalus;`) is the fix. This is why the `AddAgentMemories` migration was verified against Testcontainers rather than the local volume.
- The related symptom — hosts coming up against a database with only `rag_chunks` and no EF tables — is **fixed** on this branch: the AppHost used `WaitFor(migrations)`, which releases as soon as the one-shot job *starts*, and now uses `.WaitForCompletion(migrations)` (`4d96d66`).

**Carried over from phase 1.1:**

- `AgentSession.RowVersion` is inert on Npgsql (the store uses atomic UPDATEs) — `UseXminAsConcurrencyToken()` or drop the column.
- The Keycloak realm has no `developer` role yet. It now gates two things: the mutating `roslyn__apply_*`/`rename_*` tools **and** deleting a shared-owner memory via `DELETE /api/agent-memories/{id}`. Add it to `keycloak-realm.json`.
- 9 Keycloak-container integration tests fail with connection refused on this machine (pre-existing infra).
- `#app` loading styles persist after Blazor boot (cosmetic, pre-existing).
- Thalos follow-ups: per-agent Sentinel identity, per-session Sentinel rate limiting, cache-token usage fields, ref-counted agent invalidation, `ThalosOptions` binding from `IConfiguration` (typed-id converter).

## Recommended next step

Start **phase 1.3 — Skills** (reusable procedures the agent loads and refines, Rag-backed; #229) with `superpowers:brainstorming` against the agent-core design doc. Phase 1.3 builds directly on 1.2: skills are retrieved the same way memories are, so the Rag.NET index, the `AgentMemories`/`rag_chunks` split and the `MemoryScope` visibility rule are the patterns to extend rather than re-invent.
