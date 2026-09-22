# Session State

**Last session:** 2026-09-21
**Milestone 1 — Hermes-Style Agent Framework: CLOSED (2026-09-21).** All 9 phases complete. See
`docs/planning/MILESTONE.md` for the definition-of-done checklist and its honest scoping, and
`docs/planning/ROADMAP.md` for the "Carried forward from Milestone 1" list of 7 known, deliberately
unfixed items.

**Current milestone:** 2 — Software Manufacturing (**phase 2.1 complete; phase 2.2 designed 2026-09-22, ready to plan**)

**Phase 2.1 — git write tooling: complete (2026-09-21).** Branch `feat/phase-2.1-git-tooling`, 6
commits, PR opened against `main`. The roadmap described this phase as building branch, commit, push
and pull-request tools from nothing; most of that already existed. What actually happened: the
capability split across the reusable-framework line recorded on 2026-09-21 — generic git write
actions (`Thalos.Git.GitActionTools`: `git__create_branch`, `git__commit`, `git__push`,
`git__open_pull_request`) shipped in **Thalos.NET 0.6.0**; hosting glue stayed in Daedalus
(`ThalosPullRequestPublisher` implementing Thalos's `IPullRequestPublisher` over the existing
dispatcher, `git` local-tool-source registration, `developer` policy binding in both
`Daedalus.Api/appsettings.json` and `Daedalus.Cli/appsettings.json`). The other real work was
consolidating Daedalus's own GitHub client onto one `GitHubApi` in
`Daedalus.Infrastructure.Services.GitHub`, removing a circular-reference hazard between Agents and
Infrastructure along the way.

**Suites, all green, no pre-existing failure changed:** Domain 338/338, Unit 212/212, Application
409/409, Infrastructure 133/133 — unit total 1092/1092 — Integration 508/508 on the first run (no
crash this time), Playwright.Api 126/126. `Playwright.Browser` was not run — about 17 minutes and
this branch does not touch its fixtures, per standing guidance.

**Phase goal confirmed live, not asserted:** all four `git__*` tools enumerated straight from the
shipped `Thalos.NET.Git` 0.6.0 XML doc comments; both `Daedalus.Api` and `Daedalus.Cli`
`appsettings.json` bind `{ "Pattern": "git__*", "Policy": "developer" }`; and
`RepoToolBoundaryTests` run against the real composed `Daedalus.Api` host (9/9 passing) proves
`git__*` is exposed under its own tool source, every `git__*` tool resolves to the `developer`
policy, and the configured detached-run principal (`DetachedRuns:Roles = ["reader"]`) fails that
policy — a scheduled run cannot branch, commit, push, or open a pull request no matter what its
agent definition lists.

**Not proved end to end against a live remote, by design.** Creating a branch, pushing, and opening
a real pull request is outward-facing and irreversible — it needs a human decision about which
repository and what artefacts are acceptable to leave there. Phase 1.9 proved its *read* path
against live GitHub safely, because reading leaves nothing behind. The write proof is ready and
waiting on that decision.

**Carried forward from phase 2.1:**
1. `IRepositoryAuthenticationProvider` was never registered anywhere on `main`, so Azure DevOps
   pull-request creation could never have worked, on any host, ever. It hid behind resolution
   order — `PullRequestFactory` resolves `GitHubPullRequestFactory` first, so it always failed on
   the GitHub parameter before reaching Azure DevOps. Fixed in this phase's task 5.
2. `Daedalus.Console`'s `RalphLoopWorker` does not call the Agents composition root
   (`AddDaedalusAgents`), so agent-registered services — including all `git__*` tools — are absent
   on that host. Console only calls `AddDaedalusMemory`.
3. The Integration suite test-host-crashed twice during this phase under Docker resource
   contention, with zero failures reported both times. A third and this task's own run were clean.
   Resource contention on this machine, not code.
4. Thalos's `scripts/pack-local.ps1` hard-codes `0.3.0-<suffix>` and never calls GitVersion, so
   local dev feeds carry the wrong version. Real releases are unaffected — GitVersion wins in CI.

**Next: phase 2.2 — the durable workflow engine. Designed 2026-09-22; ready to plan.**
Design: `docs/plans/2026-09-22-phase-2.2-workflow-engine-design.md`.

A spike measured the roadmap's named substrate and **all three candidates failed**:
`ZeroAlloc.StateMachine`'s runtime assembly is seven attribute types and zero non-attribute types,
so a process shape can never come from data; `ZeroAlloc.EventSourcing` has no step or branch
vocabulary at all; `ZeroAlloc.Saga` enforces a contiguous linear step order and supports neither
loops nor branching. The Saga ban was lifted on defect grounds and the exclusion now stands on
architectural grounds instead. `RunStep` was cited as precedent but advances strictly forward, so
it is precedent for the linear shape, not for branching.

The engine is therefore written here, as `Thalos.NET.Workflow` plus `Thalos.NET.Workflow.Orm`, on
**`ZeroAlloc.ORM` and `ZeroAlloc.Outbox.Orm` rather than EF Core** — which makes 2.2 the pilot for
Milestone 3's data-layer migration and keeps the engine EF-free so it can live in Thalos at all.
Placement settled: Thalos owns the graph, gates and abstractions; Daedalus owns process files,
hosting and the resume endpoint's policy binding.

**The security property of the phase:** an agent must not be able to resume its own approval gate.
Resume is bound to `developer` and is deliberately not registered as an agent tool.

**Parked ideas:** `docs/planning/parked-ideas.md` — currently one, a customer chatbot product on Rag.NET, deferred as a separate application rather than a Daedalus milestone. The 1.0 tag on Thalos.NET is
deliberately held back until Milestone 2 settles the agent contracts, since 2.2's workflow engine and
2.3's squad roster will likely want changes to `ISubagentRunner`. Decision recorded 2026-09-21 on
ROADMAP 1.8 and issue #233.

**Phase 1.8 — closed 2026-09-21, docs and architecture-diagrams rewrite, no release.** The plan for
this phase called for shipping Thalos.NET 0.6.0; that call was wrong and is cancelled. Thalos.NET
releases via release-please from conventional commits, and since 0.5.1 there are 18 commits — 17
`chore`, 1 `docs`, zero `feat`, zero `fix` — none releasable without forcing a `Release-As` override.
Confirmed with the user: no override, because nothing in the code changed and a minor version would
signal features that do not exist, undercutting the version-as-signal reasoning that deferred 1.0 in
this same phase. `docs/architecture-diagrams.md` was restructured around the system that exists — 21
sections, 26 mermaid blocks, all verified to render — with fabricated content removed (wrong
`IGitRepositoryManager`/`IRepositoryCodeExtractor` signatures, OpenAI/Copilot named as the LLM backend
where it is Anthropic Claude via Thalos.NET, non-persisted ER entities, symbols that do not exist).
Thalos.NET's 11 packages are documented in that repo's README via
[Thalos.NET#138](https://github.com/MarcelRoozekrans/Thalos.NET/pull/138); the docs are on Thalos.NET
`main` and reach the NuGet package page whenever the next real change ships.

**Phase 1.7 — Daedalus ZeroAlloc migration. Complete and merged** via [#257](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/257).

`CSharpFunctionalExtensions` and both `FluentValidation` packages are gone from the solution and guarded
by architecture tests; the hand-rolled CQRS layer is now `ZeroAlloc.Mediator` 5.1.1. All suites green:
unit 1078, Integration 505, Playwright.Api 126, Playwright.Browser 99.

Three test suites were also repaired along the way, in [#255](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/255)
and [#256](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/256). `Playwright.Api` had been providing
**zero coverage** — 0 of 126 passing, dying at fixture setup in 11 seconds — and now runs 126/126 in about
25 seconds. Nobody had noticed because CI excludes both Playwright projects and the Keycloak tests by name.
**That CI exclusion is still in place** — carried forward, not fixed; see ROADMAP's carried-forward list.

[#258](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/258) then lifted the `ZeroAlloc.Saga` ban and
re-justified `ZeroAlloc.Scheduling`'s, after re-measuring the upstream fixes rather than trusting issue
status. Saga was briefly a candidate for phase 2.2's workflow engine; the 2026-09-22 spike ruled it out on
architecture rather than defects. Milestone 3's premise is half retired because `Saga.Orm` and
`Outbox.Orm` ship with zero EF Core — and 2.2 now pilots that ORM path on greenfield tables.
**Branch state:** `docs/phase-1.8-design`, ahead of `main`. Clean tree apart from the two
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

**Superseded 2026-09-21 — Milestone 1 is closed; phases 1.7, 1.8 and 1.9 are all complete.** The
paragraphs below describing 1.7 as next are historical and left in place rather than deleted; they
record why 1.7 was unblocked at the time. See the top of this file for the current pointer.

**Superseded 2026-09-21 (later the same day) — phase 2.1 is also complete.** The paragraph below
describing it as next is historical and left in place for the same reason. See the top of this file
for the current pointer, now phase 2.2.

**Phase 2.1 — git write tooling** is next, opening Milestone 2. It is not blocked: phase 1.9 already
built the authorization boundary (`repoaction__*` bound to the `developer` policy, denied to a
scheduled run regardless of its tool list) that 2.1 extends to git writes.

---

*(Historical, from phase 1.7's close)* **Phase 1.7 — Ralph retirement + ZeroAlloc migration** is next in the roadmap, and nothing now
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
