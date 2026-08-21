# Pre-push review — 2026-08-21 22:30 (local)

| Metric | Value |
|---|---|
| Date | 2026-08-21 22:30 |
| Branch | `feature/daedalus-channels` |
| Base Branch | `main` |
| Commits Reviewed | 15 |
| Files Changed | 39 |
| Lines Added | 4428 |
| Lines Removed | 24 |
| Verdict | **FAIL** |

This review runs as Task 10 of the `2026-08-20-thalos-channels-plan-b` SDD ledger — the final assembled-whole
verification pass before the branch is offered. Every task on this branch (Tasks 1–9) already carries its own
dedicated implementer/reviewer round in
`.superpowers/sdd/2026-08-20-thalos-channels-plan-b/progress.md`, each ending "review Approved" or "review clean
— no findings." This review does not re-derive those from scratch; it re-confirms plan adherence, code quality,
and commit hygiene with fresh spot-checks, and — the part only a fresh full run can do — actually re-executes
every test suite and the AppHost. That last step is what surfaced this review's one Blocker.

## Verdict rationale

**FAIL**, on exactly one Blocker: `Daedalus.Tests.Playwright.Api` fails **126/126** tests (see Regression Testing
Results below). Per this skill's verdict table, any test failure in Phase 5 is a Blocker. This finding is real
and reproducible, but **it is not caused by this branch** — see the root-cause analysis below — and I am
recording that distinction because misattributing it would be its own kind of dishonesty. Applying the rule
mechanically rather than rationalizing it away is the point of running this skill at all.

No other phase produced a Blocker. One Warning is recorded (Telegram end-to-end behavior unverified — see
Regression Testing Results); it does not change the verdict since the Blocker above already determines it.

## Plan Adherence Results

**Plan document used:** `docs/plans/2026-08-20-thalos-channels-plan-b.md`, spec authority
`docs/plans/2026-08-20-thalos-channels-design.md`, tracked task-by-task in
`.superpowers/sdd/2026-08-20-thalos-channels-plan-b/progress.md`.

Planned items (Tasks 1–9, all implementation tasks on this branch) / implemented: **9/9**. Task 10 itself (this
review + the smoke run) is verification-only and produces no source changes, matching its own brief.

- Task 1 — real-Keycloak identity guard: `tests/Daedalus.Tests.Integration/Authentication/RealKeycloakIdentityTests.cs`. Present.
- Task 2 — `ChannelConversation` entity + table: `src/Daedalus.Domain/Entities/ChannelConversation.cs`,
  migration `20260821155023_AddChannelConversations`. Present.
- Task 3 — `PostgresConversationMap`: `src/Daedalus.Agents/Channels/PostgresConversationMap.cs` +
  `ChannelConversationConfiguration.cs`. Present.
- Task 4 — durable outbound delivery via ZeroAlloc.Outbox: `ChannelMessageQueued.cs`,
  `ChannelOutboxServiceCollectionExtensions.cs`, migration `20260821163952_AddChannelMessageOutbox`. Present.
- Task 5 — outbox → channel adapter routing: `ChannelMessageQueuedDispatcher.cs`. Present.
- Task 6 — host wiring composition root: `DaedalusChannelsServiceCollectionExtensions.cs`
  (`AddDaedalusChannels`). Present.
- Task 7 — Telegram in the API host: `src/Daedalus.Api/Program.cs` (`AddDaedalusChannels` call),
  `appsettings.json` `Thalos:Channels:Telegram` shape, `src/Daedalus.AppHost/Program.cs`
  `telegram-bot-token` parameter, `ApiHostChannelWiringTests.cs`. Present.
- Task 8 — `Daedalus.Cli` interactive host: `src/Daedalus.Cli/` (new project), `includeConsoleChannel: true`.
  Present.
- Task 9 — architecture tests pinning channel layering: `CleanArchitectureTests.cs`, facts 20→23. Present.

**Missing implementations:** none.

**Unplanned changes:** none of consequence. The diff matches the task list closely; the one cross-task correction
(`d6cf211 fix(design): the outbox has no producer in 1.4 — section 9 yields to section 5`) is a documented,
plan-level ruling recorded in `progress.md` under Task 6, not a silent scope change — the design doc itself was
corrected in the same commit with the original (wrong) claim quoted and marked wrong, rather than rewritten in
place. This is the kind of "unplanned but disclosed and justified" change the skill's Phase 2 treats as
acceptable.

## Code Quality Results

Reviewed the full diff; spot-checked in depth the highest-risk new files:
`ChannelMessageQueuedDispatcher.cs`, `DaedalusChannelsServiceCollectionExtensions.cs`,
`PostgresConversationMap.cs`, `src/Daedalus.Api/Program.cs`, `src/Daedalus.AppHost/Program.cs`,
`src/Daedalus.Cli/Program.cs`.

**No findings at Blocker or Important severity.**

Observations (Info, not scored against the verdict — all already recorded and consciously accepted in
`progress.md` by each task's own reviewer):

- `PostgresConversationMap.BindAsync` uses a raw parameterized `INSERT ... ON CONFLICT ... DO UPDATE` rather than
  an EF Core `SaveChanges` round trip, specifically to close a real TOCTOU race (two processes — Telegram poller
  and CLI host — can bind the same conversation concurrently). The XML doc explains why; the choice is sound and
  the interpolated-string overload used (`ExecuteSqlInterpolatedAsync`) is the parameterized one, not naive string
  concatenation — no SQL injection risk despite the raw SQL.
- `ChannelMessageQueuedDispatcher` treats an unknown `ChannelId` as "log and drop" rather than throwing, with a
  documented rationale (a missing adapter registration is permanent; throwing would burn the outbox's retry
  budget before dead-lettering something that could never succeed). Reasonable.
- `AddDaedalusChannels`'s `includeConsoleChannel` parameter and `Replace`-not-`TryAdd` dispatcher registration are
  both documented with the specific ordering hazards they avoid (double-registered `OutboxWorkerService`;
  `TryAdd` losing to a same-type registration depending on call order) — both were independently mutation-tested
  per Task 6/7's review trail in `progress.md`.
- Several "minor (deferred)" items are recorded across Tasks 6–9 in `progress.md` (e.g., `CreatedAt` has no
  injected `TimeProvider`; hardcoded table/column names in the raw `INSERT` could drift silently from
  `ChannelConversationConfiguration`; `Daedalus.Application` has no EF Core architecture boundary rule, only
  `Daedalus.Domain` does; an invalid `DefaultAgent` only fails at runtime when a message arrives, not at host
  start). None were escalated by their own task reviewers and none are re-escalated here — they are documented,
  accepted technical debt, not gaps in this review.

## Commit Hygiene Results

**Commit messages:** all 15 commits use `type(scope): summary` conventional-commit form, headers well under 100
characters (longest is 80), imperative mood, specific. No hygiene issues.

**Secrets scan:** clean across this branch's diff. Grepped the full diff for API-key/secret/password/token/
`-----BEGIN` patterns; every match is either a false positive (EF Core `InputTokens`/`OutputTokens` model
properties, `UserSecretsId` GUIDs which are not secrets, test fixture placeholder ids like
`ConversationId("482910337")`, the pre-existing dev-only `Password=postgres` local connection-string convention
already used elsewhere in the repo) or an empty/comment-only reference to `BotToken` (the literal string
`"BotToken": ""` and prose explaining how to set it via user-secrets — never a value that looks like a real
token).

**Incidental finding, out of this branch's scope:** the repo-root `.mcp.json` (tracked, not gitignored) has a
`context7` MCP server entry carrying a literal `CONTEXT7_API_KEY` value. This file is **not part of this branch's
diff** — `git log --all -- .mcp.json` shows it was last touched in `71533f7`, which predates this branch's fork
point (`14b94e5`) on `main`. Recording it here because it surfaced during the secrets scan and is real, not
because it belongs to this review's verdict. Recommend a separate look (rotate the key, move it to a
gitignored/user-secrets-style location) outside this branch.

**Unintended files:** none. No `node_modules`, build artifacts, OS files, or other accidental inclusions in the
39 changed files.

**Merge conflict markers:** none (`git diff main...HEAD | grep -E "^\+<<<<<<<|^\+=======|^\+>>>>>>>"` — no
matches).

**Large files:** none of the 39 changed files exceed 200KB.

**`.mcp.json` hardcoded absolute path:** `src/Daedalus.Cli/.mcp.json` (new in this branch) hardcodes
`C:/Projects/Prive/daedalus/Daedalus.sln` as an argument to the `roslyn` MCP server. Checked against precedent:
`src/Daedalus.Api/.mcp.json` (pre-existing, committed in `1bb6fe7`, predates this branch) has the identical
pattern. This branch is following an established repo convention, not introducing a new hygiene issue — noted as
Info, not scored.

## Regression Test Results

Framework(s) detected: xUnit (`Unit.Domain`, `Unit.Application`, `Unit`, `Unit.Infrastructure`, `Integration` with
Testcontainers), NUnit + Microsoft.Playwright (`Playwright.Browser`, `Playwright.Api`). Docker confirmed running
(`docker ps` showed a live daemon) before starting, per the brief's explicit instruction not to assume it.

Commands executed (all `--no-build` against a single prior `dotnet build -warnaserror` — 0 warnings, 0 errors):

| Project | Result |
|---|---|
| `Daedalus.Tests.Unit.Domain` | **283 passed, 0 failed** |
| `Daedalus.Tests.Unit.Application` | **396 passed, 0 failed** |
| `Daedalus.Tests.Unit` | **126 passed, 0 failed** (includes 23 architecture facts) |
| `Daedalus.Tests.Unit.Infrastructure` | **130 passed, 0 failed** |
| `Daedalus.Tests.Integration` | **380 passed, 9 failed** — all 9 are `AuthenticationFlowTests`, pre-existing baseline (a `traefik` container holds `localhost:8080`/`443` on this machine); not a regression, not touched |
| `Daedalus.Tests.Playwright.Browser` | **99 passed, 0 failed, 0 skipped** (5 m 22 s) — matches the documented expectation exactly |
| `Daedalus.Tests.Playwright.Api` | **BLOCKER — 0 passed, 126 failed** (14 s) |

### Blocker: `Daedalus.Tests.Playwright.Api` — 126/126 failing

Every test fails identically in `OneTimeSetUp`:
```
Npgsql.PostgresException : 42P01: relation "Skills" does not exist
```

**Root cause** (traced via the stack trace and `tests/Daedalus.Tests.Playwright.Api/Fixtures/E2EServerFixture.cs`):
the fixture accesses `_factory.Services` to get a scope for creating the DB schema, but that access is what
lazily triggers `WebApplicationFactory.StartServer()` — which starts the real host, including every
`IHostedService`. `Thalos.Skills.SkillSyncService.StartingAsync()` (from the `Thalos.NET.Skills` package, wired
by `AddDaedalusAgents`) queries the `Skills` table immediately on host start, before the fixture's own
`dbContext.Database.EnsureCreatedAsync()` call — which sits later in the same method — has a chance to run. The
schema genuinely does not exist yet at that point.

**Not caused by this branch.** `SkillSyncService`/`AddDaedalusAgents`'s Skills wiring was already on `main` before
this branch's fork point — `git log --graph main` shows `cb5e2e9 chore(state): phase 1.3 skills complete;
handoff to 1.4 channels` sitting *before* `14b94e5`, which `git merge-base main HEAD` confirms is exactly this
branch's fork commit. This branch's diff does not touch `E2EServerFixture.cs` or anything Skills-related.

**Why it was never caught:** `.github/workflows/ci.yml` excludes Playwright entirely from CI
(`--filter "FullyQualifiedName!~Playwright&..."` on both `dotnet test` invocations, no separate Playwright job).
No prior report in `docs/*.md` records a `Playwright.Api` pass count — only `Playwright.Browser` results are ever
documented. This suite has likely been silently broken since Skills' `SkillSyncService` was first wired into
`Daedalus.Api`'s host in phase 1.3.

**Not fixed here** — per this task's global constraint (report, don't fix). Likely fix: ensure the schema
(including the `vector` extension) is created before `WebApplicationFactory`'s services are first accessed —
e.g., a real Testcontainers-driven migration run instead of a post-hoc `EnsureCreatedAsync`, or restructuring the
fixture so schema creation happens from a `ConfigureServices`/`ConfigureTestServices` hook that necessarily runs
before hosted services start.

### Warning: Telegram end-to-end behavior not verified

Task 10's actual purpose — exercising `/help`, `/agents`, `/new`, a tool-using turn, `/status`, `/cancel`
mid-turn, `/end` over a real Telegram bot from a real phone, and confirming edit-in-place rendering and operator
notices arrive — **could not be performed**. No `telegram-bot-token` is configured anywhere reachable in this
environment (`dotnet user-secrets list` against `Daedalus.AppHost` shows no such entry; no
`PARAMETERS__TELEGRAM_BOT_TOKEN` env var), and there is no phone or Telegram client session available regardless.
The AppHost itself was started and confirmed healthy with channels wired in and no errors (see the companion
task report, Part 2), which confirms the host-composition side of this phase; the Telegram-specific
rendering/notice behavior remains unconfirmed by this review. Full detail, including what secondhand evidence
exists and its limits, is in
`.superpowers/sdd/2026-08-20-thalos-channels-plan-b/task-10-report.md`.

## Remediation Plan

1. **[Blocker] `Daedalus.Tests.Playwright.Api` fails 126/126** —
   `tests/Daedalus.Tests.Playwright.Api/Fixtures/E2EServerFixture.cs`, `GlobalSetupAsync()`.
   **Fix**: reorder schema creation so it happens before any hosted service can run — either drive it through a
   real migration run against the Testcontainers Postgres instance before the `WebApplicationFactory` is built,
   or move the `CREATE EXTENSION`/`EnsureCreatedAsync`/seed calls into a `ConfigureTestServices`-time hook that
   necessarily executes before `IHostedService.StartAsync`. Verify by re-running the full suite and confirming
   126/126 pass, plus a targeted check that `SkillSyncService` (or any other hosted service that queries the DB
   at startup) sees a fully-created schema.
   **Effort estimate**: Moderate (5–30 min) to restructure the fixture; add time to actually run the 126-test
   suite (~15s) to confirm.
   **Scope note**: this fix should land as its own reviewed change, not bundled into this branch, since the root
   cause predates and is unrelated to the channels work.

2. **[Warning] Telegram bot behavior unverified** — no code fix; this needs an operator with a configured bot
   token and a phone to actually run Task 10's Part 2 manual session. Recommend re-running that specific check
   once a token is available, before treating the channels phase as fully done end-to-end.

3. **[Info, out of scope] repo-root `.mcp.json` contains a literal `CONTEXT7_API_KEY`** — not part of this
   branch, but worth a separate look: rotate the key and move it out of a tracked file.
