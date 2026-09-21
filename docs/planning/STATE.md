# Session State

**Last session:** 2026-09-19
**Current milestone:** 1 — Hermes-Style Agent Framework (**8 of 9 phases complete**; only 1.8 remains)
**Current phase:** 1.7 — Daedalus ZeroAlloc migration. **Complete and merged** via [#257](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/257).

`CSharpFunctionalExtensions` and both `FluentValidation` packages are gone from the solution and guarded
by architecture tests; the hand-rolled CQRS layer is now `ZeroAlloc.Mediator` 5.1.1. All suites green:
unit 1078, Integration 505, Playwright.Api 126, Playwright.Browser 99.

Three test suites were also repaired along the way, in [#255](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/255)
and [#256](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/256). `Playwright.Api` had been providing
**zero coverage** — 0 of 126 passing, dying at fixture setup in 11 seconds — and now runs 126/126 in about
25 seconds. Nobody had noticed because CI excludes both Playwright projects and the Keycloak tests by name.

[#258](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/258) then lifted the `ZeroAlloc.Saga` ban and
re-justified `ZeroAlloc.Scheduling`'s, after re-measuring the upstream fixes rather than trusting issue
status. Saga is now a candidate for phase 2.2's workflow engine, and Milestone 3's premise is half retired
because `Saga.Orm` and `Outbox.Orm` ship with zero EF Core.
**Branch state:** `main` at `5058fa6`, synced with `origin/main`. Clean tree apart from the two
deliberately-untracked pre-pivot regression files.

## Current Position

Phase 1.9 is done, merged via [#247](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/247) —
16 commits, 44 files.

The scout can now observe a repository: commits on the default branch, merged and open pull
requests, issues opened or closed, and failed CI runs. That closes the gap where phases 1.4, 1.5 and
1.6 had built the channel, the scheduler and the diagnostics page for a digest whose scout could see
none of the four things its prompt asked for.

**A real GitHub read was performed** against a public repository and returned real data. The AppHost
boots and `/health` returns 200.

**Suite: Unit 1038 passing / 0 failed; Integration 492 passed / 9 failed.** The 9 are the known
Keycloak-dependent `AuthenticationFlowTests`.

### The write boundary

Interactive agents may comment, label and close. **The unattended scheduled run may not**, and that
is enforced by authorization rather than by naming:

- Reads live under the existing `daedalus` tool source; writes under a separate `repoaction` source.
- `repoaction__*` is bound to the existing `developer` policy in `Thalos:ToolPolicies`.
- A scheduled run executes as `schedule:daedalus` with roles `["reader"]`, so `DefaultToolAuthorizer`
  denies it **whatever its tool list says**.

This closes the weakness phase 1.6's own final review flagged in its resend endpoint, where the
boundary rested on tool-surface absence alone.

**The load-bearing test reads the shipped configuration.** `DetachedRuns:Roles` is the single line
that could silently delete the boundary; a test resolves it from the built host and asserts
`DeveloperPolicy` denies it. Adding `developer` there fails the build. Do not replace that test with
a hand-built principal — the original version did exactly that, and six other boundary tests stayed
green while the boundary was gone.

### The dependency blocker is fully resolved

`ZeroAlloc.Results` **1.2.2 is on nuget.org stamped `1.2.2.0`**, and Daedalus adopted it via
[#248](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/248). The central pin moved 1.2.1 →
1.2.2 and **all eleven `VersionOverride` lines are gone**. `Daedalus.Api.deps.json` resolves
`ZeroAlloc.Results/1.2.2` with `assemblyVersion=1.2.2.0`.

Root cause upstream was `publish-from-manifest.yml` building without `-p:Version`, so the assembly
took a fallback while pack overrode only `PackageVersion`. **The same defect was present in 23 of 27
repositories in the ZeroAlloc org**; all 23 are fixed and merged, tracked in
[ZeroAlloc-Net/.github#26](https://github.com/ZeroAlloc-Net/.github/issues/26).

## Blockers

1. ~~The Telegram delivery path is unverified end to end.~~ **CLOSED 2026-09-20.** A real digest was
   delivered to a real chat. The full chain ran: sweeper claimed the schedule, the scout swept the
   repository with the phase 1.9 GitHub tools, the writer produced prose, and the outbox dispatched
   `ChannelMessageQueued` to Telegram — five outbox rows, all succeeded, `RetryCount` 0, no dead
   letters, execution `Done` with no `FailedAtStep`.

   **Verifying it found two real bugs, neither findable any other way.** See "What the first real
   digest cost" below.

2. **`ci.yml` excludes `~Playwright`**, so ~99 browser tests and the whole `Playwright.Api` suite
   never run in CI. The `Playwright.Api` fixture bug — 126 of 126 failing in `OneTimeSetUp` on
   `relation "Skills" does not exist` — is therefore invisible there. Carried from 1.4.

3. **`Daedalus.Cli` has an independent boot failure:** `IProjectRepository` is unregistered for
   `WorkspaceOrchestrator`. It reproduces only under `DOTNET_ENVIRONMENT=Development`, not
   `ASPNETCORE_ENVIRONMENT`, because the CLI uses the generic Host builder.

4. `appsettings.Development.json` carries `postgres`/`postgres` while the compose container
   `daedalus_postgres` uses `daedalus`/`daedalus`.

## Environment note that will bite immediately

**A bare `dotnet run --project src/Daedalus.Api` now fails on `column s.Repository does not exist`.**
Phase 1.9 added an `AddScheduledRunRepository` migration, and a bare run does not apply migrations.
Either use the AppHost, which runs them, or apply it by hand first. This is not a defect; it is the
first phase to add a migration that an existing local database will not already have.

## What the first real digest cost

Two defects surfaced the moment the path was exercised for real. Both are fixed and merged.

**The Telegram channel had never worked, since phase 1.4.** A bot token is `<digits>:<secret>`, so
`bot{token}/{method}` parses as an **absolute** uri whose scheme is `bot<digits>` — a scheme may be
letters and digits followed by a colon. `HttpClient.PostAsync`'s string overload applies
`BaseAddress` only to a *relative* string, so the configured `api.telegram.org` was never consulted
and every call threw `The 'bot<digits>' scheme is not supported`. Fixed upstream in
[Thalos.NET#120](https://github.com/MarcelRoozekrans/Thalos.NET/pull/120), released as 0.5.1,
adopted here in #252.

**Why no test caught it:** every token fixture in the Thalos suite was colon-free — `"TOKEN"`,
`"T"`, `"cfg-token"`. Those build a genuinely *relative* uri, so `BaseAddress` applied and the tests
passed. The existing test asserted exactly the right property with a fixture incapable of exposing
the failure. **The shape of the fixture was the bug.**

**The scout's budget and page size had never been reconciled.** `MaxTotalTokens` was 50000 per
subagent run while `MaxItemsPerCategory` was 50, so the scout could pull five categories of fifty
items on top of the `roslyn__*`, `daedalus__*` and `memory__*` schemas it carries before fetching
anything. Fixed in #251: page size 15, budget 150000, `DeadlineSeconds` deliberately unchanged.

**A third gap was found before it could bite:** the AppHost forwarded only the Anthropic key and the
Telegram token, never `GITHUB_TOKEN`, so under Aspire the scout would have reported every category
unreadable. Fixed in #250.

### Operational notes from that run

- **Orphaned `dcp.exe` processes hold ports 17300, 18889 and 18890** after a killed AppHost, and a
  relaunch then fails with `Unable to allocate a network port` **after** printing its dashboard
  banner. Killing containers is not enough; kill `dcp.exe` too and verify the ports are free.
- **The Aspire Postgres is volume-backed**, so execution and outbox rows survive the container being
  recreated. A stale failed row will look exactly like a fresh failure — check `CreatedAt` before
  concluding anything.
- **Changing a schedule's cron does not recompute `NextRunAt`.** `UpdateFromConfig` sets `Cron` and
  the reconciler does not reschedule, so a cron change takes effect only after the schedule fires
  once on its old one. To fire on demand, set `NextRunAt` into the past directly.

## Known Narrowings — state these rather than paper over them

1. **"Cron wrong" is not representable.** An enabled schedule whose cron never fires reads
   `NotYetDue` or `Overdue`; the diagnostics page cannot say "your cron is wrong." So four and a half
   of the five documented death causes are covered, not five.

2. **A schedule that is both overdue and has a failed last run renders `Failed`**, with a past
   `Next run` beside it and no visual cue.

3. **A non-existent schedule id returns `200 OK` with an empty list**, so "no such schedule" and
   "schedule with zero runs" are indistinguishable to a caller. The agent tool's wording covers both
   honestly; the service-level fix needs a `Result`-shaped return and was deferred.

4. **Four ZeroAlloc packages still carry mis-stamped assemblies** — `Specification` 1.1.0, `Flux`
   1.1.1, `Saga` 2.0.0, `EventSourcing` 1.2.0. All are **latent, not broken**: breakage requires the
   assembly version to go *down* between releases, and these are uniformly wrong rather than
   downgrades. Their repos use multi-package release-please configs, so a `.github/` change released
   nothing; each picks up a correct stamp on its next real code change, with the fix already in place.

5. **`global.json` pins SDK `10.0.401` across the ZeroAlloc org** while runners may only have
   `10.0.400`. This is a race with GitHub's runner-image rollout and caused one publish failure that
   had to be rescued by hand. It will keep failing publishes intermittently until the pin is relaxed.

## Open Decisions (user)

1. **`AgentErrorCode` gaining a `None = 0` member.** `Validation` is member 0, so `default(AgentError)`
   is indistinguishable from a real validation failure. Renumbering is impossible — the enum is
   serialized. Phase 1.6 and 1.9 both took the opposite lesson deliberately: `RunVerdict` reserves
   `Unknown = 0` precisely because of this.
2. **The stranded-run reaper.** 1.6 made stranded runs visible, which was its precondition.
3. **Cross-origin schedule name collision** — still unreachable, since `schedule__create` was
   deliberately cut in 1.6 and not added in 1.9.
4. **Deprecate or unlist the confirmed-broken ZeroAlloc versions** — `Results` 1.2.1,
   `Collections` 1.1.4, `Validation` 1.3.0, `Rest` 1.3.0. They cannot be loaded and leaving them
   listed invites someone else into the same afternoon.
5. **Set an explicit `AssemblyVersion` policy** in the ZeroAlloc repos' `Directory.Build.props`.
   Several declare no version property at all, which is why their fallback was MSBuild's `1.0.0`. A
   deliberate `Major.0.0.0` would make an unversioned build harmless rather than hazardous.

## Recommended Next Step

**Phase 1.7 — Ralph retirement + ZeroAlloc migration** is next in the roadmap, and nothing now
blocks it.

**That gap is now closed.** A real digest reached a real chat on 2026-09-20, which is the first
end-to-end proof of everything built since 1.4. The remaining blockers are all secondary: CI does
not run the Playwright suites, the Cli has a DI gap in Ralph-era code that 1.7 retires, and the
development connection strings disagree with the compose container.

So 1.7 is genuinely next, with nothing blocking it.

## How This Phase Was Executed — worth carrying

Subagent-driven development: 8 tasks, fresh implementer each, independent review after each, plus a
final whole-branch review. **Twenty-two rulings** are preserved in
`docs/plans/2026-09-19-phase-1.9-rulings.md`.

**Six defects in the implementation plan were found downstream rather than by its author** — a test
snippet that would not compile, a single-argument constructor that weakened a production type, a
hand-built principal that left the security boundary unguarded, a truncation test that exercised
only the already-correct category, a fallback test that could not fail, and a target file containing
no logic at all. The pattern is consistent: the plan's test snippets pin something convenient to
write, and that convenience is exactly what stops the test catching what it exists to catch.

**The defect worth remembering came from the whole-branch review.** One task fixed truncation so it
measured the raw page rather than the filtered list, proven with tests that failed first. A later
task wrote a renderer that checked emptiness *before* truncation and discarded the flag, so a full
page filtering to zero printed "No merged pull requests" — a clean bill of health on a category it
had not finished reading. **Both tasks passed their own reviews.** The defect lived only in the seam
between them, which is exactly where a task-scoped diff review is blind by construction.

**A time bomb from phase 1.6 was also found and fixed here.** Two tests seeded rows at a fixed date
with a one-day lookback but never injected a clock, so they inherited the real one and had been
failing permanently since wall-clock time passed the fixture date. A red test on `main` corrupts the
baseline of every future phase.

## Environment

**Docker UP**, `daedalus_postgres` healthy, `traefik` holding 8080. Keycloak is started by the
Aspire AppHost, not standalone.

- **Use the AppHost.** It brings up Postgres, Keycloak and Ollama together, and applies migrations.
  A bare `dotnet run` on the Api needs `ConnectionStrings__daedalus`, has no Keycloak, and now also
  fails on the unapplied `Repository` column.
- Orphaned `dcp.exe` or dashboard processes from a killed AppHost run hold ports. Aspire reuses an
  existing Keycloak container, so a `keycloak-realm.json` change needs `docker rm -f daedalus-realm-*`.
- `PostgresFixture` builds the test schema with `EnsureCreatedAsync`, **not** `MigrateAsync`. Phase
  1.9 added `AddScheduledRunRepositoryMigrationTests` in `tests/Daedalus.Tests.Integration/Migrations/`,
  which does run `MigrateAsync` — follow that pattern for any future migration rather than verifying
  by hand.
- `dotnet` is a Windows process: give it Windows-style paths, never git-bash POSIX paths.
- Long test runs exceed a 2-minute default timeout. `Playwright.Browser` takes ~11 minutes and is
  excluded from CI; it has stalled implementers who waited on a notification mechanism that does not
  fire in this environment.
