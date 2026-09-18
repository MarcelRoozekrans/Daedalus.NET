# Session State

**Last session:** 2026-09-18
**Current milestone:** 1 — Hermes-Style Agent Framework (**5 of 8 phases complete**)
**Current phase:** 1.5 — Subagents & autonomous runs. **Complete and merged.**
**Branch state:** `main` at `05ef6b8`, synced with `origin/main`. Clean tree apart from the two
deliberately-untracked pre-pivot regression files.

## Current Position

Phase 1.5 is done. Plan A shipped as **Thalos.NET 0.5.0**; plan B merged via
[#242](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/242) — 36 commits, 66 files, +10,797 lines.

Scheduled autonomous runs work: a per-minute `BackgroundService` sweeper claims due schedules
atomically, and each run walks scout → writer → deliver through the outbox, persisting each step's
output. The terminal step queues `ChannelMessageQueued` in the same transaction that marks the run
`Done` — the writer phase 1.4 had been waiting for.

**Suite: 1,519 passed / 135 failed / 1,654 total**, against a pre-work baseline of 1,416 / 135 / 1,551.
**+103 passing, and not one pre-existing failure changed.** The 135 are the two known-failing suites:
`Playwright.Api` at 126 from its pre-existing `E2EServerFixture` ordering bug, and 9
`AuthenticationFlowTests` that fail while a `traefik` container holds port 8080.

### What replaced the saga

`ZeroAlloc.Saga` was dropped — a saga never receives its trigger event. Each guarantee was replaced
explicitly: multi-step state became a `ScheduledRunExecutions` row; correlation-key idempotency became
`UNIQUE (ScheduleId, OccurrenceAt)` plus `INSERT ... ON CONFLICT DO NOTHING`; step sequencing became
outbox messages with `IOutboxDispatcher<T>`; compensation became `Step = Failed` plus an operator notice.

**The guarantee is bounded and the code says so.** A crash after a subagent returns but before its step
commits re-runs that step and pays its tokens twice. **One step can be lost, never the whole run.**
Do not let anyone "improve" the docs into claiming exactly-once.

`ZeroAlloc.Scheduling` was also dropped — its EF job store needs a separate `SchedulingDbContext` that
ships no migrations and cannot be bootstrapped with `EnsureCreated`, and the package cannot be used
without that store at all. Both packages are now **enforced-absent by a `.csproj` scan** in
`CleanArchitectureTests`; an ArchUnit rule would be vacuous for an unreferenced package.

## Blockers

1. **The application cannot boot. This blocks more than it looks like.**
   Both `Daedalus.Api` and `Daedalus.Cli` crash during DI bootstrap with
   `FileNotFoundException: ZeroAlloc.Results, Version=0.1.4.0`. **Pre-existing and upstream:**
   `ZeroAlloc.Authorization` 2.1.0 — the latest published — is compiled against `ZeroAlloc.Results`
   0.1.4, while Thalos.NET 0.5.0 forces 1.2.x. Phase 1.5 only moved that pin 1.2.0 → 1.2.1, which
   cannot introduce a 0.1.4 conflict. Reproduced directly with `dotnet run --project src/Daedalus.Api`.

   The hosts boot **fine** in-process under `dotnet test`, which is why a green 442-test suite coexists
   with an unbootable app. Consequences: **the end-to-end AppHost run and the mid-flight-kill resume
   proof were never performed**, and the Telegram path is still unverified end to end. Needs an upstream
   fix, like the Saga generator defect. Worth filing.

2. **The scout agent has no tools to do its job.** Its prompt asks for a repository, PR and CI sweep;
   no git or GitHub MCP tool exists in the solution. The scheduling machinery is correct, but the first
   real digest will be empty until this is addressed. Relevant to 1.6.

3. Carried from 1.4 and still open: the `Playwright.Api` fixture bug (126/126, invisible in CI because
   `ci.yml` excludes `~Playwright`); no Telegram bot token.

## Open Decisions (user)

1. **`AgentErrorCode` gaining a `None = 0` member.** `Validation` is member 0, so `default(AgentError)`
   is indistinguishable from a real validation failure. This produced false-passing tests three times on
   one branch. Renumbering is impossible — the enum is serialized. Still awaiting a decision.
2. **The deadline/budget asymmetry** in `ISubagentRunner` — an over-deadline turn that succeeds is
   reported success; an over-budget one is reported failure. The reviewer argued this is correct and
   should not be unified. Left as-is.
3. **The stranded-run reaper, deferred from the final review.** A run whose step message dead-letters
   after eight attempts sits in a non-terminal step forever with no `LastError` and no notice, which
   contradicts the standing "the operator is always told something" rule. Deferred because it is new
   functionality, not a fix — **phase 1.6 builds management over these very tables and is its natural
   home.**
4. **Cross-origin schedule name collision**, deferred to 1.6: a config entry sharing a `Name` with an
   agent-created schedule stages a duplicate and fails at boot with a raw Postgres unique violation
   rather than an actionable message. Unreachable until 1.6 creates agent-origin schedules.

## Recommended Next Step

**Phase 1.6 — Schedule management**, `Surface: UI`. It is the natural home for three carried items
above: the stranded-run reaper, the cross-origin name collision, and the scout tooling gap.

Because its Surface is `UI`, `start-next-phase` will route it through `ui-design-system` — check whether
`docs/design/MASTER.md` exists first — and then `ui-workflow`'s `ui-phase` to produce a UI contract
before `writing-plans` runs.

**Consider resolving blocker 1 first.** A UI over `ScheduledRuns` that nobody can run locally is hard to
build and impossible to verify.

## How This Phase Was Executed — worth carrying

Subagent-driven development, 15 tasks, each with a fresh implementer and an independent review, plus a
whole-branch review at the end. **Forty rulings** are preserved with their reasoning and cost-if-wrong in
`docs/plans/2026-09-18-phase-1.5-rulings.md`.

**Eight of those rulings correct defects in the implementation plan itself — and all eight were found by
implementers running the work, not by the author writing it.** The plan claimed a runtime hazard that
does not reproduce, named three library methods that do not exist, specified an EF format specifier that
is not real, and twice carried a lesson into one place while leaving the same mistake standing in
another.

That ratio is the argument for keeping the expensive parts of this process rather than trimming them:
the mandated mutation checks, the "see it fail before you trust it" rule, and the instruction to report
honestly when a predicted failure does **not** occur. Each of those directly produced a finding here —
including the discovery that a race test had been passing without ever racing.

## Environment

**Docker UP** (29.5.2), `daedalus_postgres` healthy, `traefik` holding 8080.

- The compose Postgres uses `daedalus`/`daedalus` while the code default is `postgres`/`postgres`, so
  applying a migration locally needs an env-var override — and `dotnet ef migrations remove` cannot
  target it at all, because the design-time factory hardcodes credentials.
- `PostgresFixture` builds the test schema with `EnsureCreatedAsync`, **not** `MigrateAsync`, so no test
  exercises the migrations. Both phase-1.5 migrations were verified by hand against a clean scratch
  database this session — they apply cleanly and `xmin` is correctly elided from the DDL — but nothing
  pins that in CI.
- Aspire reuses an existing Keycloak container, so a `keycloak-realm.json` change needs
  `docker rm -f daedalus-realm-*`. Orphaned `dcp.exe` or dashboard processes from a killed AppHost run
  hold ports.
