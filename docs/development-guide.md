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
├── Daedalus.Migrations/       # EF Core migration runner, run before the hosts
├── Daedalus.Sandbox/          # Per-run sandbox container host and its Dockerfile
├── Daedalus.ServiceDefaults/  # Shared OpenTelemetry, logging, DB registration
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
  precondition is checked before the run starts:
  - 404: no such task.
  - 422: the project has no `RepositoryUrl`, or it matches no entry of
    `Thalos:Workflow:Repositories`, or a dependency's derived status is not
    `Completed`, or a dependency does not exist in the project.
  - 409: the task's current run is still `Running` or `Awaiting`. A 409 also
    answers a task that changed while the run started: that run is cancelled
    and the body names its `runId` and whether it was cancelled. A failed
    attach is a 500 with the same extensions, and a failed read of the task,
    its project or its dependencies is a 500 with generic text.
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

### Node selection

Phase 2.10 lets a cheap Minos decision choose, before an agent node runs, which of its role's allowed models the turn
uses and which tool families it keeps. The aim is fewer tokens and fewer tools to confuse the model: a review is 70 to
80 percent of a run's cost, and the implementer and reviewer are each handed about 54 Roslyn tools. The design is in
`docs/superpowers/specs/2026-10-13-phase-2.10-minos-decisions-design.md`. The decision code is in `src/Daedalus.Agents/`.

- **Off by default.** Decisions run only when `Thalos:Decisions:ZeroDataRetentionAccount` is `true` **and** an
  OpenRouter key is present (`DecisionsConfig.IsEnabled`). The decision reads the work intent, so task text goes to
  OpenRouter. The flag is the owner's statement that the OpenRouter account has zero data retention. Since Minos 1.1.0
  every call also sends OpenRouter's `provider` preferences, `{"zdr":true,"data_collection":"deny"}`, so OpenRouter
  routes only to qualifying endpoints and refuses the call when none qualifies; the decision then falls back like any
  failure (Minos.NET#202). Both stay: the account flag is the gate, the request preference is enforced per call.
  With decisions off, no decision client is registered and every node runs on its default model and full tool list,
  as before, and so does every review lens: `PinnedLensModels` applies only while decisions
  are on (`ActiveLensPins`, by the same `IsEnabled`). `DecisionsStartupLog` says once at host start whether decisions are
  on and, if not, why: the flag is not true, no key is configured, or both, and how many configured lens pins are
  inactive, by count only. It never logs the key or its length. A blank key is no key (`DecisionsConfig.HasKey`).
- **The key.** The AppHost parameter `openrouter-key` (secret, empty by default) becomes the `OPENROUTER_API_KEY`
  environment variable of the API, which `AddDaedalusAgents` reads under `OpenRouterKeyName`. Set it with AppHost
  user secrets (`Parameters:openrouter-key`) or `PARAMETERS__OPENROUTER_KEY`, never in a file in the repository.
- **Which turns.** `NodeSelectionRunner` wraps the subagent runner and decides only a turn whose node pins the
  `manufacture-implement` or `manufacture-review` skill. The `review-replay` process runs the same review node, so its
  turns are decided too. A role with no `Thalos:Agents` entry, and every other turn, is forwarded unchanged.
- **The menu is configuration.** A role's `AllowedModels` (cheapest first) and `Tools` in `Thalos:Agents` are what a
  decision chooses from. Empty `AllowedModels` means no model choice (`no-allowed-models`). The agent's default model
  need not be listed. Thalos 0.17.0 carries `AllowedModels` on the agent and refuses a turn whose model is outside it,
  or a tool filter that names a tool the agent does not hold, so a selector bug is loud and a decision can only narrow.
  Every allowed model needs a `ModelPricing:Models` price, which `CostAnalyticsPricingDriftTests` guards. Tool patterns
  that match no tool on this host, such as `sandbox__build` in local mode, are not offered.
- **The questions.** `NodeQuestions` asks one `Demand` score (Routine, Moderate or Hard) and six tool-family
  probabilities: `roslyn`, `sandbox`, `memory`, `skills`, `context7` and `issues`. The state sent is the role, node,
  visit and the work intent cut at `NodeSelectionDecider.MaxIntentCharacters`.
- **Mapping** (`NodeSelectionMapper`, pure). Routine takes the first allowed model, Hard the last, Moderate the middle
  one, so on a two-model menu Moderate and Hard both take the stronger model. A demand answer below
  `ModelConfidence` (default 0.6) keeps the default model. A family is kept when its probability reaches
  `ToolFamilyProbability` (default 0.5). A family is the part of a tool pattern before `__`. **`workspace` and
  `daedalus` are never removable**, nor is a family the questions do not cover. If nothing is removed, or nothing would
  be kept, the filter is null and the node keeps its full list. The filter is set whenever it narrowed, whatever the
  model.
- **Narrowing stops after a rejected review.** Once either `implement` or `review` is on a later visit, tools are no
  longer narrowed, so the retry and its re-review get full lists. The model is still decided.
- **Fallbacks.** A decision that falls back never fails a node; only a decision that cannot be recorded does, and only
  while decisions are on (see "Once per visit"). Each fallback has a reason, in `fallback` on the record and in the
  telemetry; the default model applies, except where `tools-unresolved` keeps the decided model, and the request is left as it was before this existed.

  | Reason | When |
  |---|---|
  | `off` | No decision client: decisions are off. |
  | `timeout` | The decision outran `Thalos:Decisions:Timeout` (default 5 seconds). The budget covers the tool resolution that builds the menu, the Minos call and Thalos' check of the overrides, so a slow tool source falls back too. The visit runs the full list on its own model. |
  | `error:{kind}` | Minos returned a failure; `{kind}` is its `DecisionErrorKind`, a closed enum. |
  | `error:Client` | The client threw, anything but the caller's own cancellation: a disposed client, a provider that answered other questions, a network fault, a bug. The log names the exception type only. |
  | `low-confidence` | The demand answer was below `ModelConfidence`. |
  | `no-allowed-models` | The role offers no allowed models. |
  | `tools-unresolved` | Thalos refused the decided tool filter, or the model with it. The visit runs the full list, and the decided model only if Thalos accepts that alone. |
  | `error:Validate` | Thalos could not check the overrides (not a refusal). The visit runs the full list on its own model. |

  An earlier reason is kept rather than replaced by `tools-unresolved` or `error:Validate`. The check runs before the
  span is tagged and the decision counted (`NodeSelectionDecider.DecideAsync` with a `settle` callback), so a fallback
  the host applies is told and counted like the decider's own.
- **Once per visit, reused on redelivery.** The decision is recorded as a `node-selection` run record at the run's
  `CurrentSeq`. A redelivered dispatch reads it back and runs on what the first attempt ran on. The partial unique index
  `UX_WorkflowRunRecords_NodeSelection_RunId_Seq` (on run and seq, filtered to `Kind = 'node-selection'`) settles two
  deliveries deciding at once: the loser reads the winner's record and runs on that. While decisions are on, a decision
  that cannot be recorded, or a record that does not parse, fails the node with a `StoreError`: running on a decision a
  replay could not read back would let a replay run differently. While they are off it does not: the turn is the
  unchanged request either way, so the failure is logged by exception type or reason code and the request goes on. `NodeSelectionRecord` is the one parser for the runner and the run view.
- **Review lenses.** `NodeSelectionRunner` sits outside `ReviewLensRunner`, so the three lens passes share one decision
  and one cached prompt prefix. `Thalos:Decisions:PinnedLensModels` maps a lens name (`correctness`, `falsifiability` or
  `mechanism`, lower case) to a model its pass runs on whatever the decision; the other passes keep the node's decision.
  A pin applies only while decisions are on, so with them off a review is today's review. Start-up
  refuses a pin that is not a lens, or not in `AllowedModels` (or the default model, when known) of the agent that
  runs the review node **under the current squad setting**: the reviewer when the squad is on, the fallback agent
  when it is off. Thalos checks the model again on the turn.
- **Replaying a review.** `processes/review-replay.yaml` has one `review` node and ends. Start it with
  `POST /api/workflow-runs`, REST only, with `process` `review-replay` and `baseBranch` `manufacture/<run-id>`, exactly
  that form. `baseBranch` is refused for any other process and required for this one. A replay whose original run
  worked on another repository than the requested `repository` is refused as 400 before any worktree: the original's
  workspace names its repository while it has one, and a published run's pull request link names it after that. A run
  with neither is not compared, and a branch the requested remote lacks fails the workspace. The reviewer starts from the
  `files_touched` the original run recorded, snapshotted at start. There is no implement, gate or publish, so a replay
  opens no pull request. It exists to compare reviewer setups on identical input.
- **Reading a decision.** `GET /api/workflow-runs/{id}` lists `nodeSelections` in seq order: seq, node, model, level,
  kept families, fallback and confidence. The tool filter and the answers are in the record but not in the view. A
  record that cannot be read is not listed, and `nodeSelectionsError` names its seq. The view's model is the node's: a
  review lens pinned to another model ran on that model, which the view does not show. The node's `node-usage` record
  holds the per-lens models, one `byModel` part per model, as cost analytics reads them.
- **The answers.** When Minos answered, the record's `answers` holds `question_set` (`NodeQuestions.Version`), `demand`
  and `demand_confidence`, and `families`, each tool family's probability in question order, whether or not the answer
  was taken. They are there to tune `ModelConfidence` and `ToolFamilyProbability` from real runs. Family names and
  numbers only, never task text. `NodeQuestionsShapeTests` pins `Version` to a digest of the questions, so a changed
  question fails until the version is raised. A record without `answers`, written before they existed or for a visit
  nothing was asked about, still reads.

### Web: signed-out visitors and the page set

`Home` renders `NeedsMePanel` for a signed-in person and `SignedOutLanding` otherwise; the landing page asks the API for
nothing. `App.razor` sends every other signed-out request to `RedirectToLogin`. `MainLayout` shows no menu and no toggle
signed out, and reads the sign-in before it draws the chrome. A request whose session has ended returns
`SessionExpiry.SigningInAgain`; the layout shows it once as an Info notification, and pages do not show it as an error.
The menu is in `MainLayout.razor`: Home, Tasks, Runs, Projects; PRD Generator, Agent; Operations with Schedules and
Costs; Admin with Git Repositories, behind the `admin` role. There is no Executions page.

A run's page reads `failureReason` and `reviewVisits` from the run view, and its list items carry `failureReason` and
`prUrl`, read per row from the run's variables (issue #367). `GET /api/workflow-runs` takes `signal` and `updatedSince`,
an ISO 8601 timestamp with its zone. The store cannot filter on either, so the controller scans up to 500 summaries
(`RunListLimits.ScanCap`, issue #367).
`POST /api/tasks` takes an optional `prompt`, and `TasksController` answers its refusals as `application/problem+json`;
the other controllers still differ (issue #366).

### Costs and history

- After each agent node completes with usage, the host appends a `node-usage`
  workflow run record (`NodeUsageRecorder`). `NodeUsageBackfill` fills the
  records missing for older runs at host startup. It is not an EF migration, and
  it writes nothing for a (run, seq) that already has a record.
- A node can run on more than one model: a review whose lens is pinned
  (`Thalos:Decisions:PinnedLensModels`) runs that pass on the pinned model and
  the others on the node's own. Thalos sums the passes into one usage that keeps
  the first pass's model, so `ReviewLensRunner` hands each pass's usage to
  `NodeUsageRecorder.HoldPasses`, and the completion's record then carries
  `byModel`, one part per model, summing to the record's totals. Cost analytics
  prices each part at its own model's rate and counts the record once, under its
  first part; the token counter counts each model's own tokens. A record without
  `byModel` is one part, so older records read as before. What is held is in
  memory until the completion takes it, and is released when no completion will:
  the run fails or is cancelled (the recorder observes the store), or the
  completion's write throws. A review the backfill records, after a crash or a
  failed append, is priced at its first model.
- Cost analytics reads four sources: `TaskExecutions` history, `node-usage`
  records (manufacture), chat sessions and scheduled sessions. Sessions owned by
  `workflow:*` are a manufacture run's node turns and are excluded from chat, so
  nothing counts twice. Session sources carry no model or cache split.
- A usage group's input tokens include its cache reads and writes. `CostAnalyticsService.PriceTokens` prices the
  uncached remainder at the input rate, reads at `ModelPricing.EffectiveCacheReadTokenPricePerMillion` and writes at
  `EffectiveCacheWriteTokenPricePerMillion`. Unset, those are 0.1 and 1.25 times the input price, defined only in
  `ModelPricing`; `CacheReadTokenPricePerMillion` and `CacheWriteTokenPricePerMillion` override them.
  `ModelPricingConfigurationValidator` refuses negative prices at host start. `EstimateCostAsync` has no cache split
  and stays at the input rate.
  The `by-session` cost endpoint was removed; `estimate` stays.
- A `node-selection` run record (`WorkflowRunRecord.NodeSelectionKind`) holds the model and tool decision of one agent
  node visit, with the payload `{ level, model, tool_filter, kept_families, fallback, confidence, answers }`, `answers`
  only when Minos answered. Unlike telemetry it
  holds tool names, because a run record is the run's own audit. It carries no cost. The model it names is the node's
  own; a lens pinned to another model ran on that one, and `node-usage` prices each model's tokens separately, see
  above. See "Node selection".
- A `review-response` run record (`WorkflowRunRecord.ReviewResponseKind`) holds the `review_response` one `implement`
  visit reported, at that visit's seq, with the payload `{ response }`. `ReviewHandoffWorkflowStore` writes it before the
  completion, but only while the completion is acceptable, the run still `Running` at that seq: a completion the inner
  store would refuse records nothing. A write failure fails the run. The run variable is overwritten by each visit; the
  record keeps every answer. The run view's `reviewVisits` pairs each rejected review visit with the next implement
  visit's record. The reviewer's dispatch never carries it.
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
# Run everything via .NET Aspire: Postgres, Keycloak, Ollama, migrations,
# the daedalus-sandbox:dev image build, API and Web
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

`global.json` pins SDK 10.0.402 with `rollForward: disable`. The sandbox, API and Web build stages, and the sandbox
runtime stage, use the exact tag `mcr.microsoft.com/dotnet/sdk:10.0.402` or `10.0.402-alpine3.24`, and each build stage
copies `global.json`, so an image that drifts from the pin refuses to build.

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

## Native AOT

Phase 3.1, the start of milestone 3. Every target's trim and AOT warnings are counted and ratcheted in CI, the
sandbox host ships as a native binary, and a harness measures start-up, memory and size. Development, the AppHost and
`dotnet test` stay JIT; native is the CI publish of each target and the images built from it. Rulings and numbers:
`docs/plans/2026-10-17-phase-3.1-rulings.md`.

### The toolchain

- **clang and the zlib headers** link the native binaries: `sudo apt-get install -y clang zlib1g-dev` on Debian or
  Ubuntu. The agent VM has had clang 19.1.7, zlib1g-dev and the `wasm-tools` workload since 2026-10-17.
- **The `wasm-tools` workload** compiles the Web app with WebAssembly AOT: `dotnet workload install wasm-tools`.
- **The SDK** is pinned exactly: `global.json` says 10.0.402 with `rollForward: disable`, CI installs it with
  `global-json-file`, and the Dockerfiles use the `sdk:10.0.402` tags. A servicing release can move a warning count,
  and the two-way ratchet would then turn `aot-analysis` red with no code change, so a bump is deliberate. Renovate
  sends it as one PR that moves `global.json` and the Docker tags together; that PR re-runs
  `eng/aot/count-warnings.sh --all --write` and commits any baseline change with its cause in the message.
- **jq,** which the counter uses to read and write the baseline.

CI installs clang and zlib1g-dev with apt in the `aot-analysis` job, and the workload for the Web leg only.

### Counting the warnings

`eng/aot/count-warnings.sh` is the one implementation, used by CI and locally alike, so the CI number is reproduced
with one command:

```bash
eng/aot/count-warnings.sh                        # every target, print the counts
eng/aot/count-warnings.sh --target Sandbox       # one of Api, Cli, Migrations, Sandbox, Web
eng/aot/count-warnings.sh --target Cli --check   # what CI runs: exit 1 if the count differs from the baseline
eng/aot/count-warnings.sh --target Cli --write   # record the count in the baseline, keeping the other targets
```

- **Output:** `<Target> <count>` per target, then the top five root causes, grouped by source project or namespace.
  The grouping is approximate; the count is exact.
- **Exit codes:** 0 when everything matches, 1 for a ratchet failure, 2 for a publish failure, an analysis that did
  not run, or bad usage.
  `--check` and `--write` are exclusive.
- **A publish that fails** for any reason other than warnings, by its exit code or an error line, is exit 2. It is
  never counted as 0.
- **An analysis that did not run** is exit 2 too. A native log must hold `Generating native code`; the Web log must
  hold `Optimizing assemblies` and `AOT'ing`. Every target is published with `SuppressTrimAnalysisWarnings=false` on the
  command line, so a project setting cannot hide the warnings. A 0 is only a baseline when the analysis ran.
- **Environment:** `AOT_BASELINE` overrides `eng/aot/baseline.json`, and `AOT_LOG_DIR` the directory that keeps each
  target's publish log and its normalised `<Target>.keys`, by default `$TMPDIR/daedalus-aot`. Diffing two `.keys`
  files shows which warnings came or went.
- **What it publishes:** `dotnet publish <project> -c Release -r linux-x64 -p:PublishAot=true` with warnings-as-errors
  turned off, `-p:TrimmerSingleWarn=false` so a package reports per method, and `-tl:off`. The Web app publishes with
  `-p:RunAOTCompilation=true -p:PublishTrimmed=true -p:SuppressTrimAnalysisWarnings=false` instead: WebAssembly AOT
  refuses to run untrimmed, and the project sets `PublishTrimmed=false` for its normal publish.
- **Each publish gets a fresh `--artifacts-path`.** An incremental publish skips ILC and prints no warnings, which
  would count 0.
- **Distinct** means IL2xxx and IL3xxx deduplicated by `code|file|line|member`. The file is repo-relative and the line
  drops its column, so ILC's, ILLink's and the Roslyn analyzer's reports of one site are one warning. A warning
  without a location, as ILC prints package code, keeps `Origin(): message`, because only the member used tells
  `MapGet` from `MapPost` in the same method.
- **Time,** on the 12-core agent VM: phase 3.1 Task 1 measured about 28 minutes for `--all`, and 21 min 23 s for
  the Api publish by its log timestamps, with ILC near 12 GB resident. The native smoke run's Api publish took
  22 min 3 s, Cli 90 s and Migrations 47 s (`docs/plans/2026-10-17-phase-3.1-native-smoke.md`).
- **Its own test:** `eng/aot/tests/test-count-warnings.sh` replays `sample-ilc.log` through a fake `dotnet`, so it
  needs no SDK. CI runs it in `build-and-test`.

### The ratchet

`eng/aot/baseline.json` holds one integer per target. The `aot-analysis` job runs `--check` for each of the five
targets on every PR and push to `main`, and fails on any difference, **above or below** the baseline:

- **A count that rises** is a new trim or AOT problem. Fix it at its source. Do not raise the baseline to make the job
  pass; a rise that cannot be avoided, such as a dependency floor another package forces, is recorded in the change's
  rulings with its cause and the phase that removes it.
- **A count that falls** is progress, and the change that makes it must record it, so the file always states the
  truth and no later change can give the gain back unnoticed.

To lower a baseline after fixing warnings:

1. Run `eng/aot/count-warnings.sh --target <Name> --write` for each target the change affects.
2. Commit `eng/aot/baseline.json` in the same change.
3. Confirm with `eng/aot/count-warnings.sh --target <Name> --check`, which must exit 0.

A dependency bump can move a count either way, and its PR records the new number the same way. The job needs no
secrets, so forks and Dependabot run it. The owner makes it a required check once it is green; until then, treat a
red `aot-analysis` as blocking.

The baseline after phase 3.1: Api 18591, Cli 776, Migrations 789, Sandbox 0, Web 214. Most of the Api's count is
the Anthropic SDK's reflection-based JSON, which phase 3.3 removes.

### No suppressions

A trim or AOT warning is fixed at its source, or the target does not ship native. Not allowed:

- `UnconditionalSuppressMessage`;
- `<NoWarn>` for IL2xxx or IL3xxx;
- `SuppressTrimAnalysisWarnings=true`;
- a `[RequiresUnreferencedCode]` or `[RequiresDynamicCode]` moved to another member. That moves the warning to the
  caller and fixes nothing.

A library we own is fixed upstream and released, as Thalos 0.18.0 was for the sandbox host. A third-party library
gets an upstream issue, and is replaced if an AOT-clean alternative exists. The milestone design allows a
suppression only where the code is provably safe, with the reason beside it, listed in the phase's rulings.

A clean publish is not proof that a binary works: every native target is proven by running it.

### The native sandbox host

`Daedalus.Sandbox` publishes with `PublishAot=true` and `TrimMode=full`. Its Dockerfile's build stage installs clang
and zlib1g-dev, restores and publishes for `linux-x64`, and drops the `.dbg` symbols; the runtime stage stays
`sdk:10.0.402`, because runs build and test inside it, with the native `/app/Daedalus.Sandbox` as its entrypoint. CI's
`docker` job builds the image on every PR and never pushes it. The integration suite's sandbox tests run against the
native image, including a build and a Roslyn call through it.

### Measuring start-up, memory and size

`eng/measure/` holds `measure-api.sh`, `measure-sandbox.sh` and `measure-cli.sh`. Each takes an image, or the CLI's
binary, and `--runs N`, 10 by default, and prints one JSON line: the start-up median and p90 over cold starts, idle and
peak resident memory, and image and app size. Containers run with `--cpus 2`, and the CLI under `taskset` on two
cores. The API and CLI scripts start their own Postgres and Keycloak. `eng/measure/README.md` defines every field and
what "ready" means for each target.

The JIT baseline, from `main` `8173fd55`, is in `docs/plans/2026-10-17-phase-3.1-aot-baseline.md`: run from a scratch
worktree at the commit the images were built from, two passes of 10, with the variance between them. Phase 3.6
measures the native build on the same VM the same way. Start-up varies by about 5 % at the median between passes, so
a smaller difference is noise.

## Token budget

Phase 2.15. Every workflow turn runs under a token ceiling of its own agent's, from `Thalos:Workflow:TurnBudgets`:
`Default` for an agent not listed, and an `Agents` entry per role. `BudgetedSubagentRunner` stamps the ceiling and the
deadline on each turn's request. Both hosts ship the same section, and `TurnBudgetDriftTests` pins that, that each
squad role has an entry, and that no ceiling sits below its role's measured maximum.

### The counting rule

A turn is stopped before the model call that would start with the turn's total at or above the ceiling. The total is
every token, input plus output. Cache reads count at full weight, though they are billed at a tenth; on implement they
are about 85-90% of input. A turn can therefore end above the ceiling by its last call. A stopped turn fails its node,
and its spend still shows on `/costs`, because the failed node's usage is recorded.

`DetachedRuns:MaxTotalTokens` is the scout's ceiling and nothing else. It bounds the scout and detached runs only.
`DetachedRuns:DeadlineSeconds` still applies to workflow turns.

### The sizing rule

Ceiling = the larger of 1.5 x the measured maximum and 2 x p90, over all completed turns of the agent, rounded up to
the next 25,000. The rule leaves room for a heavier task than the sample, without being so loose that a runaway turn
spends a run's worth before it stops. A turn that a ceiling stopped is a lower bound and is left out of the maxima.

### The query

Run against the dev database. Owner `workflow:manufacture:%` is every manufacture run. A turn that failed leaves its
session's totals at 0, so the last condition keeps only turns that ran; without it, a failed turn counts as a turn and
pulls the p90 down.

```sql
select case "AgentId" when 'd689abd7-72dd-4cd7-8a17-611bf5530213' then 'implementer'
                      when '6221a3bd-4f5c-483b-b2ff-36a2aba07aef' then 'reviewer' else "AgentId"::text end agent,
       count(*) turns,
       percentile_cont(0.9) within group (order by "TotalInputTokens" + "TotalOutputTokens")::bigint p90,
       max("TotalInputTokens" + "TotalOutputTokens") max,
       max(extract(epoch from "LastActivityAt" - "CreatedAt"))::int max_seconds
from "AgentSessions"
where "OwnerId" like 'workflow:manufacture:%' and "TotalInputTokens" + "TotalOutputTokens" > 0
group by 1;
```

### The sample

| Sample | Turns | Implementer | Reviewer |
|---|---|---|---|
| `daedalus-sandbox`, runs of 2026-10-14/15 | 74 completed | 18 turns, p90 437,682, max 501,172 | 56 turns, p90 270,197, max 392,852 |
| `MarcelRoozekrans/Daedalus.NET`, 2026-10-17, branch `phase/2.15-token-budget` | 13 completed | 325,872, 254,227, 1,918,692, 296,052, and 609,040 and 1,122,644 from runs whose sandbox build was broken | Lens passes 58,192, 936,414, 1,390,204, 1,249,792, 1,917,540, 527,145 and 589,686 |

That is 87 completed turns, 74 + 6 + 7. The sandbox runs also left one implementer session at 0 tokens, a failed turn
the query above leaves out. Four more turns were stopped by a ceiling and give only lower bounds: 990,493
at 875,000, and 2,107,208, 2,122,253 and 2,107,995 at a temporary 2,000,000. All four were in runs whose sandbox build
was broken, which the phase's build fix corrected. A small sandbox repository understated what a real one costs by
four to five times, 3.8 times for the implementer's maximum and 4.9 times for the reviewer's, which is why the second
sample exists.

### The numbers

| Agent | Measured maximum | 1.5 x max | 2 x p90 | Ceiling |
|---|---|---|---|---|
| implementer | 1,918,692 | 2,878,038 | 1,153,360 | **2,900,000** |
| reviewer | 1,917,540 | 2,876,310 | 753,658 | **2,900,000** |
| `Default` | | | | **2,900,000** |

The p90 is over the union of both samples: 24 implementer turns, p90 576,680, and 63 reviewer turns, p90 376,829, as
`percentile_cont(0.9)` computes it, rounded to whole tokens. 2 x p90 is well below 1.5 x max for both, so the maximum
decides both ceilings.

A turn stopped at 2,900,000 costs roughly $2.50-3.00 at current Opus 5.5 rates with typical caching. The whole
measurement cost $13.00 on `/costs`, failed nodes included.

### The deadline

`DetachedRuns:DeadlineSeconds` is 600, up from 300. The slowest measured implement turn took 313 s of session time, and
others 274 s and 240 s; reviewer passes took up to 244 s. A turn that outlives its deadline is a stranded run unless the
dispatch lease outlasts it, so `Thalos:Workflow:Dispatch:LeaseDuration` is 25 minutes: it must exceed the deadline plus
the 10 minute Roslyn readiness wait. The channel and scheduling outbox lease of 20 minutes must exceed the deadline too.

### How to resize

After a change of process, prompts or model, or when a turn is stopped that the work needed:

1. Rerun the query above on runs of the new setup, and merge its maxima and p90 with the samples that still apply.
2. Apply the sizing rule per agent.
3. Set the new values in `Daedalus.Api/appsettings.json` and `Daedalus.Cli/appsettings.json`, identical in both.
4. Update `MeasuredMaximum` in `TurnBudgetDriftTests` to the new maxima, and its comment to name the samples and dates.
5. Check the deadline against the slowest turn's `max_seconds`, with the lease above it.

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
and `src/Daedalus.Web/Program.cs`.
`InfrastructureServiceExtensions` holds one configuration, `ConfigureResilience`, that every GitHub and Azure DevOps
client starts from:

```csharp
private static void ConfigureResilience(HttpStandardResilienceOptions options)
{
    options.Retry.MaxRetryAttempts = 3;
    options.Retry.BackoffType = DelayBackoffType.Exponential;
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
}
```

**Never retry a write.** A POST, PATCH, PUT or DELETE to an API without idempotency keys, which is GitHub's and Azure
DevOps's, goes out once: a retried comment, issue or pull request lands twice. A client whose calls are writes gets
`NeverRetry` on top, which keeps the timeouts and circuit breaker and sets a retry predicate that handles nothing. Do not
use `DisableForUnsafeHttpMethods` or `GetRequestMessage`: both are `[Experimental("EXTEXP0001")]`. A facade that both
reads and writes, as `GitHubApi` does, uses two named clients. A read client against a rate-limited API retries what
`HttpClientResiliencePredicates.IsTransient` calls transient except a 429, as `RetryReadsExceptRateLimits` does. The
Web's `Daedalus.Api` client in `src/Daedalus.Web/Program.cs` does not follow this rule yet: see #340.

Do not add the raw `Polly` package or hand-roll `HttpPolicyExtensions` / `PolicyWrap`. Polly's own rejections,
`TimeoutRejectedException` and the other `ExecutionRejectedException`s, are not HTTP failures to a generated client, so
the facade catches them and returns a failed result.

## External HTTP APIs

An external REST API, as GitHub and Azure DevOps are, is a ZeroAlloc.Rest client behind a facade:

- **An internal `[ZeroAllocRestClient(MaxErrorBodyBytes = ...)]` interface**, one method per endpoint, with relative
  routes and no leading slash, so a base address's own path is kept. Read no more error body than you use: `0` for Azure
  DevOps, whose client decodes and logs none, and 2 KiB for GitHub, whose body is summarised for the agent and read to
  recognise a secondary rate limit. A
  limit of `0` means the application reads no body; do not take it to mean that no bytes are transferred.
- **A source-generated `JsonSerializerContext`** over DTOs whose fields are all nullable, passed as
  `new SystemTextJsonSerializer(Context.Default)`. No reflection serialization and no IL2026 suppression.
- **A typed error and an `[ErrorMapper]`**, as `GitHubError` and `GitHubErrorMapper`. The message is built from the
  kind, the status and the retry time, never from a body or an exception's message, because it reaches logs, run errors
  and span status. What the API said goes into a separate, bounded field, `GitHubSaid`, that only agent tools render
  through `ForAgent()`. A rate limit always carries a retry time: `Retry-After`, else `X-RateLimit-Reset` when
  `X-RateLimit-Remaining` is 0, else a minute from now. `ForWrite()` adds "it may still have been applied" to the kinds
  where a write may have landed: timeout, server, transport and unreadable.
- **Credentials per call**, as a `[Header("Authorization")]` parameter. Never set a token on a client's
  `DefaultRequestHeaders`: an `HttpClient` can be shared, and two calls at once would send each other's token.
- **The facade** owns everything that is not HTTP, such as paging, required fields and which failure means "not there".
  It creates its clients from `IHttpClientFactory` and sets their `BaseAddress` before anything is sent.
- **Handlers** for what the generated client cannot see, such as success headers. GitHub's read client carries
  `GitHubConditionalCacheHandler` outside the resilience handler and `GitHubRateLimitObserver` inside it. The observer's
  tag, `daedalus.github.resource`, comes from an allow-list of GitHub's documented resource names, so an unexpected
  header cannot create unbounded series: any other value is `other`, and a missing or blank one is `unknown`.
- **Nothing identifying in what a run can see.** On the publish path, `ThalosPullRequestPublisher`,
  `GitHubPullRequestFactory`, `PullRequestFactory` and `RepositoryAuthenticationProvider`, the error paths and the
  texts a run shows carry no URL, repository slug, response body or exception message. A log of a failure carries an
  exception's type name only. The Information-level success confirmations do carry the pull request's web URL.

**Testing.** A generated client disposes its request when the call returns, so a test handler reads the body when the
request is sent, as `StubHandler` and `RecordingHandler` do. `GitHubHost` builds the real `AddGitHubApi` pipelines over a
test handler with zero retry delays; a standard pipeline's options are named `{client}-standard`.

## Observability

Telemetry goes over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; the README's "Observability" section lists the
sources, spans and metrics. This section is how to add a span.

**A span on a seam.** Put `[Instrument(ManufactureTelemetry.SourceName)]` on the interface. Every `[Trace]` sets
`ExceptionDescription = false`, because the default copies an escaping exception's message into the span. On a method
that returns a `Result`, also set `ErrorWhen = "IsFailure"`, so a failure marks the span Error without copying its
message. Tag ids with `[TraceTag(Name)]` on a parameter, or `[TraceTagFromResult(Name, "Value", When = "IsSuccess")]` for
the value of a `Result`. Without `When`, the proxy reads `Value` on a failure, and that throws. See
`IManufactureRunStarter` and `IWorkflowRunCanceller` in `src/Daedalus.Agents/Workflow/`.

**Registering.** The generator emits an `XxxInstrumented` proxy. Register the proxy next to the implementation, as
`DaedalusAgentsServiceCollectionExtensions` does for `ManufactureRunStarterInstrumented`. Never hand-wrap a proxy to set
status: use `ErrorWhen`.

**No seam.** Use `Activity.Current?.SetTag` only where there is no seam to attribute, as `TaskManufactureService` does
for `daedalus.task.id` on the request span. `ManufactureRunStarter` tags `daedalus.repository`, a value known only inside
the implementation of a seam. Inside an implementation, tag only after checking that `Activity.Current` is your own span,
by operation name and source, as `TagStartSpan` does. Otherwise an unsampled span leaves the tag on the caller's span.

**Names.** Declare every span, tag and metric name once, in the telemetry class of its source: `ManufactureTelemetry`
for `daedalus.manufacture`, `DecisionTelemetry` for `daedalus.decisions`. Names Thalos owns, such as the node and run id
tags, are aliased from `WorkflowTelemetry`, so they cannot drift.

**Testing.** Use `Thalos.Testing.TelemetryCapture`, and filter on a value only the test produces, such as a fresh run id:
the capture sees every span in the process, so never assert an unfiltered count. For the host's own pipeline use the
OpenTelemetry in-memory exporter, with a locked collection read through one snapshot: the exporter adds without a
lock, and it also sees every other host's spans in the process, so filter there too and never count.
`TelemetryEndToEndTests` shows both.

**Decisions.** `NodeSelectionDecider` is the only seam for the per-node decision, on the source and meter
`daedalus.decisions` (`DecisionTelemetry`). One span `daedalus.node_selection` per decided visit, with these tags:
`thalos.node`, `daedalus.role`, `daedalus.decision.level`, `daedalus.decision.kept_families` (a count),
`daedalus.decision.model` (the chosen model; not `gen_ai.request.model`, which names the model an inference span
calls), `daedalus.decision.confidence` (only when Minos answered, so absent for `off`, `timeout` and
`error:*`) and `daedalus.decision.fallback` (absent when the decision was taken). The counter `daedalus.decisions`
(`{decision}`) is tagged by `daedalus.decision.fallback`, with `none` for a taken decision. Never task text, tool names
or a `DecisionError` message, which can quote the request. Minos's own spans and metrics use the source and meter
`Minos`, which the service defaults subscribe to beside `daedalus.*`.

**No content.** A tag, span name, status description, event or metric tag never carries prompt or agent text, task titles
or descriptions, PR text, file content, credentials or token values. Outgoing HTTP spans export their origin only.
Exported logs are not yet covered by this rule (Daedalus#334).

## Web state with ZeroAlloc.Flux

Every page of the Web app keeps its state in a ZeroAlloc.Flux store. Runs and Tasks moved in phase 2.12; phase 2.14
moved the rest: Home, Projects, Git Repositories, Costs, Schedules, the PRD generator, Brainstorm and Agent. The
decisions behind the rules below are in `docs/plans/2026-10-16-phase-2.14-rulings.md`.

| Feature | Read by | Lifetime |
|---|---|---|
| `RunsState` | Runs, a run's page, the Runs menu count, the live indicator | the list and the open run are page-only, cleared by `RunListClosed` and `RunClosed`; the count and the hub status live as long as the app |
| `TasksState` | Tasks | page-only, `TasksClosed` |
| `ProjectsState` | Projects, Tasks, the task create dialog, Home's New task, the PRD project step | shared, never cleared on close |
| `HomeState` | Home's `NeedsMePanel` | page-only, `HomeClosed` |
| `RepositoriesState` | Git Repositories | page-only, `RepositoriesClosed` |
| `CostsState` | Costs | page-only, `CostsClosed` |
| `SchedulesState` | Schedules | page-only, `SchedulesClosed` |
| `PrdState` | PRD Generator | page-only, `PrdClosed`, and `PrdReset` on Start over |
| `BrainstormState` | `/brainstorm/{id}` | page-only, `BrainstormClosed` |
| `AgentState` | Agent | page-only, `AgentClosed` |

### The pattern

- **Features** live in `Daedalus.Web/State`, one folder per feature. Each state is a `readonly partial record struct`
  marked `[Feature(InitialState = nameof(Initial))]`, because the default state leaves collections `null`. The record's
  summary states its lifetime.
- **Actions** are `readonly record struct`s in the feature's `…Actions.cs`.
- **Reducers** are pure static `On` methods marked `[Reducer]`. A reducer for an answer compares it with the request it
  answers and ignores a stale one.
- **Effects** are scoped services, registered together in one block in `Program.cs`. They inject their typed clients,
  `IDispatcher`, the stores they read and a logger: `ProjectsEffects` takes two clients, `ApiClient` for reads and
  `IProjectApiClient` for writes, and `HomeEffects` also takes a `TimeProvider` for the cards' read time. They take no
  `NotificationService`, `DialogService` or `NavigationManager`, so every effect can be unit-tested without a renderer.
  Most dispatch every outcome of a read as an action. Brainstorm and Agent store a read's answer but not its failure:
  they return the failure as an outcome, `BrainstormOutcome` or `AgentOutcome`, for the page to toast. No effect throws
  for an expected failure.
- **Components** inherit `FluxComponent`, override `SubscribeStores()` and call `Subscribe`. One that also overrides
  `OnInitialized`, such as Tasks, `TaskCreateDialog` and `PrdGenerator`, calls `base.OnInitialized()` first. A page
  dispatches its `…Closed` action from `DisposeAsync`, after it unsubscribes, so a live change does not re-fetch what
  is off screen.
- **View helpers** are pure static classes beside the state, such as `ProjectsView`, `RepositoriesView`,
  `SchedulesView`, `AgentView` and `TasksView`. A decision a component would otherwise make in markup, such as whether
  to show the spinner or which notification a write gets, goes there, so it is unit-tested without a renderer.

### Rules

- **Typed clients only.** No component injects `HttpClient`. Pages and effects call `ApiClient`, `AgentApiClient`,
  `IProjectApiClient` or `RepositoriesApiClient`, and the Web binds the Application DTOs directly; it keeps no copies.
  `WebLayer_KeepsNoCopyOf_AnApplicationDto` in `Daedalus.Tests.Unit`'s `CleanArchitectureTests` guards the second.
- **The clients turn Polly's rejections into a failed `Result`.** The resilience handler throws
  `ExecutionRejectedException` when its circuit is open or its timeout fires. `AgentApiClient`, `RepositoriesApiClient`,
  `ProjectApiClient` and `ApiClient`'s writes catch it by name beside `HttpRequestException`. `ApiClient`'s reads, all
  through its `GetAsync`, still end in a catch-all: a rejection, or a body it cannot read such as a `JsonException`,
  returns a failed `Result` that reads "Unexpected error: …". Narrowing it changes failure text that tests pin, so it
  is tracked on issue #383, as is the Agent stream, which reports a rejection with the same old catch-all text.
  `ProjectApiClient` also catches a cancellation and a body it cannot write or read, and lets any other exception
  through as a defect. No page needs a catch-all.
- **A cancelled caller dispatches nothing.** An effect checks its token before its first dispatch. A page already gone
  would otherwise clear what a page opened since shows, or move the request sequence past its answer. A failure that
  comes back after the token was cancelled stores no error. Only Projects ends the load for it, with
  `ProjectsLoadAbandoned`, because `ProjectsState` has no `…Closed` to end it. Every other feature returns without a
  dispatch and relies on its page's `…Closed`, which already ended the load: `RunListClosed`, `RunClosed` and
  `TasksClosed` for Runs and Tasks, and each 2.14 page's own. Git Repositories, Costs, Schedules, PRD, Brainstorm and
  Agent drop a success that comes back after the cancellation the same way. Home dispatches it, and its reducers drop
  it: `HomeClosed` has marked Home closed, or a newer read has taken the card's number. `AgentApiClient`'s reads let a
  cancellation through as an `OperationCanceledException` instead of a failed `Result`; Blazor ignores a cancelled
  lifecycle or event task.
- **A request sequence drops stale answers.** A read's start takes the next sequence number and dispatches it, with no
  await between the read of the number and the dispatch. An answer with an older number is ignored, so an earlier
  reader's late answer never replaces a later one. A feature's `…Closed` keeps the number, and `PrdClosed` and
  `PrdReset` advance it, so an answer to a read made before the close never matches one made after it. The 2.12
  features compare an answer with the query or id it answers instead, and drop one that lands after the close by the
  `ListRequested` flag on `RunsState` and the `LoadRequested` flag on `TasksState`.
- **Started clears the last error.** A load's start clears the feature's last error, so a failure from another page
  never shows under a list that is being read again.
- **`SessionExpiry.SigningInAgain` is never stored.** The layout already says "Signing in again", so an effect that
  sees it, through `SessionExpiry.IsSigningIn`, stores no error and returns no outcome. Runs and Tasks end the load
  with an `…Abandoned` action instead; Tasks marks the board unread, so `TasksView.ShowEmpty` claims no empty board.
- **Failures are logged.** A failure an effect stores, or a background one it drops, is logged as a warning
  through a `[LoggerMessage]` method, with the error text only: never the request, a prompt, a message or a PRD. A
  refused write is logged the same way, unless the session is being renewed or the caller is gone.
- **Writes return their outcome.** A write effect returns its outcome, such as `ProjectSave`, `RepositorySave`,
  `ScheduleResend`, `BrainstormOutcome` or `AgentOutcome`, to the component that asked, and that component shows the
  toast through `FailureNotices`. There is no last-outcome state, so a component never shows another write's outcome.
  `null` means no outcome: the session is being renewed, or the caller is gone.
- **The spinner is only for a load the component started.** A component keeps a flag for its own load and asks a pure
  view helper, such as `ProjectsView.ShowSpinner`, whether to show the spinner. A background refresh, from the hub, a
  reconnect or another page, renders silently over the rows already shown.

### `ProjectsState`, the shared feature

`ProjectsState` is never cleared when a page closes. Blazor disposes the page being left after it initialises the next
one, so a clear from the old page would land after the new page asked for the projects. Instead every reader re-fetches
the list when it opens, and the request sequence drops an older reader's late answer. What a closed page left behind
shows at most until that re-fetch lands. Two derived views say what a reader may use:

- `Settled` is the list once the newest load finished without an error, and `null` otherwise. A decision made once,
  such as the create dialog's one-project preselect, waits for it.
- `Current` is the list a reader shows outside a load of its own: the held list during a background refresh, and
  `null` once a load has failed, so the failure shows as before.

`Selected`, `SelectedId` and `SelectError` are the Projects page's own and are cleared by `ProjectsPageClosed`, which
leaves the list alone.

### Home's live updates

Home's Awaiting, Running and Failed cards re-fetch on each `RunChanged` and on reconnect, while Home is open; the Recent
pull requests card is read only on open and on Retry. `HomeEffects` returns at once from a change: SignalR runs its
handlers one at a time and waits for each, so a handler that waited for the re-fetch would hold back every change behind
it. Changes that arrive while a re-fetch is in flight are coalesced into one more round after it. A background
re-fetch that fails keeps the card's rows and logs a warning; only a read that showed the skeleton, on open or Retry,
shows the card's error.

### Other conventions

- **Status text** for a run comes from `RunStatusLabels`. Who may see the console comes from `RunAccess` in
  `Daedalus.Web/Auth`: `CanView` is `developer` or `admin`, `CanRetry` is `admin`. `RolesClaimsPrincipalFactory` splits
  Keycloak's `roles` array into role claims so `IsInRole` works.
- **The Agent stream** dispatches one action per event into immutable transcript records. The component throttles its
  renders to one per 50 ms through `AgentView.ShouldRender`.

## Live run updates

A run's page and the Runs list change without a refresh. The path from a store commit to the browser:

1. Thalos calls `IWorkflowRunObserver` after each commit, once the store has released its connection.
   `WorkflowRunChangeBroadcaster` implements it and only queues the change into a bounded channel of 256 entries that
   drops the oldest when full. A slow client never blocks a commit.
2. The same singleton is a hosted service. Its loop reads the channel and sends `WorkflowRunChanged` to every client
   on the hub `WorkflowRunsHub`, mapped at `/hubs/workflow-runs`, as the client method `RunChanged`. The path and the
   method name are shared constants in `WorkflowRunsHubContract` in `Daedalus.Application`. The broadcaster and the
   observer are registered only while `Thalos:Workflow:Enabled` is true.
3. `WorkflowRunChanged` carries exactly `Id`, `Status`, `CurrentNode` and `AwaitingSignal`. It carries no task content,
   variables or error text, because the broadcast goes to every client connected to the hub and is not scoped per
   user. The client uses it only as a signal.
4. `RunHubConnection` in the Web re-fetches what is on screen on each change and again on reconnect, so a dropped
   message is repaired by the next fetch. It uses WebSockets with `SkipNegotiation`. A dropped connection and a first
   start that failed are both tried again on `HubRetryPolicy`'s schedule, at once and then after 2, 5 and 10 seconds,
   then every 30 seconds without end. A failed start of any kind is a Disconnected status, never an exception. The
   Api maps the hub with `CloseOnAuthenticationExpiration`, so a connection closes when its token expires.
5. A send failure is logged by exception type name only, never the message or the exception object.

**The `access_token` rule.** A browser WebSocket cannot set an `Authorization` header, so the hub's bearer token travels
in the `access_token` query value. `HubAccessToken`, wired through `JwtBearerEvents.OnMessageReceived`, reads it from
the query only on paths under `/hubs`. The token must not reach telemetry or logs:
`OpenTelemetry.Instrumentation.AspNetCore` already redacts `url.query` by default, and `ServerQueryRedactionProcessor`
in `Daedalus.ServiceDefaults` covers a host where `OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION`
turns that off. `HostingDiagnosticsLogFilter` holds the hosting "Request starting" line, which prints the query, at
Warning. It is a post-configure on `LoggerFilterOptions`, because a plain `AddFilter` is overridden by
provider-specific configuration.

**One API instance.** The channel and the hub live in one process. A scaled-out API would need a backplane, such as
Redis, so that a change committed on one instance reaches the clients of another. Daedalus has none.

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
