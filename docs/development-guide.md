# Daedalus Development Guide

Long-form reference for this codebase's conventions. `CLAUDE.md` at the repo
root carries the short list of things that change day-to-day behaviour; this
file has the extended examples and the full pattern catalogue. Everything
below was checked against `src/` as of phase 1.7 (ZeroAlloc migration) — see
`.superpowers/sdd/2026-09-20-phase-1.7-zeroalloc-migration-plan/` for the
migration record if something here looks surprising.

## Architecture

Daedalus has one presentation layer, `Daedalus.Api` with `Daedalus.Web` (REST
controllers and a Blazor WebAssembly UI), plus the `Daedalus.Cli` host. They
share one `Daedalus.Application` + `Daedalus.Infrastructure` stack.

Phase 2.8 (October 2026) retired the Ralph loop and its `Daedalus.Console`
worker, which polled the board and drove an LLM iteration loop. Work now starts
when a person presses Manufacture on a board task: see "Manufacturing a task"
below. Project layout:

```
src/
├── Daedalus.Domain/           # Entities, value objects
├── Daedalus.Application/      # Mediator requests/handlers, DTOs, services,
│                               # repository interfaces (ITaskRepository, etc.)
├── Daedalus.Infrastructure/   # EF Core DbContext, repository implementations,
│                               # migrations, external service clients
├── Daedalus.Api/              # REST controllers, request/response models
├── Daedalus.Agents/           # Agent/session infrastructure (ZeroAlloc.Outbox),
│                               # the manufacture workflow host
├── Daedalus.Cli/              # Console-channel host
├── Daedalus.Web/              # Blazor WebAssembly UI
└── Daedalus.AppHost/          # .NET Aspire orchestration

tests/
├── Daedalus.Tests.Unit(.Domain|.Application|.Infrastructure)/
├── Daedalus.Tests.Integration/
└── Daedalus.Tests.Playwright.Api / Daedalus.Tests.Playwright.Browser/
```

### Manufacturing a task

A board task becomes work only when a person starts a run for it. There is no
automatic pickup.

- `POST /api/tasks/{id}/manufacture` (`TasksController.Manufacture`, logic in
  `src/Daedalus.Api/Services/TaskManufactureService.cs`) needs the
  `WorkflowResume` policy, the `developer` or `admin` role. It answers 201 with
  the run id, and attaches the run to the task (`Task.WorkflowRunId`). Every
  refusal comes before anything is spent:
  - 404: no such task.
  - 422: the project has no `RepositoryUrl`, or it matches no entry of
    `Thalos:Workflow:Repositories`, or a dependency's derived status is not
    `Completed`.
  - 409: the task's current run is still `Running` or `Awaiting`. A 409 also
    answers a task that changed while the run started: that run is cancelled
    and the body names its `runId` and whether it was cancelled. A failed
    attach is a 500 with the same extensions.
  - The starter's own failures map as `POST /api/workflow-runs` maps them
    (`ManufactureStartProblem`): 400 invalid, 422 unstartable, 503 when the
    starter is unavailable (with `Retry-After: 30`), and 503 without it when
    the workflow engine is disabled.
- The repository match: for a github.com URL, owner and name, ignoring case and
  form. For any other remote, the same string after trimming one trailing `/`
  and one trailing `.git` from each side. The work intent is the task's title,
  a blank line, then its description.
- A run waits at its `human_approval` gate. A developer or admin approves it
  with `POST /api/workflow-runs/{id}/resume` and a body of
  `{ "signal": "human_approval", "payload": null }`. `applyStandingInstructions`
  and `dropFindings` are optional. This `resume` is a workflow-run gate and has
  nothing to do with the retired task `resume` endpoint.

### Task status is derived

A task stores only its `WorkflowRunId`. `TaskStatusDerivation` computes the
status on read from the run:

| Run | Task status |
|---|---|
| Running | `InProgress` |
| Awaiting | `AwaitingApproval` (5) |
| Succeeded | `Completed` |
| Failed | `Failed` |
| Cancelled | `Cancelled` (6) |

5 and 6 are never stored: the check constraint `CK_Tasks_Status_Stored` allows
0 to 4. A task without a run shows its stored status, so an old Ralph task keeps
`Completed`, `Failed` or `Abandoned`. When the workflow engine is disabled on a
host, the reader answers Unknown and the stored status shows. Task update and
delete answer 409 while the task's run is live. The old Ralph-only fields
(`MaxIterations`, `CompletionPromise`, `IterationCount`, `CurrentSessionId`)
remain as columns and show read-only on old tasks.

### Costs and history

- After each agent node completes with usage, the host appends a `node-usage`
  workflow run record (`NodeUsageRecorder`). `NodeUsageBackfill` fills the
  records missing for older runs at host startup. It is not an EF migration, and
  it writes nothing for a (run, seq) that already has a record.
- Cost analytics reads four sources: `TaskExecutions` history, `node-usage`
  records (manufacture), chat sessions and scheduled sessions. Sessions owned by
  `workflow:*` are a manufacture run's node turns and are excluded from chat, so
  nothing counts twice. Session sources carry no model or cache split.
  The `by-session` cost endpoint was removed; `estimate` stays.
- `TaskExecutions` and `ExecutionSessions` are read-only history. The Web
  "Task Loop History" page shows them and cannot change them.
- `daedalus__search_failure_patterns` and `daedalus__search_learnings` read data
  only the retired loop wrote. Their descriptions and empty answers carry
  `FrozenHistory.Notice`. Agents still recall learnings through the `memory__*`
  tools and auto-recall; the budget is `Thalos:Memory:LearningsRecall`, and the
  old `Thalos:Memory:RalphRecall` key fails the boot.

`RalphLoopOrchestrator` in `src/Daedalus.Infrastructure/Services/CodeAnalysis/`
is an unrelated code-analysis type that kept its name (issue #279).

Clean Architecture layer boundaries (Domain must not depend on Application/
Infrastructure/Api, etc.) are enforced by ArchUnitNET tests in
`tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs` and
`tests/Daedalus.Tests.Integration/Architecture/CleanArchitectureTests.cs`. The
package is `TngTech.ArchUnitNET.xUnit` (0.13.2) — not the plain `ArchUnitNET`
package some older docs reference.

## Developer workflows

```bash
# Run everything (Postgres, migrations, API, Web) via .NET Aspire
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

The agent is told the mode; it never works it out (ruling R61). `ManufactureRunStarter` writes the run variable
`run_mode`, `sandbox` or `local`, from this setting, and only the start may write it. `RunModeRunner` states it in the
task text of `implement` and of every `review` lens pass, as a `## Run mode` section reading `Run mode: sandbox` or
`Run mode: local`, outside the block of agent-written variables. The implement and review skills key their
mode-dependent rules off that line, and a node whose tools disagree with it reports the discrepancy instead of
guessing. Manufacture v7's skills inferred the mode from whether `sandbox__build` was in the tool list, and a live run
misread its list and refused a `.csproj` change the sandbox allowed.
The Api's shipped implement grant lists no extensions, so running the Api in local mode needs the list set explicitly as
well, or S6 below refuses the boot:

```bash
Thalos__Workflow__Sandbox__Enabled=false
Thalos__Workflow__WriteGrants__0__AllowedExtensions__0=.cs
Thalos__Workflow__WriteGrants__0__AllowedExtensions__1=.md
```

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

Everything below is under `Thalos:Workflow:DataRoot`, which is `%LOCALAPPDATA%/Daedalus/workflow-data` when blank. A run
id written `N` is the 32 hex digits without hyphens; `D` is the usual hyphenated form.

Sandbox mode (Thalos 0.14.2):

| Path | Holds |
|---|---|
| `publish/mirrors/<repository>` | The API's mirror of each repository. A run's input bundle and its publish worktree are both cut from it. |
| `publish/runs/<run-id D>` | A run's publish worktree: a clean tree cut from the run's base commit, with its patch applied. |
| `publish/patches/` | Short-lived private copies of a patch while Thalos applies it. |
| `sandboxes/<run-id N>.patch` | The patch a parked run exported. |
| `sandboxes/<run-id D>.json` | The run's sandbox record. |
| `sandboxes/<run-id N>.bundle` | A new run's input bundle, deleted once the sandbox imported it. |

Local mode:

| Path | Holds |
|---|---|
| `mirrors/<repository>` | The mirror each run's worktree is cut from. |
| `runs/<run-id D>` | A run's worktree, which its agents write into and publish commits from. |

### Publish and protected paths

The sandbox exports a patch when the run parks at its gate. Nothing is applied at the gate. When the run is resumed, the
API applies the stored patch with Thalos's `GitPatchApplier` into the run's publish worktree, under
`<DataRoot>/publish/runs`: during the resume itself when it carries `applyStandingInstructions: true`, because the
approved standing instructions are written into that worktree, and otherwise in the `publish` node right after. Publish
then commits the staged index exactly as it stands, commits `AGENT.md` separately, and pushes.

A patch the check refuses is refused before anything is committed:

- A resume with `applyStandingInstructions: true` answers 422, naming the refused path, and the run stays at its gate.
- A resume without it answers 204, and the run then fails at `publish`, with the refusal, naming the path, as its error.

Protected paths cannot be written: Thalos's fixed defaults (`.git/`, `.gitattributes`, `.gitmodules`, `.github/`,
`.gitlab-ci.yml`, `azure-pipelines.yml`, `.azure-pipelines/`, `.circleci/`, `Jenkinsfile`), which configuration cannot
remove, plus the extras in `Sandbox:ProtectedPaths` and the standing-instructions file, `AGENT.md` by default. That file is
protected as a directory too, `AGENT.md/`, because Thalos matches a file entry exactly and publish would otherwise commit a
path such as `AGENT.md/x` as the approved standing-instructions change (Thalos #265). The publish-side check is the
control; the sandbox-side refusal is only a convenience. In sandbox mode publish also refuses `.gitattributes` and
`.gitmodules` at any depth, symlinks and submodule pointers.

### Test results in the pull request

A run's `sandbox__test` and `sandbox__build` results are recorded as `test-result` run records. The pull request body's
`## Tests` section states the last test run as reported by the run's sandbox and not verified, because the sandbox runs
the change's own code. When the run recorded a workspace write after that test run, the section says the change may
differ from what was tested.

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

## Deferred review findings and the issues tools

Phase 2.7. A manufacture review can approve a change and still see defects it should not fix in that change. Those are
recorded as deferred findings, offered to the person at the gate, and filed as GitHub issues after publish. The spec is
`docs/superpowers/specs/2026-10-09-phase-2.7-issues-design.md`; its Amendments table overrides its body.

### Deferred findings

- **Approval only.** `daedalus__report_review_outcome` takes an optional `deferred` JSON array. It is accepted only with an
  approval; on a rejection everything is in scope to fix, so a `deferred` there is refused.
- **Entry shape.** `{file, line, title, scenario, reason, existingIssue?}`. `reason` is one of `different-area`,
  `needs-decision`, `too-large` or `blocked`. A finding that fits none of the four is not deferrable and the reviewer must
  reject so it is fixed. `ReviewEvidence.Validate` refuses a hollow entry. At most 32 entries, 1000 characters per field,
  `title` at most 200, no NUL.
- **Host-owned.** The tool only validates `deferred`; `ReviewLensRunner.RecordAsync` writes it into the run's
  `review-evidence` record, never a run variable, so an agent cannot plant a finding.
- **Only where it will be filed.** `ReviewLensRunner` refuses a pass that defers anything when the run's pinned process
  definition has no `file-review-findings` node, as on v8, because the gate would show those findings and nothing would
  file them. The check sits there, not in the tool, because the tool is stateless and cannot know the run's version.
- **Plain, relative fields.** `ReviewEvidence.Validate` refuses a `title` containing `<!--` and a `file` that is rooted,
  has a drive letter or has a `..` segment, so the issue's permalink stays inside the repository.
- **Only the approving visit counts.** The host keeps the deferred findings of the final review visit's three lens passes.
  Earlier visits ended in a rejection and their `file:line` points at code that was edited afterwards. Each kept finding
  has the id `<lens>-<n>`.

### The gate and `dropFindings`

`GET /api/workflow-runs/{id}` shows `deferredFindings` with their ids. `ResumeWorkflowRunRequest.DropFindings` names ids not
to file.

- An id that is not in the run's deferred set is a 422, so a typo cannot silently file a finding.
- The accepted list is written as a `findings-dropped` `WorkflowRunRecord` with the resumer as its principal. A run with
  deferred findings always gets one, even when the list is empty, so the latest record is the one that applies.
- A resume that loses a concurrent race, or fails after the drop was recorded, voids that record with a
  `findings-drop-voided` record, so a list that never took effect cannot be read as the latest.

### The issues tools

| Tool | Source | Who may call it |
|---|---|---|
| `issues__get` | `issues`, its own source | The `reviewer` role and the chat Architect only |
| `issues__search` | `issues` | The `reviewer` role and the chat Architect only |
| `repoaction__create_issue` | `repoaction` | `developer` policy, so developer chat sessions only; no workflow role can call it |

- `issues__get(repo, number)` returns title, state, labels and the body cut to about 2 KB, and refuses a pull request number.
- `issues__search(repo, query, state)` prepends `repo:<repo> is:issue` and returns at most 10 hits with no bodies. It refuses
  a query that carries its own `repo:`, `org:`, `user:` or `owner:` qualifier, because GitHub ORs several `repo:`
  qualifiers, and drops any hit from another repository.
- Both label issue text as third-party content. Only the reviewer's and the Architect's `Tools` lists name `issues__*`, in
  both `appsettings.json` files.

### The `file-findings` node

Process v9 adds `publish -> file-findings -> done`. The node runs the `file-review-findings` host action. No model runs it
and no agent holds a tool that could.

- **Inputs are host-owned.** The repository and PR number come from the run's `pr_url`, and the action checks the repository
  against the allow-list again. It needs no workspace.
- **Per finding that was not dropped.** With `existingIssue: N` the host re-reads #N and comments there if it is an open
  issue; otherwise, or with no pointer, it files a new issue with a permalink at the PR's head commit, the scenario, the
  reason, the lens, the run id and the PR link. No labels are set. One PR comment then lists what was filed, commented on
  and dropped, and by whom.
- **Idempotent.** Every issue, comment and the PR summary ends with the marker
  `<!-- daedalus-run:<runId> finding:<id> -->`, and each result is written as a `finding-filed` record when it is handled.
  A retry skips recorded findings and scans issues and comments updated since the resume. A marker counts only on the final
  line of a body authored by the token's own login. The host escapes `<!--` in everything a model wrote, and so do
  `repoaction__create_issue` and `repoaction__comment_on_issue`, which post as that same login, so a marker anywhere else
  is not ours.
- **Outcomes.** `filed` and `none`. `none` makes no GitHub call and is decided before the `pr_url`, allow-list and resume
  checks, since nothing is written. Every failure fails the node, and the run stays `Failed` at
  `file-findings` for the admin retry, which re-runs that node alone.

**Token requirement.** Use a **fine-grained** personal access token whose repository access is limited to the
allow-listed repositories, with Issues: Read and write, plus the Contents: Read and write and Pull requests: Read and write
that publish already needs. Not a classic PAT: `issues__get` and `issues__search` accept any repository the token can see,
so a classic token's reach to every repository of its owner would let an agent read issues well outside the allow-list.
The token must belong to a user: `file-findings` reads its own login with `GET /user` to attribute markers, so a GitHub App
installation token or an Actions `GITHUB_TOKEN` gets a 403 there and the node fails. A fine-grained token that lacks
Issues also fails the node with a 403, as phase 2.5's first publish did on Contents.

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
services.AddHttpClient<AzureDevOpsPullRequestFactory>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "Daedalus");
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

61 files in `src/` use `[LoggerMessage]` source-generated logging — this is
the dominant logging convention, not a suggestion:

```csharp
public sealed partial class TaskManufactureService(/* ... */ ILogger<TaskManufactureService> logger)
{
    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Run {RunId}, started for task {TaskId} but not attached, could not be cancelled")]
    private static partial void LogCancelFailed(ILogger logger, Guid runId, Guid taskId);
}
```

Mark the containing class `partial`, give each method a unique `EventId`
within that class, and prefer this over `ILogger.LogXxx()` extension methods
in anything on a hot or frequently called path.

## ZLinq

`ZLinq` is referenced by `Daedalus.Application` and `Daedalus.Infrastructure`,
but since phase 2.8 deleted the Ralph pipeline no file under `src/` calls
`AsValueEnumerable()`; only the benchmarks use it. It is a minor option — reach
for it in genuinely hot paths, not by default; standard LINQ is used everywhere
else.

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
- `ConfigureAwait(false)` is used in 74 files; keep using it in library-style
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
