# Session State

**Last session:** 2026-09-19
**Current milestone:** 1 — Hermes-Style Agent Framework (**6 of 8 phases complete**)
**Current phase:** 1.6 — Schedule diagnostics. **Complete and merged.**
**Branch state:** `main` at `8bc67aa`, synced with `origin/main`. Clean tree apart from the two
deliberately-untracked pre-pivot regression files.

## Current Position

Phase 1.6 is done. Merged via [#244](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/244) into
the planning branch and [#245](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/245) onto `main`
— 19 commits, 48 files, +5,618 lines.

The `/schedules` page answers one question: **a digest didn't arrive — where did it die?** One
`IScheduleDiagnostics` returns a **verdict, not rows**; a Blazor page, an HTTP controller and two
read-only agent tools all consume it, so a page and an agent cannot give an operator different
answers about the same run. A test proves that rather than asserting it — it boots the composed
host, seeds one execution, hits the controller route and resolves the interface from that same host,
then checks the tool's text carries the controller's own values rather than hard-coded constants.

**Suite: 1,569 passed / 135 failed / 1,704 total**, against a pre-work baseline of 1,519 / 135 /
1,654. **+50 passing, and not one pre-existing failure changed.** `Playwright.Browser` ran to
completion at 99/0/99 — it was slow, never broken.

### The proof phase 1.5 never got

**The application was verified end to end in a real browser**, through a real Keycloak login, with
all eight statically-reachable verdicts rendering against seeded data. **Resend was clicked for
real** — HTTP 200, the dead-lettered row un-dead-lettered in the database, verdict flipped to
`Delivered` on reload.

One side effect worth remembering: seeding a past-due **enabled** schedule against a live AppHost
makes the real scheduler claim it and dispatch a real subagent run against the real Anthropic API
key. It died on its own token budget, so the spend was small — but do not seed enabled schedules
against a running AppHost casually.

### The boot blocker, correctly diagnosed

This file previously recorded that phase 1.5's pin move 1.2.0 to 1.2.1 "cannot introduce a 0.1.4
conflict". **That was wrong, and this phase disproved it.**

`ZeroAlloc.Results` **1.2.1 ships `AssemblyVersion 0.1.0.0`** — a downgrade from 1.2.0's `1.2.0.0` —
because the upstream publish workflow builds without `-p:Version`, so only the *package* version was
bumped. Every consumer in the graph asks for a higher version than the file provides, so the binder
refuses it. `ZeroAlloc.Authorization` merely failed first.

Tests were unaffected because **VSTest's `PlatformAssemblyResolver` probes by simple name and ignores
version entirely** — which is exactly how a green 442-test suite coexisted with an unbootable
application for a whole phase. The fix pins `ZeroAlloc.Results` to 1.2.0 via `VersionOverride` in 4
src and **7 test** projects; the test projects are pinned deliberately, because leaving the test
graph on 1.2.1 would preserve the blindness.

Full diagnosis: `docs/plans/2026-09-18-zeroalloc-results-assembly-version-spike.md`.

## Blockers

1. **The `ZeroAlloc.Results` pin is a workaround, and the upstream fix is unmade.** Republish
   **1.2.2** with `-p:Version` supplied to the build step in `publish-from-manifest.yml` across the
   ZeroAlloc org, then delete the 11 `VersionOverride` lines — they carry a comment saying so.
   **`ZeroAlloc.Specification` 1.1.0 carries the same mis-stamp** and will bite the moment anything
   references a higher version. `ZeroAlloc.Authorization`'s `9.9.9.0` is worth a sanity check.
   *This is a package publish and needs a human.*

2. **The scout agent still has no tools to do its job.** Its prompt asks for a repository, PR and CI
   sweep; no git or GitHub MCP tool exists in the solution. Carried from 1.5 — the first real digest
   will still be empty.

3. **`ci.yml` excludes `~Playwright`**, so ~99 browser tests and the whole `Playwright.Api` suite
   never run in CI at all. The `Playwright.Api` fixture bug — 126 of 126 failing in `OneTimeSetUp` on
   `relation "Skills" does not exist` — is therefore invisible there. Carried from 1.4.

4. **`Daedalus.Cli` has a second, independent boot failure**, previously masked by the assembly
   blocker: `IProjectRepository` is unregistered for `WorkspaceOrchestrator`. It reproduces only
   under `DOTNET_ENVIRONMENT=Development`, not `ASPNETCORE_ENVIRONMENT`, because the CLI uses the
   generic Host builder. Out of scope for 1.6; nothing on the CLI path was needed.

5. `appsettings.Development.json` carries `postgres`/`postgres` while the compose container
   `daedalus_postgres` uses `daedalus`/`daedalus`. Pre-existing, costs every onboarding an hour.

## Known Narrowings — state these rather than paper over them

1. **Four and a half of the five death causes are representable.** "Cron wrong" — the second half of
   death-cause 1 — is not. An enabled schedule whose cron never fires reads `NotYetDue` or `Overdue`;
   the page cannot say "your cron is wrong."

2. **The agent write-boundary is enforced by tool-surface absence, not by authorization.** The resend
   POST sits under the same `AgentUse` policy as the reads. No agent tool can reach it today, which
   is why this is safe — but it becomes brittle the day a generic HTTP tool exists.

3. **A schedule that is both overdue and has a failed last run renders `Failed`**, with a past
   `Next run` in the adjacent column and no visual cue. An operator scanning "is the sweeper alive"
   gets no signal on those rows.

4. **A non-existent schedule id returns `200 OK` with an empty list**, so "no such schedule" and
   "schedule with zero runs" are indistinguishable to a caller. The agent tool's wording covers both
   cases honestly; the service-level fix needs a `Result`-shaped return and was deferred.

## Open Decisions (user)

1. **`AgentErrorCode` gaining a `None = 0` member.** `Validation` is member 0, so `default(AgentError)`
   is indistinguishable from a real validation failure. Renumbering is impossible — the enum is
   serialized. Still awaiting a decision. **Phase 1.6 took the opposite lesson deliberately:**
   `RunVerdict` reserves `Unknown = 0` as a sentinel precisely because of this.
2. **The deadline/budget asymmetry** in `ISubagentRunner`. Left as-is.
3. **The stranded-run reaper.** 1.6 makes stranded runs *visible*, which was its precondition;
   automating the recovery is still open.
4. **Cross-origin schedule name collision** — a config entry sharing a `Name` with an agent-created
   schedule fails at boot with a raw Postgres unique violation. Still unreachable: 1.6 ships no
   agent-created schedules, because `schedule__create` was deliberately cut.

## Recommended Next Step

**Phase 1.7 — Ralph retirement + ZeroAlloc migration** is next in the roadmap. Before it, two items
are worth considering:

- **Blocker 1 is the cheapest high-value fix available** and unblocks deleting 11 workaround lines.
- **Blocker 2 gates the feature actually being useful.** The scheduling machinery, the page and the
  agent tools are all correct, but the first real digest will still be empty until the scout has
  repository tooling. A diagnostics page that reports a healthy run producing nothing is an odd
  place to stop.

## How This Phase Was Executed — worth carrying

Subagent-driven development: 10 tasks, fresh implementer each, independent review after each, plus a
final whole-branch review on the most capable model. **Twenty-two rulings** are preserved with their
reasoning and cost-if-wrong in `docs/plans/2026-09-18-phase-1.6-rulings.md`.

**Three of those rulings corrected defects in the implementation plan itself**, and the most serious
was found by a reviewer rather than the author: the overview originally diagnosed only the *latest
execution*, so a dead sweeper would show yesterday's run as `Delivered` while today's digest never
fired — death-cause 2 for the common case, on a page built to catch exactly that.

**Two lessons worth keeping:**

- **A task-scoped diff review has a structural blind spot.** Task 3 added a mapped property without
  updating a hand-written `INSERT`'s column list; the guard test that caught it lived in a file
  Task 3 never touched, so it was absent from the review diff. The net for that is a full-suite run
  before every commit — which that task's dispatch had omitted.
- **Prose that stops matching behaviour is its own defect class.** Two verdict doc comments asserted
  the *opposite* of the shipped code, and one correction was itself corrected into a different wrong
  statement. A solution-wide sweep then found the same falsehood in the **human-facing UI text**.
  Asking for the sweep rather than assuming the known sites were all of them is what caught it.

## Environment

**Docker UP** (29.5.2), `daedalus_postgres` healthy, `traefik` holding 8080. Keycloak is **not**
running standalone — it is started by the Aspire AppHost.

- Bare `dotnet run --project src/Daedalus.Api` fails on `RagNetMemoryOptions.ConnectionString is
  required`. Supplying `ConnectionStrings__daedalus` gets it through the whole DI graph and into the
  middleware pipeline; it then fails in `AuthenticationMiddleware` without Keycloak. **Use the
  AppHost**, which brings up Postgres, Keycloak and Ollama together and works end to end.
- Orphaned `dcp.exe` or dashboard processes from a killed AppHost run hold ports. Aspire reuses an
  existing Keycloak container, so a `keycloak-realm.json` change needs `docker rm -f daedalus-realm-*`.
- `PostgresFixture` builds the test schema with `EnsureCreatedAsync`, **not** `MigrateAsync`, so no
  test exercises migrations. 1.6's `AddFailedAtStep` migration was verified by hand against a clean
  scratch database, as 1.5's two were. Nothing pins that in CI.
- `dotnet` is a Windows process: give it Windows-style paths, never git-bash POSIX paths.
- Long test runs exceed a 2-minute default timeout. `Playwright.Browser` takes ~11 minutes and is
  excluded from CI; it stalled two implementers in this phase who waited on a notification
  mechanism that does not fire in this environment.
