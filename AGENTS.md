# AGENTS.md: Daedalus.NET

.NET 11 / C# 13. One presentation layer, `Daedalus.Api` with `Daedalus.Web`,
plus the `Daedalus.Cli` host, share `Daedalus.Application` and
`Daedalus.Infrastructure` over PostgreSQL and ZeroAlloc.ORM (it replaced EF Core in phase 3.5).
Work starts when a person presses Manufacture on a board task; the Ralph loop and `Daedalus.Console`
were retired in phase 2.8 (October 2026). Full pattern catalogue and extended examples:
`docs/development-guide.md`. `.github/copilot-instructions.md` still exists
but is superseded and may contain stale guidance — treat it as historical,
not authoritative.

## Product-line standards

This repository is part of a .NET product line. Read the [product-line standards](https://github.com/MarcelRoozekrans/product-line/blob/main/standards.md) before changing anything: they hold the rules for every product, including how to build (a failing test first, the cause before a fix, build and test output before calling work done) and what may appear in commits, pull requests and issues. Build sessions do not use superpowers. This file adds what is specific to this repository.

## Layout

```
src/
├── Daedalus.Domain/           # Entities, value objects
├── Daedalus.Application/      # Mediator requests/handlers, DTOs, repository interfaces
├── Daedalus.Infrastructure/   # ZeroAlloc.ORM maps and SQL classes, repository impls, SQL migrations
├── Daedalus.Api/              # REST minimal API endpoints (src/Daedalus.Api/Endpoints)
├── Daedalus.Agents/           # Thalos.NET composition root, manufacture workflow host
├── Daedalus.Cli/              # Console-channel host
├── Daedalus.Migrations/       # SQL migration runner
├── Daedalus.Sandbox/          # Per-run sandbox container host
├── Daedalus.ServiceDefaults/  # Shared OpenTelemetry, logging, DB registration
├── Daedalus.Web/              # Blazor WebAssembly UI
└── Daedalus.AppHost/          # .NET Aspire orchestration
tests/
├── Daedalus.Tests.Unit(.Domain|.Application|.Infrastructure)/
├── Daedalus.Tests.Integration/
└── Daedalus.Tests.Playwright.Api / .Playwright.Browser/
```

A board task starts work through `POST /api/tasks/{id}/manufacture`, handled by
`TaskManufactureService` (`Daedalus.Api/Services/TaskManufactureService.cs`). A
task's status is derived from its run on read (`TaskStatusDerivation`).

Not in the tree above: `tests/Daedalus.Tests.NativeApiHost` (the Native AOT API host that `native-publish.yml`
publishes and drives the Playwright.Api suite against), `benchmarks/Daedalus.Benchmarks`, and `eng/` (AOT warning
counter and its baseline, live-run and measure scripts). `BenchmarkDotNet.Artifacts` is a local output folder.

## Build and test

SDK: `global.json` pins `11.0.100` with `rollForward: disable`, so exactly that SDK builds the repo; 10.0.4xx
SDKs cannot. There is no other `global.json`. Runtimes: `Daedalus.Tests.Unit`, `.Integration`, `.Playwright.*` and
`.NativeApiHost` target `net11.0`. `Daedalus.Tests.Unit.Domain`, `.Application` and `.Infrastructure` multi-target
`net10.0;net11.0`, so they also need the .NET 10 runtime. `benchmarks/Daedalus.Benchmarks` targets `net10.0` only,
an exception to the repo-wide `net11.0` in `Directory.Build.props`; it needs the .NET 10 runtime.

```bash
dotnet run --project src/Daedalus.AppHost   # Aspire: Postgres, Keycloak, Ollama, migrations, sandbox image, API, Web; dashboard :17300
dotnet build --configuration Release        # a Debug solution build fails in NativeApiHost, #455
dotnet test --configuration Release
dotnet test tests/Daedalus.Tests.Unit
dotnet test tests/Daedalus.Tests.Integration
dotnet format                                # run before committing
```

Telemetry goes over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (Aspire sets it, dashboard :17300).
Sources: `thalos`, `thalos.workflow`, `ZeroAlloc.Mediator`, `daedalus.*`. Never tag content; see README,
Observability.

CI, in `.github/workflows/ci.yml`. Job `version` derives the version, job `build-and-test` runs the rest. The
formatting step runs after the build and before the tests.

```bash
# job "version"
dotnet tool restore
VERSION=$(dotnet dotnet-gitversion /output json | jq -r '.SemVer')
# job "build-and-test" (APP_VERSION is that version)
APP_VERSION=$VERSION
eng/aot/tests/test-count-warnings.sh
dotnet restore Daedalus.sln
dotnet build Daedalus.sln --configuration Release --no-restore -p:Version="$APP_VERSION"
dotnet format Daedalus.sln --verify-no-changes --no-restore
dotnet test Daedalus.sln --configuration Release --no-build --filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"
dotnet test Daedalus.sln --configuration Release --no-build --filter "FullyQualifiedName~Integration&FullyQualifiedName!~Playwright&Category!=AuthenticationFlow"
```

CI adds coverage collection and report flags to both test steps; the commands above are the same runs without them.
The integration tests bring their own Testcontainers, so they need a working Docker. The coverage gate
(`MarcelRoozekrans/ci-baseline/actions/coverage-gate`, floor 75 percent over the merged Cobertura files of both test
steps) is a CI action; the sequence above does not reproduce it. The 75 percent floor is an exception to the org
rule of 80 percent: hand-written code measures 75.9 percent, and raising it is tracked in Daedalus.NET#438. Job `aot-analysis` runs
`eng/aot/count-warnings.sh --target $TARGET --check` for Api, Cli, Migrations, Sandbox and Web: see the Native AOT rule
below before running it locally.

Inner loop, verified locally with SDK 11.0.100: build the solution in `Release` as CI does, because a Debug solution
build fails in `Daedalus.Tests.NativeApiHost` on a scoped-css bundle error, tracked in #455. Then run one
test project.

```bash
dotnet build Daedalus.sln --configuration Release -p:Version=9.9.9-local
dotnet test tests/Daedalus.Tests.Unit --configuration Release --no-build
```

`Daedalus.Tests.Unit` holds a few `GeneratedEndpointBindingTests` that fail with an inotify instance limit error on a
shared machine that has many watchers open; that is the host limit, not the code.

## Conventions and gotchas

### Native AOT publishes: CI only on the agents VM

Agent tasks run on the shared agents VM: 24 GB of RAM for about nine agent sessions. A Native AOT
publish of `Daedalus.Api` or another full program runs ILC at 10–16 GB resident; CI measured about 12 GB
for the Api. On 2026-10-22 local publishes from a worktree were OOM-killed three times in one morning,
at 08:00, 08:35 and 09:03, and filled swap. An OOM kill on a shared VM can hit another session's process
just as well.

- Do not run a Native AOT publish (`dotnet publish -r linux-x64` of a `PublishAot` project, or the
  `eng/aot` warning count) on the agents VM. Push and let the `aot-analysis` CI job publish and count;
  read its log for the IL2xxx/IL3xxx numbers.
- `dotnet build`, `dotnet test` and non-AOT publishes are fine, one at a time.
- If a local AOT check is unavoidable: one target, never in parallel with another build, only when
  `free -g` shows at least 16 GB available, and stop after one OOM instead of retrying.

### Pattern library: ZeroAlloc.Results (not CSharpFunctionalExtensions)

`using ZeroAlloc.Results.Extensions;` for combinators.

- No inferred `Result.Success(x)` — qualify the generic type:
  `Result<T>.Success(value)`, `Result<T>.Failure(error)`.
- `Bind`, `Map`, `Match`, `Combine`, `Tap`, `TapError`, `MapError` are
  **extension methods**, not instance members — the `using` above is
  required or they won't resolve.
- `Ensure` only exists for `Result<T,E>`, not plain `Result<T>` — write the
  check explicitly instead.
- `Result<T>` has implicit conversions from both `T` and `string`. When `T`
  is `string`, a bare `return expr;` is ambiguous — always use the explicit
  `Result<string>.Success/Failure` form there.

```csharp
using ZeroAlloc.Results.Extensions;

public async Task<Result<Customer>> GetCustomerAsync(Guid id, CancellationToken ct)
{
    var customer = await customers.FindAsync(id, ct);
    return customer is not null
        ? Result<Customer>.Success(customer)
        : Result<Customer>.Failure($"Customer with ID {id} not found");
}
```

### ZeroAlloc.Mediator

- Every command/query is a `readonly record struct` implementing
  `IRequest<T>` — a reference-type `record` fails the build with `ZAM003`.
- The generated `IMediator` and `AddMediator()` are `internal` to
  `Daedalus.Application`. Never put `IMediator` in a public signature or
  reference it from `Program.cs` or `ApiApplication.cs`. API endpoints depend on the public facade
  `IApplicationCommands` instead (`Application/Abstractions/IApplicationCommands.cs`).

### Validation

Validators are registered explicitly in two places (`ApplicationServiceExtensions`
for `ValidatorFor<T>`, `Daedalus.Api/ApiApplication.cs` for `IValidationAdapter`) —
`ZeroAlloc.Validation.Inject`'s auto-discovery does not see `record`-based
`[Validate]` DTOs. A `[Validate]` DTO with no matching `IValidationAdapter`
registration is **silently never validated**. If you add one, add the other
in the same change, or `Daedalus.Tests.Integration.Architecture.CleanArchitectureTests`
fails.

The adapters feed `ZeroAllocValidationEndpointFilter`, which `MapApiGroup` adds to
every endpoint group (`Daedalus.Api/Endpoints`). Issue #436 tracks that the
Browser E2E fixture registers no adapters.

### API endpoints

Endpoints are static `<Name>Endpoints` classes mapped with `MapApiGroup`, never a
bare `MapGroup`. Bodies bind as `ApiBody<T>` and queries as `ApiQuery<T>`, failures
answer through `ApiProblem` or `ApiJson.Of(new ErrorBody(...), status)`, and the API
is unversioned. The pattern: `docs/development-guide.md`, "API endpoints".

### Not in this codebase

- No `Polly` package dependency. Resilient HTTP clients use
  `Microsoft.Extensions.Http.Resilience`'s `AddStandardResilienceHandler(...)`
  (see `Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs`).
- No `ZeroAlloc.Specification` package and no `Specification<T>` pattern.
- No generic `IRepository<T>` — repositories are one interface per aggregate
  (`ITaskRepository`, `IProjectRepository`, ...) in `Application/Abstractions/`.
- No `CSharpFunctionalExtensions` (replaced by `ZeroAlloc.Results` in
  phase 1.7).

### Genuinely widespread conventions

- `[LoggerMessage]` source-generated logging (61 files) — prefer it over
  `ILogger.LogXxx()` in anything performance-sensitive.
- Primary constructors for DI everywhere; never mix one with a second,
  traditional constructor on the same class.
- `ZLinq`'s `AsValueEnumerable()` for hot-path LINQ — minor, not the default;
  no file under `src/` uses it since phase 2.8, only the benchmarks. Standard
  LINQ is fine elsewhere.
- Clean Architecture layering (Domain has no dependency on Application/
  Infrastructure/Api) and the other architecture rules live in the Ariadne
  pack `.architecture/rules.yaml`, checked by
  `tests/Daedalus.Tests.Unit/Architecture/AriadneArchitectureTests.cs`
  (`Ariadne.NET.Testing`). Facts the pack cannot hold are in
  `RetainedArchitectureTests.cs` next to it and in the Integration project's
  `Architecture/CleanArchitectureTests.cs`.

### Data layer: ZeroAlloc.ORM, no EF Core

Daedalus' own tables are read and written through ZeroAlloc.ORM over one `NpgsqlDataSource`. There is no
`DbContext`, no change tracking, no model snapshot and no `dotnet ef`.

- Per aggregate, a folder in `src/Daedalus.Infrastructure/Persistence/Orm/<Aggregate>/` holds the `[TableMap]` class,
  the row records and a `<Aggregate>Sql` class. The `...Sql` class takes an open connection and exposes the
  generated queries. Inserts are raw `[Command]`s that list every column.
- A store opens a connection with `DaedalusDb.OpenAsync` (registered by `AddApplicationDatabase`), builds the
  `...Sql` class over it and disposes the connection.
- Domain stays free of ORM attributes. Rows become entities through each entity's public `Rehydrate` factory,
  documented "for persistence only"; `Create` keeps the invariants.
- A write that stores values computed from the row it read is guarded by `xmin` (Task, Project, BrainstormSession,
  CodeAnalysisRequest, ScheduledRun, ScheduledRunExecution): it throws `ZeroAllocOrmConcurrencyException` when the
  row changed since the read, and the repository turns that into its failure `Result`. Writes of independent
  columns and atomic in-database updates are not guarded.
- Schema changes are numbered SQL files, `Persistence/Migrations/NNNN_<name>.sql`, applied by
  `DaedalusMigrations.CreateRunner`. Never edit a shipped file: add the next number. See
  `skills/daedalus-migrations/SKILL.md` and `docs/development-guide.md`, "Data layer".

### Run sandboxes

`Thalos:Workflow:Sandbox:Enabled` picks the mode. Sandbox (the API default): each run's code builds and tests in its own
Docker container, and publish applies the exported patch into a clean worktree. Local (`Daedalus.Cli`, dev hosts without
Docker): host worktree, `.cs`/`.md` only. S6 boot check: a write grant with no `AllowedExtensions` needs the sandbox
enabled; an explicit empty list is refused. Publish refuses `.github/` and the other protected paths, plus
`.gitattributes`, `.gitmodules`, symlinks and submodule pointers. Details: `docs/development-guide.md`, "Run sandboxes".

### Central package management

`Directory.Packages.props` owns every version. `PackageReference` entries in
`.csproj` files carry **no `Version` attribute** — add/bump versions only in
`Directory.Packages.props`.

### Git commit messages: no nested parentheses

release-please's conventional-commit parser throws on a nested `(` inside a
parenthesized group and **silently drops the whole commit** from the
changelog — the workflow still reports success. Write `typeof IFoo`, not
`(typeof(IFoo))`; use a fenced code block or restructure the sentence instead
of nesting parens in a commit body. Square brackets are fine.

### Tests that pass for the wrong reason

The recurring defect in this codebase's history. Phase 1.7 alone found eight
instances, including a suite that passed only because the system under test
was broken, and two guards that could not fail by construction. Check
falsifiability **per assertion, not per test**: for every assertion, be able
to name the code change that would turn it red, and verify red/green by
actually making that change. Details:
`.superpowers/sdd/2026-09-20-phase-1.7-zeroalloc-migration-plan/progress.md`.

### Forbidden

No `Task.Wait()`/`.Result`/`.GetAwaiter().GetResult()`. No `async void`
outside event handlers. No throwing for expected/flow-control failures — use
`Result<T>`. No `Task.Run` to fake async over a sync API. No storing
`HttpContext` in a field. See `docs/development-guide.md` for the full list
with examples.

<!-- org-core:start sha256=08a88996bc9165f888fc0260607cfd0670cae128c323e16707c6b452ee66222d (generated from docker-nuc scripts/agents-md/org-core-personal.md; do not edit here) -->
## Org rules

These rules hold in every public MarcelRoozekrans repo.

### Commits and releases

- Conventional Commits. release-please reads them on the default branch, and the changelog sections
  in `release-please-config.json` decide which types count. The standard: `feat`, `fix`, `perf`,
  `deps` and `revert` open or update the release PR; `docs`, `test`, `refactor`, `build`, `ci`,
  `chore` and `style` do not, unless the commit is breaking or has a `Release-As:` footer. The
  release happens when that PR merges. A breaking change needs `!` or a `BREAKING CHANGE:` footer.
- A commit body never contains nested parentheses. release-please cannot parse them and silently
  drops the whole commit from the changelog, while the workflow still reports success.
- After merging a commit that should release, check that it appears in the release-please PR.

### Branches and pull requests

- Every change goes through a branch and a pull request. Never push to the default branch directly.
- A PR body says what the diff does, and nothing the diff does not do.
- Fix review findings in the PR under review, including small ones. File an issue only for a finding
  that belongs to other code, needs a design decision, is its own piece of work, or waits on
  something external.

### CI

- The .NET repos call the shared workflows of `MarcelRoozekrans/ci-baseline`, pinned to `@v1`, never
  `@main`.
- Its coverage gate fails the build when line coverage drops below the floor of 80%, and Codecov
  fails a PR that lowers project coverage by more than 1 percentage point. New code comes with
  tests.
- A repo with a lower floor documents the reason and links the issue that raises it.
<!-- org-core:end -->
