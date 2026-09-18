# Plan B baseline: test suite state before phase 1.5 work begins

**Date measured:** 2026-09-17
**Plan:** `docs/plans/2026-09-17-scheduled-runs-plan-b-continuation.md`, Task 2
**Purpose:** record, per test project, what passes/fails/does-not-run *right now*, before any
phase 1.5 code lands. Phase 1.4's retrospective names the absence of this record as what let a
126-test Playwright failure hide for an entire plan. This document exists so that any failure
found at review time can be checked against this baseline and proven pre-existing rather than
attributed to phase 1.5.

## Environment

- Docker: **up**, version 29.5.2. Testcontainers-backed suites (`Daedalus.Tests.Integration`,
  `Daedalus.Tests.Playwright.Api`) ran for real, not skipped.
- `docker ps --filter publish=8080` at measurement time:

  ```
  CONTAINER ID   IMAGE          COMMAND                  CREATED        STATUS         PORTS
  08ab666afe74   traefik:v3.3   "/entrypoint.sh --lo…"   5 months ago   Up 6 minutes   0.0.0.0:8080->80/tcp, [::]:8080->80/tcp, 0.0.0.0:8443->443/tcp, [::]:8443->443/tcp
  ```

  A `traefik` container **is** holding port 8080 (and 8443) on this machine. Per the brief, the
  `AuthenticationFlowTests` failures below are expected as a result.
- .NET SDK targets `net10.0` across the solution.
- Each project below was built and run with a separate `dotnet test <csproj>` invocation — never
  a single root `dotnet test` — so a failure can be attributed to its owning project.

## Every test project in the solution

`dotnet sln list` lists 7 projects whose path contains `tests` (case-insensitive), matching the
brief's expectation of seven:

1. `tests\Daedalus.Tests.Integration\Daedalus.Tests.Integration.csproj`
2. `tests\Daedalus.Tests.Playwright.Api\Daedalus.Tests.Playwright.Api.csproj`
3. `tests\Daedalus.Tests.Playwright.Browser\Daedalus.Tests.Playwright.Browser.csproj`
4. `tests\Daedalus.Tests.Unit.Application\Daedalus.Tests.Unit.Application.csproj`
5. `tests\Daedalus.Tests.Unit.Domain\Daedalus.Tests.Unit.Domain.csproj`
6. `tests\Daedalus.Tests.Unit.Infrastructure\Daedalus.Tests.Unit.Infrastructure.csproj`
7. `tests\Daedalus.Tests.Unit\Daedalus.Tests.Unit.csproj`

All seven built and ran (none failed to build, none were skipped, none were "did not run").

## Results per project

| Project | Result | Passed | Failed | Skipped | Total | Duration |
|---|---|---|---|---|---|---|
| `Daedalus.Tests.Unit.Domain` | ran | 283 | 0 | 0 | 283 | 509 ms |
| `Daedalus.Tests.Unit.Application` | ran | 396 | 0 | 0 | 396 | 4 s |
| `Daedalus.Tests.Unit.Infrastructure` | ran | 130 | 0 | 0 | 130 | 17 s |
| `Daedalus.Tests.Unit` | ran | 128 | 0 | 0 | 128 | 14 s |
| `Daedalus.Tests.Integration` | ran, **known failures** | 380 | **9** | 0 | 389 | 1 m 39 s |
| `Daedalus.Tests.Playwright.Api` | ran, **known failures** | 0 | **126** | 0 | 126 | 15 s |
| `Daedalus.Tests.Playwright.Browser` | ran | 99 | 0 | 0 | 99 | 7 m 42 s |
| **Total** | | **1,416** | **135** | **0** | **1,551** | |

Every project actually executed its tests — "ran" here means the test host started and reported
a pass/fail/skip count, not that all tests within it passed. The two rows marked **known
failures** are addressed explicitly below; those failures are not attributed to phase 1.5.

No project fell into "did not run" (build failure, host crash before reporting, or explicit
skip). If a future baseline run has such a case, it must be its own row, never folded into a
passed or failed count.

## Known-failing suite 1: `Daedalus.Tests.Playwright.Api` — 126/126 failing

**Observed: 126 failed, 0 passed, 0 skipped, 126 total.** This matches the brief's predicted
126/126 exactly.

Root cause, confirmed from this run's stack trace: `E2EServerFixture.GlobalSetupAsync()` (line
178→200 in `tests/Daedalus.Tests.Playwright.Api/Fixtures/E2EServerFixture.cs`) touches
`WebApplicationFactory.Services`, which starts the ASP.NET Core host — including Thalos's
`SkillSyncService.StartingAsync`, which calls `PostgresSkillStore.ListAsync` against the
`Skills` table — **before** the fixture's own `EnsureCreatedAsync()` has created the EF Core
schema. Every test failed with the same underlying error:

```
Npgsql.PostgresException (0x80004005): 42P01: relation "Skills" does not exist
```

Pre-existing since phase 1.3, per the brief. `.github/workflows/ci.yml` confirms this suite is
invisible in CI: both the "Run Unit Tests" step (`--filter "FullyQualifiedName!~Playwright&..."`)
and the "Run Integration Tests" step (`--filter "...FullyQualifiedName!~Playwright..."`)
explicitly exclude anything with `Playwright` in its fully-qualified name.

**This is not phase 1.5's to fix.** Any review comparing against this baseline should expect
this suite to still show 126/126 failing with the same `relation "Skills" does not exist` error.
A different count, or a different error, is a real regression signal.

## Known-failing suite 2: `AuthenticationFlowTests` — 9/9 failing (environmental)

**Observed: 9 failed** inside `Daedalus.Tests.Integration` (in
`tests/Daedalus.Tests.Integration/Authentication/AuthenticationFlowTests.cs`), out of that
project's 389 total (380 passed elsewhere in the same project). This matches the brief's
predicted count of 9.

Cause: these tests drive a running API through `localhost:8080`/`localhost:443` behind a
`traefik` + Keycloak stack that is expected to be already running on the developer's machine —
they do not bring up their own Testcontainers, unlike every other integration test in the
project. `docker ps --filter publish=8080` (above) confirms a `traefik` container is currently
holding port 8080 on this machine, but the failures are `Connection refused` on `localhost:443`
— the stack the tests expect (Keycloak reachable through that reverse proxy) is not actually up,
only the `traefik` container itself is. Sample error from this run:

```
System.Net.Http.HttpRequestException: Kan geen verbinding maken omdat de doelcomputer de
verbinding actief heeft geweigerd. (localhost:443)
  ---- System.Net.Sockets.SocketException: Connection refused
  at Daedalus.Tests.Integration.Authentication.AuthenticationFlowTests.ApiEndpoint_WithValidToken_Returns200Ok()
```

`.github/workflows/ci.yml` confirms this is a known, accepted gap: the "Run Integration Tests"
step filters `Category!=AuthenticationFlow` and carries this comment directly above it:

> `Category=AuthenticationFlow` tests drive a *running* API at localhost:8080 through a Keycloak
> container — an end-to-end check for a developer machine with the stack up, not something a
> test runner can satisfy; every other integration test brings its own Testcontainers.

**This is environmental, not phase 1.5's to fix.** Any review comparing against this baseline
should expect this suite to still show 9 failing for the same connection-refused reason, on a
machine in the same state (traefik up, full Keycloak stack not proxied through it).

## Self-review

- All 7 test projects in the solution are listed and were run individually — confirmed against
  `dotnet sln list`, filtered for `tests`, case-insensitive.
- Every number in the tables above is copied from an actual `dotnet test` run performed for this
  document, not estimated or copied from the brief. `Daedalus.Tests.Playwright.Api` (126/126)
  and the `AuthenticationFlowTests` count (9) happen to match the brief's predictions exactly —
  they were not adjusted to match; the raw run output is quoted above.
- "Did not run" is called out explicitly as a category (see the paragraph after the results
  table) and none of the 7 projects landed in it on this run. Every row in the table represents a
  suite that actually executed and reported counts.
- `Daedalus.Tests.Playwright.Browser` is not called out as a known failure — it passed 99/99 on
  this run. It is excluded from CI by the same `!~Playwright` filter as `Playwright.Api`, but
  unlike `Playwright.Api` it has no pre-existing defect observed here.
