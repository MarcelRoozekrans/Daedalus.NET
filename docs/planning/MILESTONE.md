# Milestone 3: Native AOT

**Status:** active
**Started:** 2026-10-17

> Brainstormed with the owner on 2026-10-17. Design: `docs/superpowers/specs/2026-10-17-milestone-3-design.md`.
> Evidence: `docs/plans/2026-10-17-native-aot-survey.md`. Milestone 2's file is archived at
> `docs/planning/archive/MILESTONE-2.md`.

## Goal

Daedalus and Thalos.NET ship as Native AOT. The API, the CLI, the migration runner and the sandbox host publish as
native binaries, the Web app publishes with WebAssembly AOT, and every Thalos.NET package is AOT-compatible.

**Why:** EF Core has no roadmap to Native AOT. The data layer has to leave EF Core for Daedalus to ever ship AOT, and
every month on it adds code that must move later, so this milestone moves it deliberately. The schema and the data
stay exactly as they are.

## Definition of Done

- [ ] **All planned phases complete.** Phases 3.1 to 3.7 are merged.
- [ ] **All tests passing.** Unit, Unit.Domain, Unit.Application, Unit.Infrastructure, Integration, Playwright.Api
      and Playwright.Browser all pass. Playwright.Api also runs against the native API binary, and Playwright.Browser
      against the WebAssembly AOT Web build.
- [ ] **Native targets publish clean.**
  - `Daedalus.Api`, `Daedalus.Cli`, `Daedalus.Migrations` and `Daedalus.Sandbox` publish with `PublishAot=true`, in
    CI, with zero trim or AOT warnings and no suppressions.
  - Each native binary starts and does its job: the CLI answers a turn, Migrations upgrades a database, the sandbox
    host serves a run, and the API passes the API suite.
- [ ] **Thalos.NET is AOT-compatible.**
  - Every packable Thalos project sets `IsAotCompatible=true` and builds with zero trim or AOT warnings, on both
    target frameworks. `Thalos.NET.Testing` is exempt.
  - It is released as a Thalos version Daedalus consumes.
- [ ] **The Web app ships WebAssembly AOT.** `Daedalus.Web` publishes with `RunAOTCompilation=true`, with zero trim
      warnings. The browser suite passes against that build.
- [ ] **EF Core is gone.**
  - `Microsoft.EntityFrameworkCore*` and `Npgsql.EntityFrameworkCore.PostgreSQL` appear nowhere in the dependency tree.
  - An existing database upgrades in place with its data intact, proven on a copy of the dev database.
- [ ] **The HTTP contract is unchanged.** The 18 controllers became minimal API endpoints. Playwright.Api and the Web
      app pass without changes to what they assert.
- [ ] **A live manufacture run succeeds on the native API.** It goes from a board task to a pull request on
      `daedalus-sandbox`, with its spend on `/costs`.
- [ ] **The gains are measured.** Start-up time, working set and image size are recorded for the API, the CLI and the
      sandbox host, before (JIT, in 3.1) and after (native, in 3.6).
- [ ] **Documentation complete.**
  - Every phase has a design, a plan and a rulings record.
  - The README and the development guide describe the native build, its toolchain and the AOT rules for
    contributors.

## Phases

| # | Phase | Surface | Status |
|---|---|---|---|
| 3.1 | AOT toolchain, gates, baseline and ORM gap audit; the native sandbox host | Infra | pending |
| 3.2 | Our own reflection out: STJ source-gen, config binding, DI annotations, tool registry, YAML static generator | Refactor | pending |
| 3.3 | Thalos' own Anthropic and Ollama clients | Backend | pending |
| 3.4 | Controllers to minimal APIs, identical contract | Refactor | pending |
| 3.5 | EF Core to ZeroAlloc.ORM, schema and data kept | Data | pending |
| 3.6 | Native API, CLI and Migrations; native test runs, live proof, measurements | Infra | pending |
| 3.7 | Blazor WebAssembly AOT | Infra | pending |

Order: 3.1 comes first, then 3.2. 3.3, 3.4 and 3.5 are independent of each other. 3.6 needs 3.2 to 3.5. 3.7 needs only
3.1 and 3.2.

## Policies

- **Blocking dependencies:**
  - A library we own is fixed upstream.
  - A third-party library gets an upstream issue, and is replaced if an AOT-clean alternative exists.
  - If neither works, the milestone waits, tracked.
  - No suppression stands in for a fix.
- **Zero warnings means no suppressions.** A suppression is allowed only where the code is provably safe, with the
  reason beside it, and is listed in its phase's rulings.
- **Warning-free is not working.** Every native target is proven by running it.
- **No data loss, no contract change** in any phase.

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
