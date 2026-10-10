# Daedalus

.NET 10 / C# 13. One presentation layer, `Daedalus.Api` with `Daedalus.Web`,
plus the `Daedalus.Cli` host, share `Daedalus.Application` and
`Daedalus.Infrastructure` over PostgreSQL/EF Core. Work starts when a person
presses Manufacture on a board task; the Ralph loop and `Daedalus.Console`
were retired in phase 2.8 (October 2026). Full pattern catalogue and extended examples:
`docs/development-guide.md`. `.github/copilot-instructions.md` still exists
but is superseded and may contain stale guidance — treat it as historical,
not authoritative.

## Project structure

```
src/
├── Daedalus.Domain/           # Entities, value objects
├── Daedalus.Application/      # Mediator requests/handlers, DTOs, repository interfaces
├── Daedalus.Infrastructure/   # EF Core DbContext, repository impls, migrations
├── Daedalus.Api/              # REST controllers
├── Daedalus.Cli/              # Console-channel host
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

## Build and test

```bash
dotnet run --project src/Daedalus.AppHost   # Aspire: Postgres + migrations + API + Web, dashboard on :17300
dotnet build
dotnet test
dotnet test tests/Daedalus.Tests.Unit
dotnet test tests/Daedalus.Tests.Integration
dotnet format                                # run before committing
```

## Pattern library: ZeroAlloc.Results (not CSharpFunctionalExtensions)

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
    var customer = await dbContext.Customers.AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);
    return customer is not null
        ? Result<Customer>.Success(customer)
        : Result<Customer>.Failure($"Customer with ID {id} not found");
}
```

## ZeroAlloc.Mediator

- Every command/query is a `readonly record struct` implementing
  `IRequest<T>` — a reference-type `record` fails the build with `ZAM003`.
- The generated `IMediator` and `AddMediator()` are `internal` to
  `Daedalus.Application`. Never put `IMediator` in a public signature or
  reference it from `Program.cs`. Controllers depend on the public facade
  `IApplicationCommands` instead (`Application/Abstractions/IApplicationCommands.cs`).

## Validation

Validators are registered explicitly in two places (`ApplicationServiceExtensions`
for `ValidatorFor<T>`, `Daedalus.Api/Program.cs` for `IValidationAdapter`) —
`ZeroAlloc.Validation.Inject`'s auto-discovery does not see `record`-based
`[Validate]` DTOs. A `[Validate]` DTO with no matching `IValidationAdapter`
registration is **silently never validated**. If you add one, add the other
in the same change, or `Daedalus.Tests.Integration.Architecture.CleanArchitectureTests`
fails.

## Not in this codebase

- No `Polly` package dependency. Resilient HTTP clients use
  `Microsoft.Extensions.Http.Resilience`'s `AddStandardResilienceHandler(...)`
  (see `Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs`).
- No `ZeroAlloc.Specification` package and no `Specification<T>` pattern.
- No generic `IRepository<T>` — repositories are one interface per aggregate
  (`ITaskRepository`, `IProjectRepository`, ...) in `Application/Abstractions/`.
- No `CSharpFunctionalExtensions` (replaced by `ZeroAlloc.Results` in
  phase 1.7).

## Genuinely widespread conventions

- `[LoggerMessage]` source-generated logging (61 files) — prefer it over
  `ILogger.LogXxx()` in anything performance-sensitive.
- Primary constructors for DI everywhere; never mix one with a second,
  traditional constructor on the same class.
- `ZLinq`'s `AsValueEnumerable()` for hot-path LINQ — minor, not the default;
  no file under `src/` uses it since phase 2.8, only the benchmarks. Standard
  LINQ is fine elsewhere.
- `AsNoTracking()` for read-only EF Core queries.
- Clean Architecture layering (Domain has no dependency on Application/
  Infrastructure/Api) is enforced by ArchUnitNET
  (`TngTech.ArchUnitNET.xUnit`) in
  `tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs` and the
  Integration-project counterpart.

## Run sandboxes

`Thalos:Workflow:Sandbox:Enabled` picks the mode. Sandbox (the API default): each run's code builds and tests in its own
Docker container, and publish applies the exported patch into a clean worktree. Local (`Daedalus.Cli`, dev hosts without
Docker): host worktree, `.cs`/`.md` only. S6 boot check: a write grant with no `AllowedExtensions` needs the sandbox
enabled; an explicit empty list is refused. Publish refuses `.github/` and the other protected paths, plus
`.gitattributes`, `.gitmodules`, symlinks and submodule pointers. Details: `docs/development-guide.md`, "Run sandboxes".

## Central package management

`Directory.Packages.props` owns every version. `PackageReference` entries in
`.csproj` files carry **no `Version` attribute** — add/bump versions only in
`Directory.Packages.props`.

## Git commit messages: no nested parentheses

release-please's conventional-commit parser throws on a nested `(` inside a
parenthesized group and **silently drops the whole commit** from the
changelog — the workflow still reports success. Write `typeof IFoo`, not
`(typeof(IFoo))`; use a fenced code block or restructure the sentence instead
of nesting parens in a commit body. Square brackets are fine.

## Tests that pass for the wrong reason

The recurring defect in this codebase's history. Phase 1.7 alone found eight
instances, including a suite that passed only because the system under test
was broken, and two guards that could not fail by construction. Check
falsifiability **per assertion, not per test**: for every assertion, be able
to name the code change that would turn it red, and verify red/green by
actually making that change. Details:
`.superpowers/sdd/2026-09-20-phase-1.7-zeroalloc-migration-plan/progress.md`.

## Forbidden

No `Task.Wait()`/`.Result`/`.GetAwaiter().GetResult()`. No `async void`
outside event handlers. No throwing for expected/flow-control failures — use
`Result<T>`. No `Task.Run` to fake async over a sync API. No storing
`HttpContext` in a field. See `docs/development-guide.md` for the full list
with examples.
