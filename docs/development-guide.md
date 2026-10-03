# Daedalus Development Guide

Long-form reference for this codebase's conventions. `CLAUDE.md` at the repo
root carries the short list of things that change day-to-day behaviour; this
file has the extended examples and the full pattern catalogue. Everything
below was checked against `src/` as of phase 1.7 (ZeroAlloc migration) — see
`.superpowers/sdd/2026-09-20-phase-1.7-zeroalloc-migration-plan/` for the
migration record if something here looks surprising.

## Architecture

Daedalus has two presentation layers sharing one Application + Infrastructure
stack:

- **Web/API layer** (`Daedalus.Api`, `Daedalus.Web`) — REST controllers and a
  Blazor WebAssembly UI.
- **Console layer** (`Daedalus.Console`, the "Ralph Loop") — a background
  worker that polls for tasks and drives an LLM iteration loop directly
  against the database, bypassing HTTP for latency.

Both layers depend on the same `Daedalus.Application` and `Daedalus.Domain`
projects. Project layout:

```
src/
├── Daedalus.Domain/           # Entities, value objects
├── Daedalus.Application/      # Mediator requests/handlers, DTOs, services,
│                               # repository interfaces (ITaskRepository, etc.)
├── Daedalus.Infrastructure/   # EF Core DbContext, repository implementations,
│                               # migrations, external service clients
├── Daedalus.Api/              # REST controllers, request/response models
├── Daedalus.Console/          # Ralph Loop worker (RalphLoopWorker.cs)
├── Daedalus.Agents/           # Agent/session infrastructure (ZeroAlloc.Outbox)
├── Daedalus.Cli/              # Command-line tooling
├── Daedalus.Web/              # Blazor WebAssembly UI
└── Daedalus.AppHost/          # .NET Aspire orchestration

tests/
├── Daedalus.Tests.Unit(.Domain|.Application|.Infrastructure)/
├── Daedalus.Tests.Integration/
└── Daedalus.Tests.Playwright.Api / Daedalus.Tests.Playwright.Browser/
```

There is no file named `RalphLoopService.cs` — the worker's hosted service is
`src/Daedalus.Console/RalphLoopWorker.cs`; the pipeline logic lives in
`src/Daedalus.Application/Services/RalphLoopPipelineService.cs` and
`src/Daedalus.Infrastructure/Services/CodeAnalysis/RalphLoopOrchestrator.cs`.

Task claiming is `TaskRepository.ClaimNextAsync(Guid sessionId, ...)` in
`src/Daedalus.Infrastructure/Persistence/TaskRepository.cs` (not
`ClaimNextTaskAsync`), which sets `Task.CurrentSessionId` via `task.Claim(...)`.

Clean Architecture layer boundaries (Domain must not depend on Application/
Infrastructure/Api, etc.) are enforced by ArchUnitNET tests in
`tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs` and
`tests/Daedalus.Tests.Integration/Architecture/CleanArchitectureTests.cs`. The
package is `TngTech.ArchUnitNET.xUnit` (0.13.2) — not the plain `ArchUnitNET`
package some older docs reference.

## Developer workflows

```bash
# Run everything (API, Console worker, Postgres) via .NET Aspire
dotnet run --project src/Daedalus.AppHost
# Dashboard: http://localhost:17300

# Build / test
dotnet build
dotnet test
dotnet test tests/Daedalus.Tests.Unit
dotnet test tests/Daedalus.Tests.Integration

# Format (run before committing)
dotnet format
```

## Run sandboxes

Phase 2.6. A manufacture run edits, builds and tests code the model wrote, so the API never does that on the host:
each run gets its own Docker container, and the host only applies the run's exported patch. The diagram is section 22
of `docs/architecture-diagrams.md`.

### The two modes

`Thalos:Workflow:Sandbox:Enabled` picks the mode.

| Mode | Used by | What a run may write |
|---|---|---|
| Sandbox (`true`) | `Daedalus.Api`, which ships it on, and the AppHost | Whatever each write grant lists, or any file when a grant lists no extensions. MSBuild evaluates project, props and targets files inside the run's container, never on the host. |
| Local (`false`) | `Daedalus.Cli` and dev hosts without Docker | A git worktree on the host, and only `.cs` and `.md`. A grant that lists no extensions is refused at boot. |

Boot-time checks, all in `DaedalusAgentsServiceCollectionExtensions` (a failure is an `InvalidOperationException` at
startup, never a runtime surprise):

- **S6.** A write grant with no `AllowedExtensions` needs `Sandbox:Enabled`. Without the sandbox it is refused.
- **Uniform grants.** In sandbox mode every grant's extension list must be the same. Per-node narrowing does not exist
  inside a sandbox, because every `workspace__*` call there runs as the sandbox's own caller (Thalos issue #251).
- **No empty list.** An explicit `AllowedExtensions: []` is refused. The binder reads it as a missing key, which would
  mean "any extension" under the sandbox, so the raw configuration section decides.
- **Do not chain workflow configuration through `ConfigurationBuilder.AddConfiguration`.** A chained configuration turns
  an empty list into a missing key, which defeats the check above. Stack JSON sources on one builder instead, as
  `SandboxConfigTests` does.
- **Solution required.** Every `Repositories` entry needs `Solution` set, because a sandboxed run's Roslyn server starts
  on that solution inside the container.
- **Images required.** `Sandbox:Image`, `Sandbox:Docker:GatewayImage` and `Sandbox:Docker:EgressImage` must not be blank.
  `appsettings.json` pins the gateway (nginx) and egress (squid) images by digest.

### Running the AppHost

```bash
dotnet run --project src/Daedalus.AppHost
```

The AppHost needs Docker with Linux containers. It builds `daedalus-sandbox:dev` from `src/Daedalus.Sandbox/Dockerfile`
before the API starts, then starts the API with `Thalos__Workflow__Sandbox__Enabled=true` and
`Thalos__Workflow__Sandbox__Image=daedalus-sandbox:dev`. The sandbox image is the .NET SDK plus git and RoslynCodeLens,
because a run restores, builds and tests inside it.

Without Docker the API still boots in sandbox mode: start-up reconciliation logs the problem and deletes nothing, and
starting a run answers 503. Start-run answers: invalid 400, unstartable 422, unavailable 503 with `Retry-After: 30`,
engine disabled 503 without `Retry-After`, anything else 500.

A `DOCKER_HOST` that needs TLS client credentials is not supported: Thalos's Docker options carry no TLS settings. Use
`Sandbox:Docker:Endpoint` for a socket, pipe or plain TCP endpoint.

### Inspecting a run's sandbox

The Thalos 0.14.2 Docker runtime labels everything it creates: `thalos.sandbox=true`, `thalos.sandbox.network=<network>`,
`thalos.sandbox.role` (`run`, `gateway` or `egress`) and, on a run's container and volume, `thalos.run_id=<run id>`.

```bash
docker ps --filter label=thalos.run_id=<run-id>        # the run's container
docker ps -a --filter label=thalos.sandbox=true        # every sandbox object, infrastructure included
docker logs <container>                                # the sandbox host's log
docker exec -it <container> bash                       # look at /work/repo, the run's checkout
```

The infrastructure containers are named `<network>-gateway` and `<network>-egress`; the network defaults to
`daedalus-sandboxes` (`Sandbox:Docker:Network`). Each run's volume is `thalos-sandbox-<id>-work`.

### Where files live

Everything below is under `Thalos:Workflow:DataRoot`, which is `%LOCALAPPDATA%/Daedalus/workflow-data` when blank.

| Path | Holds |
|---|---|
| `mirrors/<repository>` | The API's mirror of each repository. A run's input bundle is cut from it. |
| `sandboxes/<run-id>.patch` | The patch a parked run exported, with the sandbox record next to it. |
| `publish/` | Publish worktrees: clean trees the API applies a patch into. |
| `runs/` | Worktrees of local-mode runs. |
| `patches/` | Short-lived copies of a patch while Thalos applies it. |

### Publish and protected paths

The sandbox exports a patch. At the review gate the API applies it with Thalos's `GitPatchApplier` into a clean worktree
under `<DataRoot>/publish`, commits the staged index exactly as it stands, then pushes. `AGENT.md` is committed
separately. A patch the API refuses on resume answers 422.

Protected paths cannot be written: Thalos's fixed defaults (`.git/`, `.gitattributes`, `.gitmodules`, `.github/`,
`.gitlab-ci.yml`, `azure-pipelines.yml`, `.azure-pipelines/`, `.circleci/`, `Jenkinsfile`), which configuration cannot
remove, plus the extras in `Sandbox:ProtectedPaths` and the standing-instructions file, `AGENT.md` by default. The
publish-side check is the control; the sandbox-side refusal is only a convenience. In sandbox mode publish also refuses
`.gitattributes` and `.gitmodules` at any depth, symlinks and submodule pointers.

### Test results in the pull request

A run's `sandbox__test` and `sandbox__build` results are recorded as `test-result` run records. The pull request body's
`## Tests` section states them as reported by the run's sandbox and not verified, because the sandbox runs the change's
own code.

### Cleaning up

Run these in Git Bash. `$(...)` is not PowerShell syntax.

```bash
docker rm -f $(docker ps -aq --filter label=thalos.sandbox=true)
docker network rm daedalus-sandboxes
```

The first removes every Thalos sandbox container on the engine, other hosts' included. To touch only this host's
network, add `--filter label=thalos.sandbox.network=daedalus-sandboxes`. Remove leftover volumes with
`docker volume rm $(docker volume ls -q --filter label=thalos.sandbox=true)`. The network cannot go while a container is
attached, so remove containers first.

### Known limitations

- A host stopped mid-setup can leave a `Created` egress container and its network (Thalos #243). The cleanup above
  clears it.
- MCP stdio servers pin thread-pool threads on Windows (Thalos #257).
- The HTTP discover probe has a workaround (Thalos #260).
- Publish-path error codes are coarse (Thalos #263).

### The sandbox suite

`ManufactureSandboxEndToEndTests` and its fixture run manufacture end to end against real Docker. They need Docker with
Linux containers, plus nuget.org and Docker Hub reachable: the suite builds the real sandbox image, and a run restores
NuGet packages through the egress proxy. The suite skips when Docker is missing or runs Windows containers
(`SandboxEngineProbeTests` pins that). Each host in the suite gets its own network, named `daedalus-b8-*`, so a developer's
own `daedalus-sandboxes` network is left alone.

## ZeroAlloc.Results — full pattern catalogue

`ZeroAlloc.Results` (not CSharpFunctionalExtensions — that package was removed
in phase 1.7) is the Result type used throughout `src/`.

```csharp
using ZeroAlloc.Results;
using ZeroAlloc.Results.Extensions; // Bind, Map, Match, Combine, Tap, TapError, MapError, Ensure

public async Task<Result<Customer>> GetCustomerAsync(Guid id, CancellationToken ct)
{
    var customer = await dbContext.Customers
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);

    return customer is not null
        ? Result<Customer>.Success(customer)
        : Result<Customer>.Failure($"Customer with ID {id} not found");
}

public async Task<Result<OrderConfirmation>> PlaceOrderAsync(OrderRequest request, CancellationToken ct)
{
    return await ValidateRequest(request)
        .Bind(r => GetCustomerAsync(r.CustomerId, ct))
        .Bind(c => CheckInventoryAsync(request.Items, ct))
        .Map(inventory => CreateOrder(request, inventory))
        .Bind(o => SaveOrderAsync(o, ct))
        .Map(o => new OrderConfirmation(o.Id, o.Total));
}
```

Notes verified against the installed package (`ZeroAlloc.Results` 1.2.2 /
0.1.4 XML docs):

- There is no non-generic `Result.Success(...)`/`Result.Failure(...)` that
  infers `T`. Always qualify: `Result<T>.Success(value)`,
  `Result<T>.Failure(error)`.
- `Bind`, `Map`, `Match`, `Combine`, `Tap`, `TapError`, `MapError` are
  extension methods on `ZeroAlloc.Results.Extensions.ResultExtensions` /
  `ResultAsyncExtensions` — add the `using`, they are not instance members.
- `Ensure` is only defined for the two-generic `Result<T,E>` shape
  (`ResultExtensions.Ensure<T,E>`). There is no overload for the common
  single-generic `Result<T>` — write the failing-check explicitly instead of
  reaching for `.Ensure(...)` on a `Result<T>`.
- `Result<T>` has implicit conversions from both `T` and from `string`. Where
  `T` is itself `string`, a bare `return someString;` is ambiguous between
  "this is the success value" and "this is an error message" — always use the
  explicit `Result<string>.Success(...)`/`Failure(...)` form there.

## ZeroAlloc.Mediator — visibility rules

`ZeroAlloc.Mediator` (5.1.1) replaced the hand-rolled CQRS handler dispatch.
Commands/queries are `readonly record struct` types implementing `IRequest<T>`:

```csharp
public readonly record struct CreateTaskCommand(string Prompt, string CompletionPromise, /* ... */)
    : IRequest<Result<TaskDto>>;
```

The generator rejects reference-type records with `ZAM003` ("Request type is
a class; expected a struct") — every command/query in
`src/Daedalus.Application/Commands/*` and `Queries/*` must be a
`readonly record struct`.

`AddMediator()` and the generated `IMediator` interface are `internal` to
`Daedalus.Application` (see `ApplicationServiceExtensions.cs`). They cannot
appear in a public method signature, and `Program.cs` in `Daedalus.Api` never
references `IMediator` directly. Two `internal` classes inside
`Daedalus.Application` are allowed to depend on it —
`Services/ApplicationCommands.cs` and `Services/PrdService.cs` — and
everything outside the assembly (controllers included) depends on the public
facade `IApplicationCommands` (`src/Daedalus.Application/Abstractions/IApplicationCommands.cs`)
instead. `TasksController` and `ProjectsController` are the current
consumers.

## Validation — explicit registration, not `ZeroAlloc.Validation.Inject`

`ZeroAlloc.Validation.Inject`'s `AddZeroAllocValidators()` only discovers
`class`-declared `[Validate]` targets by generator predicate; it silently
skips `record` targets, which is what all 8 `[Validate]` DTOs in
`Daedalus.Application.DTOs` are. Bulk auto-registration could not be used, so
validator registration is explicit and hand-maintained in two places:

1. `ApplicationServiceExtensions.AddApplicationServices` registers each
   `ValidatorFor<T>` explicitly, e.g.
   `services.AddSingleton<ValidatorFor<CreateTaskDto>, CreateTaskDtoValidator>();`
   (currently ×8).
2. `Daedalus.Api`'s `Program.cs` registers the matching `IValidationAdapter`
   for each type — this is what `ZeroAllocValidationFilter` actually consults
   at request time.

A DTO that gains `[Validate]` but no adapter registration in `Program.cs` is
**silently never validated** — no exception, no 500. This is guarded by
`Daedalus.Tests.Integration.Architecture.CleanArchitectureTests`, which
reflects over `Daedalus.Application` for `[Validate]`-attributed types and
asserts every one has a live `IValidationAdapter` registration in the built
`Daedalus.Api` service provider. If you add a `[Validate]` DTO, add its
adapter registration in the same change or this test fails.

## Resilient HTTP calls

There is no dependency on the `Polly` NuGet package directly. Resilient HTTP
clients use `Microsoft.Extensions.Http.Resilience` (which wraps Polly v8
internally) via `AddStandardResilienceHandler`, used in
`src/Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs`
and `src/Daedalus.Web/Program.cs`:

```csharp
services.AddHttpClient<GitHubPullRequestFactory>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "Daedalus-RalphLoop");
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    });
```

Do not add the raw `Polly` package or hand-roll `HttpPolicyExtensions` /
`PolicyWrap` — use `AddStandardResilienceHandler` and configure its
`Retry`/`AttemptTimeout`/`TotalRequestTimeout`/`CircuitBreaker` options.

## Repository pattern

There is no generic `IRepository<T>`. Repositories are one interface per
aggregate, defined in `src/Daedalus.Application/Abstractions/` (e.g.
`ITaskRepository`, `IProjectRepository`, `IExecutionSessionRepository`,
`IBrainstormRepository`, `IRepositoryConfigurationRepository`) and implemented
in `src/Daedalus.Infrastructure/Persistence/`. Methods return
`Result<T>`/`Result` rather than throwing for expected failures (not-found,
concurrency conflicts).

## Value objects

`ZeroAlloc.ValueObjects` (2.0.7) supplies a `[ValueObject]` attribute used on
a small number of types (3 in `src/`) for value equality — this is a real but
minor convention, not the dominant style. There is no dependency on
`ZeroAlloc.Specification`, and no `Specification<T>`/`ISpecification<T>` types
exist in `src/` — the Specification Pattern documented in the old Copilot
instructions is not used here.

## Primary constructors

Primary constructors are the dominant DI style across the codebase (services,
controllers, repositories):

```csharp
public sealed partial class TasksController(
    ITaskQueryService taskService,
    IApplicationCommands commands,
    ILogger<TasksController> logger) : ControllerBase
{
    // ...
}
```

Do not mix a primary constructor with an additional traditional constructor
on the same class — pick one.

## Compile-time logging with `LoggerMessage`

66 files in `src/` use `[LoggerMessage]` source-generated logging — this is
the dominant logging convention, not a suggestion:

```csharp
public sealed partial class TaskRepository(ApplicationDbContext dbContext, ILogger<TaskRepository> logger)
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Task {TaskId} claimed by session {SessionId}")]
    private static partial void LogTaskClaimed(ILogger logger, Guid sessionId, Guid taskId);
}
```

Mark the containing class `partial`, give each method a unique `EventId`
within that class, and prefer this over `ILogger.LogXxx()` extension methods
in anything on a hot or frequently called path.

## ZLinq

`ZLinq` is referenced and used in 5 files (`LearningsService.cs`,
`PhaseOrchestrator.cs`, `RalphLoopPipelineService.cs`,
`RalphPromptTemplateBuilder.cs`, `LearningCategory.cs`) via
`AsValueEnumerable()`. It is real but a minor convention — reach for it in
genuinely hot paths, not by default; standard LINQ is used everywhere else.

```csharp
using ZLinq;

var filtered = tasks
    .AsValueEnumerable()
    .Where(t => t.Status == TaskStatus.Pending)
    .Select(MapToDto)
    .ToList();
```

## Async conventions

- No `Task.Wait()`, `.Result`, or `.GetAwaiter().GetResult()` — none exist in
  `src/` today; keep it that way.
- No `async void` outside event handlers — none exist in `src/` today.
- `ConfigureAwait(false)` is used in 54 files; keep using it in library-style
  code (Application/Infrastructure), not required in ASP.NET Core request
  pipeline code without a `SynchronizationContext`.
- `IAsyncEnumerable<T>` is used in 3 files — a real but narrow pattern for
  streaming; don't force it onto ordinary list-returning methods.

## EF Core

- `AsNoTracking()` is used in 18 files for read-only queries — the default
  for anything that doesn't mutate.
- `ExecuteUpdateAsync`/`ExecuteDeleteAsync` for bulk operations instead of
  loading entities.
- DbContext pooling and transactions are used where appropriate; check
  `src/Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs`
  for current registration before adding a new pattern.

## API layer conventions

- Controllers return `IActionResult`/`ProblemDetails` via `Result.Match(...)`
  patterns; minimal APIs are not the primary style here (this is a
  controller-based API).
- Response compression is enabled once, in `src/Daedalus.Api/Program.cs`
  (`AddResponseCompression`).
- `System.Text.Json` source generation is in active use —
  `src/Daedalus.Api/ApiJsonSerializerContext.cs` — for the API's JSON
  contracts.

## Performance helpers

`src/Daedalus.Application/Services/PerformanceOptimizations.cs` provides
zero-allocation helpers for hot paths (`ValidateAndTrimString`,
`ContainsTarget`, `CountOccurrences`, `CreateOptimizedBuilder`) — use these
instead of hand-rolled `Trim()`/`Contains()` calls in request-validation code
that runs on every command.

```csharp
var prompt = PerformanceOptimizations.ValidateAndTrimString(command.Prompt, out var promptError);
if (prompt is null)
    return Result<TaskDto>.Failure($"Prompt: {promptError}");
```

## Central package management

`Directory.Packages.props` at the repo root is the single source of package
versions (`ManagePackageVersionsCentrally = true`). Every `.csproj`
`PackageReference` carries no `Version` attribute — add or bump a version in
`Directory.Packages.props` only.

## The recurring defect: tests that pass for the wrong reason

Phase 1.7 (the ZeroAlloc migration) found eight separate instances of tests
that appeared to cover a behaviour but structurally could not fail if that
behaviour broke — including one full suite whose tests passed only because
the system under test was already broken, and two guards that could not fail
by construction (see
`.superpowers/sdd/2026-09-20-phase-1.7-zeroalloc-migration-plan/progress.md`,
entries tagged "FIFTH"/"EIGHTH instance of the pattern").

The rule that came out of it: **check falsifiability per assertion, not per
test.** For every assertion in a test, be able to name the specific code
change that would turn it red. A test with five assertions where only one is
actually exercised by the scenario is a false-negative risk on the other
four, even though the test overall "passes for a reason." When reviewing or
writing a test, mutate the production code the assertion claims to guard and
confirm the test goes red before trusting it green.

## Forbidden patterns

```csharp
// Blocking on async
var result = GetDataAsync().Result;
GetDataAsync().GetAwaiter().GetResult();
Task.WaitAll(tasks);

// async void outside event handlers
public async void ProcessData() { }

// Throwing for expected/flow-control failures — use Result<T> instead
throw new NotFoundException("Customer not found");

// Task.Run to fake async over a sync API
public Task<Data> GetDataAsync() => Task.Run(() => GetData());

// Storing HttpContext in a field — use IHttpContextAccessor
public class MyService { private readonly HttpContext _context; }

// Allocating large buffers directly — use ArrayPool<T> for >= 85KB
var buffer = new byte[100_000];

// String concatenation in loops — use StringBuilder
foreach (var item in items) result += item.ToString();

// Mixing a primary constructor with a second, traditional constructor
public class CustomerService(ICustomerRepository repository)
{
    private readonly ILogger<CustomerService> _logger;
    public CustomerService(ICustomerRepository repository, ILogger<CustomerService> logger) : this(repository)
        => _logger = logger;
}
```

## File naming

- Classes: `PascalCase.cs`
- Interfaces: `IPascalCase.cs`
- DTOs: `PascalCaseDto.cs`
- Tests: `ClassNameTests.cs`

## Formatting

`dotnet format` before committing; `.editorconfig` at the repo root is the
source of truth for style rules and is picked up automatically by
`dotnet format` and IDEs — don't override it in personal editor settings.
