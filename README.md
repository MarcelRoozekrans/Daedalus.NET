# Daedalus

[![CI](https://github.com/MarcelRoozekrans/Daedalus.NET/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/MarcelRoozekrans/Daedalus.NET/actions/workflows/ci.yml)
[![Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/MarcelRoozekrans/883ece1e357faef9d6bdfb459e31fe66/raw/daedalus-coverage.json)](https://github.com/MarcelRoozekrans/Daedalus.NET/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-13.0-239120?logo=csharp)](https://learn.microsoft.com/dotnet/csharp/)
[![Docker](https://img.shields.io/badge/Docker-ghcr.io-2496ED?logo=docker)](https://github.com/MarcelRoozekrans/Daedalus.NET/pkgs/container/)

High-performance .NET 10 application using Railway-Oriented Programming for AI-driven task execution. A person plans work
on a task board and starts a manufacture run for a task; the task's status follows that run. One presentation layer
(Blazor Web + REST API), plus a CLI host, share one Application + Infrastructure stack, orchestrated via .NET Aspire.

**Cost-aware manufacturing.** Before each implement and review step, a cheap [Minos.NET](https://github.com/MarcelRoozekrans/Minos.NET)
decision picks the model and the tool families that step needs, from the set its role allows: a routine change runs on a
small model, a harder one on a stronger model. In the phase 2.10 live comparison, routine tasks cost 72-78% less and four
tasks together about 61% less, with no loss in what review caught. See
[Per-node model and tool selection](#per-node-model-and-tool-selection).

## Table of Contents

- [About the Name](#about-the-name)
- [Architecture Overview](#architecture-overview)
- [Manufacturing a task](#manufacturing-a-task)
- [Tech Stack](#tech-stack)
- [Project Structure](#project-structure)
- [Prerequisites](#prerequisites)
- [Setup](#setup)
- [Running the Application](#running-the-application)
- [Observability](#observability)
- [Configuration](#configuration)
- [Thalos agents](#thalos-agents)
- [Authentication & Keycloak Setup](#authentication--keycloak-setup)
- [Running Tests](#running-tests)
- [Running Benchmarks](#running-benchmarks)
- [Docker Compose (without Aspire)](#docker-compose-without-aspire)
- [Development Guidelines](#development-guidelines)
- [Troubleshooting](#troubleshooting)
- [Documentation](#documentation)

---

## About the Name

In Greek mythology, **Daedalus** was a master craftsman and inventor known for his ingenious skill and creative
problem-solving. He was the architect of the famous Labyrinth on Crete, built to contain the Minotaur. Daedalus is
celebrated for his intelligence, technical mastery, and innovative solutions — from constructing intricate mechanical
devices to designing the wax wings that allowed him and his son Icarus to escape imprisonment. His legacy represents the
pinnacle of craftsmanship, innovation, and the human drive to overcome challenges through ingenuity.

---

## Architecture Overview

Daedalus has **one presentation layer** (Web) and a CLI host, sharing one Application + Infrastructure stack:

| Layer                    | Project                                     | Purpose                                                         |
|--------------------------|---------------------------------------------|-----------------------------------------------------------------|
| **Web** (Blazor WASM)    | `Daedalus.Api` + `Daedalus.Web`             | REST API + browser UI via Radzen components                     |
| **Agents** (Thalos)      | `Daedalus.Agents` (hosted in `Daedalus.Api`) | Thalos.NET agent stack: sessions, tools, AI.Sentinel, SSE chat, Telegram channel |
| **CLI** (Thalos channels)| `Daedalus.Cli`                              | Interactive terminal host for Thalos agents (console channel, no browser) |
| **Shared**               | `Application` + `Infrastructure` + `Domain` | CQRS handlers, EF Core repositories, PostgreSQL persistence     |

The Ralph loop and its `Daedalus.Console` worker were retired in phase 2.8 (October 2026): nothing polls the board, and no
tokens are spent unless a person starts a run.

For detailed diagrams covering component interactions, data flow, Git integration, and the manufacture run, see
[Architecture Diagrams](docs/architecture-diagrams.md) (Mermaid diagrams, including the Thalos agent turn).

---

## Manufacturing a task

A task on the board becomes work only when a person starts a manufacture run for it. There is no automatic pickup.

1. **Create the task.** The task's project must have a `RepositoryUrl` that matches an entry of
   `Thalos:Workflow:Repositories`. For a github.com URL the match is on owner and name, ignoring case. For any other
   remote it is an exact match of the remote, after trimming one trailing `/` and one trailing `.git` from each side.
   Creating and editing a task needs the `task-manager` or `admin` role. A new task needs only a project, a title and a
   description. Priority (0 Low to 3 Critical) and Complexity (0 Low to 2 High) have defaults, and the Prompt, under
   Advanced, is optional: when empty, the description is the prompt (`POST /api/tasks` takes an optional `prompt`).
   The dialog's **Create and manufacture** creates the task and starts its run in one step, then opens the run. It shows
   only for the `developer` or `admin` role, which starting a run needs. If the task is created but the start is
   refused, the page the dialog was opened from, Tasks or Home, says why, using the `detail` of the API's problem. A
   refused `POST /api/tasks` answers `application/problem+json`, so its reason is in `detail`.
2. **Press Manufacture** on the Tasks page (or use Create and manufacture), or call
   `POST /api/tasks/{id}/manufacture`. It needs the `developer` or `admin` role (the `WorkflowResume` policy, as
   `POST /api/workflow-runs`). The run's work intent is the task's title, a blank line, then its description. The answer
   is `201` with `{ "runId": "..." }`; the run id is attached to the task. Starting again later replaces it, and earlier
   runs stay in the workflow run history. Every precondition is checked before the run starts:

   | Status | When |
   |---|---|
   | `404` | The task does not exist. |
   | `422` | The project has no `RepositoryUrl`, it matches no allow-listed entry (the message names it), a dependency is not `Completed`, or a dependency does not exist in the project. |
   | `409` | The task's current run is still `Running` or `Awaiting`. Also when the task changed while the run started: that run is cancelled, and the body carries its `runId` and `runCancelled`. |
   | `400`, `422`, `503` | The run starter refused, as for `POST /api/workflow-runs`. `503` is also the answer while the workflow engine is disabled. |
   | `500` | Reading the task, its project or its dependencies failed (generic text, the cause is logged). Or the run started but attaching it failed: the run is cancelled and the body carries `runId` and `runCancelled`. |

3. **Watch the derived status.** A task stores only its run id, and its status is read from the run each time:

   | Run | Task status |
   |---|---|
   | Running | `InProgress` (1) |
   | Awaiting the human gate | `AwaitingApproval` (5) |
   | Succeeded | `Completed` (2) |
   | Failed | `Failed` (3) |
   | Cancelled | `Cancelled` (6) |

   5 and 6 are never stored: the database check constraint allows 0 to 4. A task without a run keeps its stored status,
   so a task from the Ralph era keeps `Completed`, `Failed` or `Abandoned`. When the workflow engine is disabled on a
   host, the run cannot be read, and the stored status shows. While a task's run is live, updating or deleting the task
   answers `409`. The Manufacture button shows for `Pending`, `Failed`, `Abandoned` and `Cancelled` tasks.
4. **Approve at the gate.** The run waits for `human_approval`. Open it in the console: the **Runs** item lists the runs
   and shows a count of those awaiting approval. A run's page shows the timeline, the review, the usage and the writes.
   A `Failed` run says why in the alert at the top of its page (`failureReason`), such as the review limit and the lens
   that rejected last. The Review tab lists every review visit, newest first, as Approved, Rejected or Unreadable, each
   lens's checked list and findings, and for a rejected visit "How implement answered", the `review_response` the next
   implement visit reported. Home also lists the runs awaiting approval.
   Its gate panel lets a developer or admin approve, drop findings, or cancel the run. The page updates live, with no
   refresh. A script can do the same with `POST /api/workflow-runs/{runId}/resume` and the body
   `{ "signal": "human_approval", "payload": null }`; `applyStandingInstructions` (default `false`) and `dropFindings`
   are optional. This is the workflow run's gate, not the retired task `resume` endpoint.
5. **Follow the pull request.** Once the run publishes, the task shows a link to its pull request (`pullRequestUrl` on
   `GET /api/tasks/{id}`).
6. **Read the costs.** `/costs` and `GET /api/cost-analytics/summary` combine four sources:
   - `history`: `TaskExecutions`, the iterations of the retired loop;
   - `manufacture`: one `node-usage` record per completed agent node of a run, with its model and cache split; a
     review whose pinned lens ran on another model carries one part per model (`byModel`), each priced at its own rate;
   - `chat`: agent sessions owned by a person;
   - `scheduled`: agent sessions owned by `schedule:*`.

   Sessions owned by `workflow:*` are a run's node turns. They are excluded from `chat`, because their `node-usage`
   records already count them. Session sources carry no model and no cache split, so their tokens are not priced.
   Prices are cache-aware, as Anthropic bills prompt caching: a node's input tokens include its cache reads and
   writes, so only the uncached remainder costs the input price, cache reads cost 0.1 times it, and cache writes cost
   1.25 times it, the 5-minute TTL rate. Set `CacheReadTokenPricePerMillion` or `CacheWriteTokenPricePerMillion` on a
   `ModelPricing:Models` entry to override either; a negative price stops the host at start. The `estimate` endpoint
   has no cache split and still prices its prompt tokens at the input rate. The
   host appends each `node-usage` record when a node completes, and a startup step backfills the records of older runs
   (it skips any run and sequence already recorded). The `by-session` cost endpoint no longer exists; `estimate` stays.

### The Web app

The menu groups the pages by the path a task takes. Home, Tasks, Runs and Projects come first. PRD Generator and Agent
follow. Under Operations are Schedules and Costs, and under Admin, shown to admins only, Git Repositories. There is no
Executions page: the retired loop's iterations are history on Costs, and `/executions`, like any unknown address, shows
"Page not found" with a link to Home.

- **Signed out.** `/` shows a landing page with Sign in and no data. Every other page sends the visitor to sign in. When
  a session ends mid-use, the page shows one Info notification for 1.5 seconds and then goes to sign in. Log out returns
  to the landing page. The Web app's `Oidc:PostLogoutRedirectUri` is its `authentication/logout-callback` address.
- **Home** (signed in) shows four cards: runs awaiting approval, running runs, runs that failed in the last 7 days, and
  recent pull requests. The cards need the `developer` or `admin` role, as the run list does; anyone else sees one line
  saying runs are visible to developers and admins, and Home reads no runs for them. The first three cards update live
  as runs change, with no refresh; the pull requests card is read when Home opens. **New task** opens the create
  dialog.
- **Tasks** lists a task's project, status, pull request, and on screens 1280px or wider its priority and when it was
  created. The grid shows Created, not Updated, because no "last updated" time exists yet (issue #373). The status links
  to the task's run. Below 768px the grid is one column: the title, then the status and pull request, then the actions.
  An empty board says "No tasks yet." with **New task**.
- **Git Repositories** is an admin's page. Anyone else who opens it by its address is told that git repositories are
  managed by admins.
- **Runs** lists the runs, and each run's page is described in step 4 above.

### Per-node model and tool selection

A manufacture run's implement and review steps do not all need the strongest model, or every tool. With decisions on,
each visit of those steps first asks one [Minos.NET](https://github.com/MarcelRoozekrans/Minos.NET) question set,
answered by `jev-latest` through OpenRouter for a fraction of a cent. It asks how demanding the work is and which tool
families it needs. The answer picks a model from the role's `AllowedModels`, cheapest first, and narrows the tools the
turn is offered. It never widens either, and Thalos refuses anything outside the role's own envelope.

- **Fallback is today.** No key, a slow or failed decision, or a low-confidence answer gives the role's default model
  and its full tool list. The reason is recorded and counted, and the node always runs.
- **One decision per visit.** It is recorded as a `node-selection` run record, reused on redelivery and returned as
  `nodeSelections` by `GET /api/workflow-runs/{id}`. The console does not show it yet (#354). The three review lens passes share it. After a rejected review, tools are no longer narrowed for the rest
  of the run.
- **Measured.** The phase 2.10 comparison ran four sandbox tasks with decisions off and on, priced cache-aware and
  checked against Anthropic's billing:

  | Task | Off | On | Saving |
  |---|---|---|---|
  | Routine (`Inventory.Remove`) | $1.57 | $0.44 | 72% |
  | Routine (Kelvin conversions) | $2.22 | $0.48 | 78% |
  | Harder (`Inventory.FromJson`) | $3.47 | $1.97 | 43% |
  | Harder (reservations) | $2.61 | $0.91 | 65% |

  Most of the saving is review, which moves from Opus 5 to Sonnet 5.5 for routine work. Replaying each review under
  both setups found nothing the default reviewer raised that the decided one missed. One run per task per arm is a small
  sample: review rounds vary, so the percentages will move. Details:
  [phase 2.10 rulings](docs/plans/2026-10-15-phase-2.10-rulings.md).
- **Turning it on.** Set the AppHost secret `Parameters:openrouter-key` (`OPENROUTER_API_KEY`). Turn on zero data
  retention at openrouter.ai/settings/privacy and only then set `Thalos:Decisions:ZeroDataRetentionAccount` to `true`,
  because a decision sends the task's text. Each call also asks OpenRouter for zero data retention and no data
  collection. The API's start-up log says whether decisions are on, and why not. See
  [Thalos agents](#thalos-agents) for `AllowedModels` and the `Thalos:Decisions:*` settings.

**History and frozen data.** `TaskExecutions` and `ExecutionSessions` are kept as read-only history, and the Web "Task
Loop History" page only shows them. The `daedalus__search_failure_patterns` and `daedalus__search_learnings` tools read
data only the retired loop wrote, and their descriptions and empty answers say so (`FrozenHistory.Notice`). Agents still
recall learnings through the `memory__*` tools and auto-recall. The recall budget for `search_learnings` is
`Thalos:Memory:LearningsRecall`; the old `Thalos:Memory:RalphRecall` key fails the boot.

The old technique is described in [Ralph Wiggum Technique](docs/ralph-wiggum-technique.md), kept as history.

---

## Tech Stack

| Category             | Technology                                                                          |
|----------------------|-------------------------------------------------------------------------------------|
| Language             | C# 13.0                                                                             |
| Framework            | .NET 10                                                                             |
| Orchestration        | .NET Aspire 13.1.0                                                                  |
| Database             | PostgreSQL 16 with pgvector (`pgvector/pgvector:pg16`)                              |
| ORM                  | Entity Framework Core 10 (Npgsql 10.0.3)                                            |
| Pattern Library      | ZeroAlloc.Results for Result types, ZeroAlloc.ValueObjects for value equality       |
| ZeroAlloc            | ZeroAlloc.Results 1.2.0, ZeroAlloc.Authorization 2.1.0, ZeroAlloc.Validation 1.5.6, ZeroAlloc.Mapping 1.6.1 (via Thalos.NET) |
| Durable delivery     | ZeroAlloc.Outbox 2.5.2 + ZeroAlloc.Outbox.EfCore 2.5.2 (channel outbound messages)  |
| Zero-Allocation LINQ | ZLinq 1.5.4                                                                         |
| Agent framework      | Thalos.NET 0.4.0 (nuget.org) on Microsoft Agent Framework 1.17, Microsoft.Extensions.AI 10.9         |
| Agent memory         | `Thalos.NET.Memory` 0.4.0 + `Thalos.NET.Memory.RagNet` 0.4.0 (→ Rag.NET 0.1.x pgvector store, `rag_chunks`) |
| Agent skills         | `Thalos.NET.Skills` 0.4.0 (markdown procedures from git, in-process cosine search, no Rag.NET dependency) |
| Agent channels       | `Thalos.NET.Channels` 0.4.0 (console channel, pump, commands) + `Thalos.NET.Channels.Telegram` 0.4.0 (Bot API, long-poll) |
| Embeddings           | Ollama `nomic-embed-text` (768 dims) via OllamaSharp 5.4.12                          |
| LLM security         | AI.Sentinel 2.0.1 (via `Thalos.NET.Sentinel`)                                       |
| LLM Providers        | Anthropic SDK 12.53.0 (agent factory for brainstorm and PRD generation; Thalos via `Thalos.NET.Anthropic`) |
| MCP                  | ModelContextProtocol 2.2.0 (`Thalos.NET.Mcp` reads `.mcp.json`)                     |
| Frontend             | Blazor WebAssembly + Radzen.Blazor 8.7.5                                            |
| Mocking              | NSubstitute 5.3.0 + NSubstitute.Analyzers.CSharp 1.0.17                             |
| Testing              | xUnit 2.9.3, NUnit 4.4.0, Playwright 1.58.0, AwesomeAssertions 7.0.0, Respawn 7.0.0 |
| Authentication       | Keycloak 26.0 (OIDC), JWT Bearer                                                    |
| Code Analysis        | SonarAnalyzer.CSharp, Meziantou.Analyzer, Microsoft.CodeAnalysis.NetAnalyzers       |
| Telemetry            | OpenTelemetry (OTLP export)                                                         |

---

## Project Structure

```
src/
├── Daedalus.AppHost/          # .NET Aspire orchestration (DCP, service wiring)
├── Daedalus.ServiceDefaults/  # Shared OpenTelemetry, logging, DB registration
├── Daedalus.Domain/           # Entities, Value Objects, Domain Events
├── Daedalus.Application/      # CQRS handlers, DTOs, Interfaces, Abstractions
├── Daedalus.Infrastructure/   # EF Core DbContext, Repositories, LLM services, External integrations
├── Daedalus.Agents/           # Thalos.NET composition root: agents from config, Postgres session + memory stores, knowledge tools, DeveloperPolicy
├── Daedalus.Api/              # REST controllers (incl. /api/agents + SSE), JWT auth, health checks, .mcp.json for Thalos, Telegram channel poller
├── Daedalus.Cli/              # Interactive terminal host for Thalos agents (console channel; run by hand, not in AppHost)
├── Daedalus.Web/              # Blazor WASM frontend (Radzen components)
├── Daedalus.Sandbox/          # Per-run sandbox container host (Dockerfile; built by the AppHost)
└── Daedalus.Migrations/       # EF Core database migration runner

tests/
├── Daedalus.Tests.Unit/              # General unit tests + ArchUnit rules (xUnit)
├── Daedalus.Tests.Unit.Domain/       # Domain layer unit tests (xUnit)
├── Daedalus.Tests.Unit.Application/  # Application layer unit tests (xUnit)
├── Daedalus.Tests.Unit.Infrastructure/ # Infrastructure unit tests (xUnit)
├── Daedalus.Tests.Integration/       # Integration tests with Testcontainers (xUnit, pgvector/pgvector:pg16)
├── Daedalus.Tests.Playwright.Api/    # API E2E tests (NUnit + Playwright)
└── Daedalus.Tests.Playwright.Browser/ # Browser E2E tests (NUnit + Playwright)

skills/
└── <name>/SKILL.md            # Agent procedure documents, synced into the Skills table at host start

benchmarks/
└── Daedalus.Benchmarks/       # BenchmarkDotNet performance benchmarks
```

---

## Prerequisites

| Requirement     | Version  | Purpose                                             |
|-----------------|----------|-----------------------------------------------------|
| .NET SDK        | **10.0** | Build and run all projects                          |
| Docker Desktop  | Latest   | PostgreSQL container, Aspire DCP, integration tests |
| PowerShell      | **7+**   | Aspire CLI install scripts, Playwright setup        |
| WSL 2 (Windows) | Enabled  | Required by Docker Desktop on Windows               |

> PostgreSQL does **not** need to be installed locally — Aspire provisions it as a Docker container automatically.

---

## Setup

### 1. Clone and Restore

```bash
git clone <repository-url>
cd Daedalus

# Restore NuGet packages
dotnet restore

# Restore local .NET tools (EF Core CLI + Aspire CLI)
dotnet tool restore
```

The `.config/dotnet-tools.json` manifest includes:

| Tool         | Version | Purpose                |
|--------------|---------|------------------------|
| `dotnet-ef`  | 10.0.0  | EF Core migrations     |
| `Aspire.Cli` | 13.1.0  | Aspire Dashboard + DCP |

### 2. Install Aspire CLI (if not already)

The Aspire CLI is needed for DCP container orchestration and the Dashboard:

```powershell
# Windows (PowerShell)
Invoke-RestMethod -Uri "https://aspire.dev/install.ps1" | Invoke-Expression
```

```bash
# macOS / Linux
curl https://aspire.dev/install.sh | bash
```

Verify installation:

```bash
aspire --version
# Expected: 13.1.0 or later
```

### 3. Install .NET Workloads

```bash
# WASM workload (required for Blazor WebAssembly frontend)
dotnet workload install wasm
```

### 4. Verify Docker

```bash
docker --version
docker compose version
```

On Windows, ensure Docker Desktop is running and WSL 2 is enabled (`wsl --list --verbose`).

### 5. Build

```bash
dotnet build
```

All projects enforce `TreatWarningsAsErrors=true` with three code analyzers (SonarAnalyzer, Meziantou, NetAnalyzers) at
`AnalysisLevel=latest-all`. A clean build should produce **0 errors, 0 warnings**.

---

## Running the Application

The project uses **.NET Aspire 13.1.0** with DCP for orchestration. All services (PostgreSQL, migrations, API,
frontend) are started and wired together automatically.

### Quick Start

```bash
dotnet run --project src/Daedalus.AppHost
```

The `launchSettings.json` pre-configures all required environment variables. No extra setup is needed.

### What Happens on Startup

1. **DCP** starts container orchestration
2. **PostgreSQL 16 + pgvector** container starts (`pgvector/pgvector:pg16`, persistent data volume)
3. **Keycloak 26.0** container starts (with realm auto-import from `keycloak-realm.json`)
4. **Ollama** container starts and pulls `nomic-embed-text` (embeddings for AI.Sentinel and agent memory; ~274 MB on the
   first run, cached in a data volume)
5. **Migrations** run automatically (`Daedalus.Migrations`, waits for DB + Keycloak), and the per-run sandbox image
   `daedalus-sandbox:dev` is built with `docker build`. The API waits for both to *complete* (`WaitForCompletion`),
   not merely to start, so no host boots against an un-migrated database
6. **API** starts (REST + JWT auth via Keycloak, port `5000`; creates the Rag.NET `rag_chunks` schema)
7. **Web Frontend** starts (Blazor WASM, OIDC login via Keycloak)
8. **Aspire Dashboard** available with real-time monitoring

### Endpoints

| Service          | URL                    | Notes                                     |
|------------------|------------------------|-------------------------------------------|
| Aspire Dashboard | http://localhost:17300 | Main monitoring UI                        |
| API              | Shown in Dashboard     | Target port `5000`, dynamic external port |
| Web Frontend     | Shown in Dashboard     | Blazor WASM app                           |
| Keycloak         | Shown in Dashboard     | OIDC provider, admin: `admin`/`changeme`  |
| PostgreSQL       | `localhost:5432`       | Managed by DCP                            |
| OTLP gRPC        | `localhost:18889`      | Telemetry ingress                         |
| OTLP HTTP        | `localhost:18890`      | Telemetry ingress                         |

### Running Without launchSettings

If running outside an IDE that picks up `launchSettings.json`, set these variables first:

```powershell
$env:ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL = "http://localhost:18889"
$env:ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL = "http://localhost:18890"
$env:ASPIRE_ALLOW_UNSECURED_TRANSPORT = "true"
$env:PARAMETERS__DB_USERNAME = "postgres"
$env:PARAMETERS__DB_PASSWORD = "postgres"

dotnet run --project src/Daedalus.AppHost
```

### Running Migrations Manually

Migrations run automatically via Aspire. To run them manually (e.g., after pulling schema changes):

```bash
dotnet run --project src/Daedalus.Migrations
```

Or via the EF Core CLI:

```bash
dotnet ef database update --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api
```

---

## Observability

Daedalus traces a manufacture run step by step and counts the tokens it spends. Telemetry goes **over OTLP only when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set** (non-empty). Aspire sets it for the projects it runs. The Api, and the Cli
when it is pointed at a collector, are the hosts that export; Migrations and Web do not. Under
`dotnet run --project src/Daedalus.AppHost` the dashboard on http://localhost:17300 shows traces, metrics and logs.
Without it nothing is exported. `docker-compose.full.yml` runs no collector and sets no endpoint, so export is off
there; add a collector service and set `OTEL_EXPORTER_OTLP_ENDPOINT` on the API to enable it.

One `UseOtlpExporter()` carries all three signals, configured from the standard `OTEL_*` variables. When the host stops
normally, `TelemetryShutdownFlush` flushes logs, traces and metrics within one 5 second budget. It exists because of an
upstream OpenTelemetry bug (open-telemetry/opentelemetry-dotnet#7915, tracked in Daedalus#332): with `http/protobuf`,
the logs of a host that stops before its first export are lost. A host whose start fails still loses them.

### What is collected

- **Traces:** the sources `thalos`, `thalos.workflow`, `ZeroAlloc.Mediator`, `daedalus.*` and `Minos` (the decision
  client's own spans), plus ASP.NET Core and `HttpClient`. Requests to `/health` and `/alive` are not traced. Npgsql is
  not traced: the outbox poller would drown the traces.
- **URLs.** An outgoing `HttpClient` span exports only the origin of its call: `url.full` is `scheme://host[:port]`,
  with no path, query or userinfo, and no other tag or the span name carries the path. A path can hold a secret, such as
  the Telegram bot token in `/bot{token}/{method}`, so `HttpClientOriginOnlyProcessor` cuts it before any exporter sees
  it, failed calls and redirect hops included. ASP.NET Core server spans mask the query, so `url.query` shows
  `?key=Redacted`, but they export the request path in `url.path` unmasked, for example
  `/api/tasks/<task id>/manufacture`. Task ids are Guids; do not put a secret in a route. A failed outgoing call also
  carries the runtime's `exception` event, with the exception's type, message and stack trace. Those messages name the
  host and port, not the path.
- **Metrics:** the meters `thalos`, `thalos.workflow`, `daedalus.*` and `Minos`, the built-in `System.Runtime` meter,
  and the ASP.NET Core and `HttpClient` metrics.

### Finding a run

Filter traces by `thalos.run.id`. The spans of a run:

| Span | What it is |
|------|------------|
| `POST api/Tasks/{id:guid}/manufacture` | The request span, named by its route template. It carries `daedalus.task.id`. |
| `daedalus.manufacture.start` | The start. Tags `daedalus.repository` (the configured, allow-listed repository name, set only once the request matched the allow-list) and, on success, `thalos.run.id`. A refused start has Error status described only by the failure kind, a closed enum. |
| `thalos.workflow.store.start` | The store write that creates the run, a child of the start span. |
| `thalos.workflow.dispatch` | One workflow step. Each step is **its own trace**, with a link to `thalos.workflow.store.start`, so follow the link back to the start. Tags `thalos.run.id`, `thalos.run.process`, `thalos.node`, `thalos.seq`, `thalos.outcome`. |
| `thalos.workflow.host_action.{type}` | A host action inside a step, named by its CLR type, such as `thalos.workflow.host_action.OpenPullRequestAction`. |
| `daedalus.node_selection` | The per-node model and tool decision, on the source `daedalus.decisions`. Tags `thalos.node`, `daedalus.role`, `daedalus.decision.level`, `daedalus.decision.kept_families` (a count), `daedalus.decision.model` (the chosen model), `daedalus.decision.confidence` (only when Minos answered) and `daedalus.decision.fallback` (only when the default applies). Off, a timeout and an error carry no confidence. |
| `thalos.turn` | An agent turn, from the Thalos turn runtime. |
| `thalos.workflow.store.resume` | The gate approval: the person approving a gate resumes the run. Other store spans: `.cancel`, `.fail`, `.fail_stranded`, `.retry`. |
| `daedalus.manufacture.cancel` | A compensating cancel. Tags the run id and `daedalus.cancelled`. |

### Metrics

| Metric | Unit | Tags | Meaning |
|--------|------|------|---------|
| `thalos.workflow.runs.started` | `{run}` | `thalos.run.process` | Runs started. |
| `thalos.workflow.runs.finished` | `{run}` | `thalos.run.process`, `thalos.run.state` | Runs that reached Succeeded, Failed or Cancelled. |
| `thalos.workflow.dispatch.duration` | ms | `thalos.node`, `thalos.outcome` (`error.type` instead when the dispatch throws) | One step dispatch. |
| `thalos.workflow.host_action.duration` | ms | `thalos.host_action` | One host action call. |
| `thalos.workflow.run_tool.duration` | ms | `thalos.tool.source`, `thalos.tool` | One run tool call. |
| `daedalus.manufacture.tokens` | `{token}` | `thalos.node`, `gen_ai.request.model` (omitted when empty), `daedalus.token.kind` | Tokens a completed node spent. |
| `daedalus.github.ratelimit.remaining` | `{request}` | `daedalus.github.resource` (one of GitHub's documented resources such as `core` or `search`, `other` for any other value, `unknown` when the header is missing or blank) | Requests left in GitHub's current rate-limit window, from the last answer, 304s included. A read answered by a 304 costs nothing, so the value does not drop. |
| `daedalus.decisions` | `{decision}` | `daedalus.decision.fallback` (`none` when the decision was taken, else `off`, `timeout`, `error:{kind}`, `error:Client`, `low-confidence`, `no-allowed-models`, `tools-unresolved` or `error:Validate`) | Per-node decisions. |

`daedalus.token.kind` is `input`, `output`, `cache_read` or `cache_write`. **`input` already includes `cache_read` and
`cache_write`, so do not sum all four kinds**: that counts cache tokens twice. Tokens are counted at most once per
completed node. A crash can lose a count but never double it. Backfilled history is not counted.

### No content in telemetry

Span names, tags, status descriptions, events and metric tags carry only ids, names, counts, durations, outcomes and
error codes. Never prompt or agent text, task titles or descriptions, PR titles or bodies, file content, credentials or
token values. Decision telemetry follows the same rule: it never carries task text, tool names or the OpenRouter key.
Outgoing HTTP calls export their origin only, see "What is collected". A failed step sets Error status
without a description; the one exception is the start span's failure kind.

The console's live connection sends its bearer token in the `access_token` query value of the hub URL. The API
redacts it from server spans and keeps it out of logs. It cannot reach the logs it does not write, so strip
`access_token` from the access logs of any proxy or ingress in front of the API.

This rule does not yet cover exported **logs**. Whether and how to scrub them is an open decision, Daedalus#334.

### Tuning

Sampling follows the standard variables, with no code change: `OTEL_TRACES_SAMPLER` and `OTEL_TRACES_SAMPLER_ARG`, for
example `OTEL_TRACES_SAMPLER=parentbased_traceidratio` with `OTEL_TRACES_SAMPLER_ARG=0.1`. Each step is its own root
trace, so a ratio sampler keeps or drops each step of a run independently, and a sampled run is usually incomplete.

---

## Configuration

### Environment Variables

| Variable                                  | Required     | Default                  | Purpose                                               |
|-------------------------------------------|--------------|--------------------------|-------------------------------------------------------|
| `PARAMETERS__DB_USERNAME`                 | Yes (Aspire) | `postgres`               | PostgreSQL username                                   |
| `PARAMETERS__DB_PASSWORD`                 | Yes (Aspire) | `postgres`               | PostgreSQL password                                   |
| `ANTHROPIC_API_KEY`                       | No           | —                        | Claude API key (agent factory fallback; required for Thalos agent turns) |
| `DAEDALUS_REGRESSION_SCREENSHOTS`         | No           | —                        | `1` → Browser E2E writes regression screenshots into `docs/regression-screenshots/` |
| `GITHUB_TOKEN`                            | No           | —                        | GitHub API authentication for Git operations          |
| `OPENROUTER_API_KEY`                      | No           | —                        | OpenRouter key for per-node model and tool decisions. Under Aspire, set the AppHost secret `Parameters:openrouter-key`. Decisions also need `Thalos:Decisions:ZeroDataRetentionAccount` = `true`, else they stay off |
| `GITLAB_TOKEN`                            | No           | —                        | GitLab API authentication                             |
| `AZURE_DEVOPS_TOKEN`                      | No           | —                        | Azure DevOps API authentication                       |
| `E2E_BASE_URL`                            | No           | —                        | Override base URL for Playwright E2E tests            |
| `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL`      | Yes (Aspire) | `http://localhost:18889` | OTLP gRPC endpoint                                    |
| `ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL` | Yes (Aspire) | `http://localhost:18890` | OTLP HTTP endpoint                                    |
| `ASPIRE_ALLOW_UNSECURED_TRANSPORT`        | No           | `true` (dev)             | Allow HTTP transport in dev                           |

### Application Settings (`appsettings.json`)

The **API** and **CLI** projects each have their own `appsettings.json`. The API one has these key sections:

#### Database Connection

```json
{
    "ConnectionStrings": {
        "daedalus": "Host=localhost;Port=5432;Database=daedalus;Username=postgres;Password=postgres;Include Error Detail=true"
    }
}
```

> When running via Aspire, connection strings are injected automatically and override these defaults.

#### LLM Provider Configuration

`IAgentFactory` (brainstorm and PRD generation) reads the Claude section. Thalos agent turns are configured under
`Thalos:Anthropic` instead (see [Thalos agents](#thalos-agents)). With no `Model` of its own, the Claude section uses
`Thalos:Anthropic:DefaultModel`, so one setting decides the model of every Anthropic call. The API refuses to start
when neither is set, or when the model has no `ModelPricing:Models` price.

```json
{
    "ExternalServices": {
        "Llm": {
            "Claude": {
                "ApiKey": null,
                "MaxTokens": 8192
            }
        }
    }
}
```

| Setting               | Description                                                              |
|-----------------------|--------------------------------------------------------------------------|
| `Llm.Claude.ApiKey`   | Anthropic API key (or set `ANTHROPIC_API_KEY` env var)                   |
| `Llm.Claude.Model`    | Claude model identifier; defaults to `Thalos:Anthropic:DefaultModel`     |
| `Llm.Claude.MaxTokens`| Output token cap per invocation                                          |

#### MCP (Model Context Protocol) Integration

```json
{
    "ExternalServices": {
        "Mcp": {
            "Enabled": true,
            "Servers": {
                "context7": {
                    "Type": "http",
                    "Url": "https://context7.com/api"
                }
            }
        }
    }
}
```

#### Repository Platform Tokens

Git operations support GitHub, GitLab, and Azure DevOps. Tokens are read only from environment variables; no
configuration key holds one:

| Platform     | Env Variable         |
|--------------|----------------------|
| GitHub       | `GITHUB_TOKEN`       |
| GitLab       | `GITLAB_TOKEN`       |
| Azure DevOps | `AZURE_DEVOPS_TOKEN` |

`ExternalServices:Platforms:GitHub` configures the GitHub client: `ApiUrl`, `UserAgent`, `MaxItemsPerCategory` (default
15, set in code) and `DefaultLookback`.

#### The GitHub client

Every GitHub call goes through `GitHubApi`, over two ZeroAlloc.Rest clients generated from `IGitHubRestApi`:

- **Reads** use the `GitHub.Read` client. They retry on 5xx, 408, timeouts and connection failures. A 429 is not
  retried: it fails at once as a rate limit with the time it lifts. A repeated read is sent with `If-None-Match`, and
  GitHub's `304 Not Modified`, which costs no rate limit, is answered from an in-memory cache of 200 answers of at most
  256 KiB each. A restart empties it.
- **Writes**, comments, labels, closes, issues and pull requests, use the `GitHub.Write` client and are sent **exactly
  once**, never retried: GitHub has no idempotency keys, so a retried write can land twice. The Azure DevOps pull request
  create follows the same rule.
- **Errors** are a typed `GitHubError` with a kind, such as `RateLimited`, `NotFound` or `Forbidden`, the status, and
  for a rate limit the time it lifts. A 429, a 403 with `X-RateLimit-Remaining: 0`, a 403 with `Retry-After` and a 403
  whose body names a secondary rate limit are all rate limits. The time is `Retry-After` when GitHub sends it, else
  `X-RateLimit-Reset` when no requests remain (a reset already past counts as now), else a minute from now. An agent
  tool also shows a one-line summary of what GitHub said, at most 300 characters. Logs, run errors and the publish
  node never repeat GitHub's response body or an exception's message, and an error text carries no URL or repository
  slug; a failure log names an exception's type only. Only the success confirmation of a pull request carries its
  web URL. A write that failed in a way that may have reached GitHub, a timeout, a server error, a
  transport failure or a 2xx answer that could not be read, says so.
- **The remaining rate limit** is the gauge `daedalus.github.ratelimit.remaining`, see "Metrics". Nothing throttles
  before GitHub refuses.

#### Authentication (API)

```json
{
    "Authentication": {
        "Authority": "https://your-oidc-provider.example.com",
        "Audience": "daedalus-api"
    }
}
```

JWT Bearer authentication is configured but the Authority must point to a real OIDC provider for production. In
development, Keycloak is provisioned automatically by Aspire and HTTPS metadata validation is disabled.

---

## Thalos agents

Phase 1.1 of the [roadmap](docs/planning/ROADMAP.md) adds a general-purpose agent stack built on
[Thalos.NET](https://github.com/MarcelRoozekrans/Thalos.NET) (Microsoft Agent Framework 1.17 underneath, ZeroAlloc-native,
AI.Sentinel at the model boundary). It once ran **alongside** the Ralph Loop as a strangler; phase 2.8 retired that loop, and a
board task now starts work as a manufacture run (see [Manufacturing a task](#manufacturing-a-task)). Design: [docs/plans/2026-08-16-thalos-agent-core-design.md](docs/plans/2026-08-16-thalos-agent-core-design.md);
sequence diagram: [Architecture Diagrams §15](docs/architecture-diagrams.md#15-agent-turn-thalos).

Phase 1.2 adds **memory** on top: `Thalos.NET.Memory` + `Thalos.NET.Memory.RagNet` (Thalos.NET 0.2.0). Design:
[docs/plans/2026-08-17-thalos-memory-design.md](docs/plans/2026-08-17-thalos-memory-design.md); see
[Memory](#memory) below.

Phase 1.3 adds **skills** on top: `Thalos.NET.Skills` (Thalos.NET 0.3.0), a library of markdown procedures authored in
git that agents can see the titles of every turn and pull into context on demand. Design:
[docs/plans/2026-08-18-thalos-skills-design.md](docs/plans/2026-08-18-thalos-skills-design.md); see
[Skills](#skills) below.

Phase 1.4 adds **channels**: a second and third way to reach the same agents besides the Blazor app —
`Thalos.NET.Channels` + `Thalos.NET.Channels.Telegram` (Thalos.NET 0.4.0) give a Telegram bot (polling, one
operator) and `Daedalus.Cli` gives a terminal host, both built on the same `ChannelPump`/`IChannelAdapter` seam.
Design: [docs/plans/2026-08-20-thalos-channels-design.md](docs/plans/2026-08-20-thalos-channels-design.md); see
[Channels](#channels) below.

**What you get:** a signed-in user opens `/agent` in the Blazor app, picks an agent (default: *Daedalus Architect*),
starts a session and chats — or reaches the same agents from Telegram or a terminal (see [Channels](#channels)).
Each turn is streamed back as Server-Sent Events (text deltas, tool calls/results, usage,
memory events), tools come from MCP servers (`roslyn__*`, `context7__*`), local knowledge tools (`daedalus__*` — failure
patterns), memory tools (`memory__remember/recall/forget/list`) and skill tools (`skills__load`, `skills__search`),
relevant memories are injected before each turn, a catalogue of available procedures is appended to the agent's
instructions, and every prompt/response passes through AI.Sentinel. Sessions, transcripts, memories and skills are
persisted in PostgreSQL (`AgentSessions`, `AgentMessages`, `AgentMemories`, `Skills`), and channel bindings in
`ChannelConversations`.

### Configuration (`Thalos:*` in `src/Daedalus.Api/appsettings.json`)

```json
{
    "Thalos": {
        "McpConfigPath": ".mcp.json",
        "Anthropic": { "DefaultModel": "claude-sonnet-5", "DefaultMaxOutputTokens": 8192 },
        "Sentinel": {
            "Enabled": true,
            "OnCritical": "Quarantine", "OnHigh": "Alert", "OnMedium": "Log", "OnLow": "Log",
            "DisabledDetectors": []
        },
        "Memory": {
            "Enabled": true,
            "SharedOwnerId": "daedalus",
            "Recall": { "TopK": 5, "MinScore": 0.6, "MaxChars": 2000 },
            "Dedupe": { "Enabled": true, "Threshold": 0.95 },
            "ExposeTools": true,
            "VectorDimensions": 768,
            "LearningsRecall": { "TopK": 10, "MinScore": 0.5 },
            "Reindex": { "Enabled": true, "StartupDelay": "00:00:10", "RetryInterval": "00:02:00", "SweepInterval": "00:15:00" }
        },
        "Skills": {
            "Enabled": true,
            "Roots": [ "skills" ],
            "Catalogue": { "MaxChars": 2000 },
            "Search": { "TopK": 5, "MinScore": 0.6 }
        },
        "Channels": {
            "DefaultAgent": "Daedalus Architect",
            "Telegram": {
                "Enabled": false,
                "BotToken": "",
                "PrincipalId": "",
                "AllowedUserIds": []
            }
        },
        "ToolPolicies": [
            { "Pattern": "roslyn__apply_*", "Policy": "developer" },
            { "Pattern": "roslyn__rename_*", "Policy": "developer" }
        ],
        "Agents": [
            {
                "Id": "01M05YCM7DPKRG9X04870B2JYH",
                "Name": "Daedalus Architect",
                "Description": "Answers architecture questions about the Daedalus solution using Roslyn and Daedalus learnings.",
                "Instructions": "You are a senior .NET architect embedded in the Daedalus project. ...",
                "Tools": [ "roslyn__*", "daedalus__*", "memory__*", "skills__*", "context7__*" ],
                "Skills": [ "*" ],
                "Memory": { "Enabled": true, "TopK": 5 }
            }
        ]
    }
}
```

| Key                                            | Description                                                                                                                                                                                                  |
|------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Thalos:McpConfigPath`                         | Claude Code-style MCP config file; relative paths resolve against the API content root. Missing file → no MCP tool sources.                                                                                  |
| `Thalos:Anthropic:DefaultModel`                | Model used when an agent does not set `Model`. The API key comes from `Thalos:Anthropic:ApiKey` or `ANTHROPIC_API_KEY` (read lazily on the first turn).                                                     |
| `Thalos:Anthropic:DefaultMaxOutputTokens`      | Per-call output cap unless the agent overrides `MaxOutputTokens`.                                                                                                                                            |
| `Thalos:Sentinel:Enabled`                      | Registers the AI.Sentinel decorator around the chat client.                                                                                                                                                  |
| `Thalos:Sentinel:On{Critical,High,Medium,Low}` | Action per severity: `PassThrough`, `Log`, `Alert`, `Quarantine`. A quarantined turn returns `422 Quarantined` (buffered) / `event: error` (stream).                                                          |
| `Thalos:Sentinel:DisabledDetectors`            | AI.Sentinel detector type names to switch off (e.g. `PromptInjectionDetector`); unknown names fail at startup.                                                                                               |
| `Thalos:Memory:Enabled`                        | Master switch for memory: auto-recall, `memory__*` tools and the reindex sweeper.                                                                                                 |
| `Thalos:Memory:SharedOwnerId`                  | Owner of shared project knowledge: the learnings the retired Ralph loop wrote. Recalled for every caller; deleting one needs the `developer` policy. Default `daedalus`.                              |
| `Thalos:Memory:Recall:{TopK,MinScore,MaxChars}`| Auto-recall budget per turn (and for `memory__recall`): how many memories, the cosine-similarity floor and the character cap on the injected block.                                                          |
| `Thalos:Memory:Dedupe:{Enabled,Threshold}`     | Thalos refuses a near-duplicate `remember` above the similarity threshold and reports the existing memory (`deduped`).                                                                                       |
| `Thalos:Memory:ExposeTools`                    | Registers the `memory` tool source. Which agents actually see `memory__*` is still decided by their `Tools` glob.                                                                                            |
| `Thalos:Memory:VectorDimensions`               | Embedding width of the Rag.NET index (`nomic-embed-text` = **768**). Must match the existing `rag_chunks` table — see [Operational notes](#operational-notes).                                              |
| `Thalos:Memory:LearningsRecall:{TopK,MinScore}` | Daedalus-only: how many shared learnings the `search_learnings` MCP tool asks for, and the minimum score. Replaces `Thalos:Memory:RalphRecall`, which fails the boot. `TopK` is clamped to 1–50 (1–20 for the MCP tool).                                           |
| `Thalos:Memory:Reindex:*`                      | Daedalus-only: `ReindexPendingMemoriesHostedService` (registered by `AddDaedalusAgents`, so in both the API and CLI hosts) — `Enabled`, `StartupDelay`, `RetryInterval` (index unavailable/failed rows), `SweepInterval` (all clear).                              |
| `Thalos:Skills:Enabled`                        | Master switch for the catalogue and the `skills__*` tools. `false` skips root validation entirely, so a host with no skills folder still starts.                                                              |
| `Thalos:Skills:Roots[]`                        | Folders holding `<name>/SKILL.md`, resolved against the host content root (like `McpConfigPath`). **A configured root that does not exist fails host start, on purpose** — see [Operational notes](#operational-notes). Empty by default. |
| `Thalos:Skills:Catalogue:MaxChars`             | Character budget for the catalogue block appended to the agent's instructions. Overflow is reported with an explicit "and N more" line, never silently truncated.                                             |
| `Thalos:Skills:Search:{TopK,MinScore}`         | `skills__search` defaults. Search is in-process cosine over the same Ollama generator; without it search reports unavailable and the catalogue stays authoritative.                                           |
| `Thalos:Channels:DefaultAgent`                 | **The agent's `Name`, not its `Id`.** `AgentId` is ULID-backed with no string constructor; the pump resolves the default agent by scanning `IAgentCatalog.Agents` for this value against `AgentDefinition.Name` (ordinal, case-insensitive). A wrong value (a ULID, a typo) fails only at **runtime**, the first time a channel needs to bind — there is no startup validation. This shipped wrong once (a ULID here) and was only caught by a hands-on CLI session; see [Channels](#channels). |
| `Thalos:Channels:Telegram:Enabled`             | Runtime on/off switch, distinct from whether Telegram is *registered* at all (that is decided by `BotToken` being non-blank — see [Channels](#channels)). **Must be `true` on at most one API host replica** — see [Operational notes](#operational-notes). |
| `Thalos:Channels:Telegram:BotToken`            | **Never set in `appsettings.json` or anywhere else in the repo.** Local: `dotnet user-secrets set Thalos:Channels:Telegram:BotToken <token> --project src/Daedalus.Api`. Container: the `telegram-bot-token` Aspire parameter. Blank → Telegram is not registered at all, host starts normally, no error. |
| `Thalos:Channels:Telegram:PrincipalId`         | The `ConfiguredSecurityContext.Id` every Telegram turn runs as — a channel-specific identity, distinct from any Keycloak `sub`. Sessions started from Telegram do not appear in the web UI's session list, and vice versa. |
| `Thalos:Channels:Telegram:AllowedUserIds`      | Telegram user ids allowed to talk to the bot. **Empty fails host start by design** (`TelegramOptions.Describe`) — never "allow everyone". Set alongside `PrincipalId`/`BotToken` via user-secrets, never in the repo. |
| `Thalos:Channels:Telegram:Roles`               | Roles granted to the Telegram principal. **Recommended: leave empty.** The *absence* of `developer`/`admin` is what stops a phone-reachable bot from running mutating Roslyn tools or deleting shared-owner memories — an empty set still gives full access to the principal's own sessions (ownership is by caller id, not role). |
| `Thalos:ToolPolicies[]`                        | Glob over qualified tool names → `[Policy]` name that must pass before the tool runs (authorization at the function boundary).                                                                               |
| `Thalos:Agents[]`                              | Agent definitions: stable `Id` (ULID or GUID — sessions reference it, never change it), `Name`, `Description`, `Instructions`, optional `Model`/`MaxOutputTokens`, `Tools` glob allow-list (empty → `*`).   |
| `Thalos:Agents[]:Skills`                       | Glob allow-list over skill names (`*` for all, `daedalus-*` for a family). **Empty by default** — unlike `Tools`, procedures are granted explicitly, because a catalogue costs tokens on every turn.          |
| `Thalos:Agents[]:Memory`                       | Per-agent overrides `{ Enabled, TopK }`; `null` members inherit `Thalos:Memory`. Tool visibility is *not* configured here — use the agent's `Tools` globs.                                                   |
| `Thalos:Agents[]:AllowedModels`                | Models a per-node decision may pick for this agent, cheapest first. Empty → no choice, the agent runs on its default model. The default need not be listed. Every entry needs a `ModelPricing:Models` price. |
| `Thalos:Decisions:ZeroDataRetentionAccount`    | `false` by default. Set `true` only when the OpenRouter account has zero data retention (openrouter.ai/settings/privacy): decisions send the task's work intent. Decisions run only when this is `true` **and** `OPENROUTER_API_KEY` is set. |
| `Thalos:Decisions:{Model,Timeout}`             | The OpenRouter model that decides (default `jev-latest`) and how long a decision may take (default 5 seconds) before the node falls back to its default model and full tool list. |
| `Thalos:Decisions:{ModelConfidence,ToolFamilyProbability}` | Minimum confidence (default 0.6) for a model choice to be taken, and minimum probability (default 0.5) for a tool family to be kept. Both between 0 and 1. |
| `Thalos:Decisions:PinnedLensModels`            | Review lens (`correctness`, `falsifiability`, `mechanism`) → model its pass runs on whatever the node's decision. Applies only while decisions are on; with them off every lens runs on the review's default model, and the start-up line counts the inactive pins. Each must be in `AllowedModels` of the agent that runs the review under the current `Thalos:Squad:Enabled`, or that agent's default model when known; start-up refuses it otherwise, on or off. |
| `Thalos:Workflow:TurnBudgets`                  | Per-turn token ceiling of each workflow agent: `Default` for any agent not listed, and `Agents:<name>` entries for `implementer` and `reviewer`. Shipped at 2,900,000 each, sized from measured turns: `docs/development-guide.md`, "Token budget". Both hosts ship the same values. |

**Turn token ceilings (phase 2.15).** Each workflow turn is stopped before the model call that would start with the
turn already at or above its agent's ceiling in `Thalos:Workflow:TurnBudgets`. The count is input plus output tokens,
with cache reads at full weight though they are billed at a tenth, so a turn can end above the ceiling by its last
call. A stopped turn fails its node with `SubagentBudgetExceeded`, and its spend still shows on `/costs`.
`DetachedRuns:MaxTotalTokens` is not a workflow ceiling: it bounds the scout and other detached runs only, and only
`DetachedRuns:DeadlineSeconds` still applies to workflow turns. How the numbers were sized and how to resize them:
`docs/development-guide.md`, "Token budget".

**Per-node model and tool selection (phase 2.10).** Before an implement or review node runs, a cheap
[Minos.NET](https://github.com/MarcelRoozekrans/Minos.NET) decision, made through OpenRouter, picks the node's model
from its role's `AllowedModels` and drops tool families the work does not need. It is off until you set
`Thalos:Decisions:ZeroDataRetentionAccount` to `true` and supply a key: under Aspire, set the AppHost user secret
`Parameters:openrouter-key`, which reaches the API as `OPENROUTER_API_KEY`. Never put the key in a file in the
repository. When it is off the API logs why at start-up, and every node runs as before. The
`workspace` and `daedalus` tool families are never removed, and tools stay whole after a rejected review. A decision
that is slow, fails or is unsure falls back to the default model and full tool list; while decisions are on, only a
decision that cannot be recorded fails the node, since a redelivery could not run on it again. Each
decision is recorded, with Minos's answers when it answered, as a `node-selection` run record and listed under `nodeSelections` on
`GET /api/workflow-runs/{id}`, with `nodeSelectionsError` naming any record that could not be read. The spans and
metrics are under "Observability". Task text is sent to OpenRouter. Every decision call asks OpenRouter for zero data
retention and no data collection, and decisions still need the account confirmation as well: see
[the development guide](docs/development-guide.md#node-selection).

To compare reviewer setups on the same code, `POST /api/workflow-runs` with `workIntent`, `repository` (an allow-listed
name), `process` `review-replay` and `baseBranch` `manufacture/<run-id>` reviews an existing manufacture branch and ends, with no pull request.
The `repository` must be the one the original run worked on, or the start is refused with 400.

**Sentinel embedding generator:** the semantic detectors (prompt injection, jailbreak, exfiltration, …) need an
`IEmbeddingGenerator`. The API passes the Ollama client (`nomic-embed-text`) when Aspire provides `ConnectionStrings:ollama`;
without Ollama only the lexical/operational detectors run and Sentinel logs a warning per agent pipeline.

### Memory

Agents keep **curated memories**: short, durable records (facts, preferences, decisions, notes, learnings), not a
transcript archive.

- **How they are used.** Before each turn Thalos recalls the top-`TopK` memories above `MinScore` and injects them into
  the prompt (budgeted by `MaxChars`); the agent can also call `memory__remember`, `memory__recall`, `memory__forget`
  and `memory__list` itself. Recall is scoped to the caller's own memories, the memories pinned to the current agent,
  and the shared owner's — so one user never sees another's.
- **The shared owner holds the retired loop's learnings.** Everything written under `Thalos:Memory:SharedOwnerId`
  (`daedalus`) with kind `learning` is recalled for every caller. The Ralph Loop wrote there until phase 2.8; nothing
  writes learnings there now, and the Application port `ILearningsMemory` (adapter `ThalosLearningsMemory` in
  `Daedalus.Agents`) only reads them, for the `search_learnings` tool. Learnings and agent memory are one store.
- **Where it lives.** The `AgentMemories` table (`PostgresMemoryStore`, `Daedalus.Agents`) is the source of truth; the
  Rag.NET index (`rag_chunks`, same database, `vector(768)`) is a **rebuildable cache** of embeddings. Vectors are never
  stored on `AgentMemories`.
- **Embeddings.** Ollama `nomic-embed-text` (768 dims), the same client AI.Sentinel uses. Without Ollama the index probes
  as unavailable: memories are still stored, flagged `index_pending`, and are not recalled until they are indexed.
  `ReindexPendingMemoriesHostedService` (API host, `Thalos:Memory:Reindex:*`) sweeps them in the background once the
  index comes up. It sweeps **pending rows only**, so rebuilding the whole index means marking the rows pending first —
  see [Operational notes](#operational-notes).
- **Hosts.** `AddDaedalusAgents` (API and CLI) registers agents **and** memory, owns the Rag.NET schema
  (`EnsureSchemaOnStartup`) and runs the sweeper. `AddDaedalusMemory`, the memory-only registration the Console worker
  used, was deleted with the Console in phase 2.8.
- **Migration.** `AddAgentMemories` creates `AgentMemories`, copies every `StructuredLearnings` row into it (category and
  severity become tags, `index_pending = true` so the sweeper embeds them) and drops `StructuredLearnings`. The
  hand-rolled learnings/embedding slice — `ILearningsRepository`, `IEmbeddingService`, `Pgvector.EntityFrameworkCore`
  and every `UseVector()` — is gone.
- **UI.** The Agent page has a **Memories** panel: what was recalled this turn (hydrated from the `memory-recalled`
  event's ids) plus a paged browse/forget list over `/api/agent-memories`.

### Skills

Agents get **skills**: procedure documents authored in git that say how this project does something.

- **What they are.** A skill is guidance the agent reads and follows — not an executable workflow, not a prompt
  template, and not a tool that acts. `skills__load` returns the text; doing the work is still the agent's job.
- **Two-stage loading.** Names and one-line descriptions of every skill an agent may use are appended to its
  instructions on **every** turn (budgeted by `Catalogue:MaxChars`; overflow is reported with an explicit "and N more"
  line, never silently dropped). Bodies are pulled in on demand with `skills__load`, so a large library costs a few
  hundred tokens a turn rather than all of it.
- **Files are the source of truth.** `skills/<name>/SKILL.md` at the repo root, with YAML frontmatter — `name`
  (required, must equal the folder name), `description` (required, ≤ 300 chars) and optional `tags: [a, b]`. The body
  is stored verbatim. The sync is **one-way, at startup only**: editing a skill while the host runs does nothing until
  restart, deliberately, so a turn can never see a half-written file.
- **Assignment.** Per agent, by glob on `Thalos:Agents[]:Skills`, exactly like `Tools`. A skill outside an agent's
  globs answers "unknown skill" — byte-identical to a name that does not exist, so the tool cannot be used to probe
  what other agents can see.
- **Where it lives.** The `Skills` table (`PostgresSkillStore`, `Daedalus.Agents`), with the **name as primary key**.
  A file that disappears deactivates its row rather than deleting it, so history and references stay resolvable.
- **Search.** `skills__search` is in-process cosine over the same Ollama `nomic-embed-text` generator memory uses.
  Without Ollama it reports unavailable and the catalogue stays authoritative — **skills never depend on Ollama being
  up**, unlike memory recall.
- **Trust boundary.** Skill bodies come from git, not from model output, so they are **not** passed through
  `IUntrustedContentScanner` the way recalled memories are. Whoever can merge a `SKILL.md` can steer the agent — the
  same trust boundary as merging code. Review skill changes like you review code.
- **Shipped procedures.** `daedalus-migrations` (adding and applying an EF Core migration here) and `thalos-release`
  (cutting and publishing a Thalos.NET release). Both are procedures this project actually executed by hand.
- **No API and no UI.** Skills are not per-user data and there is nothing to authorize per row: the repo is the UI.

### Channels

The same agents are reachable from Telegram (API host) and from a terminal (`Daedalus.Cli`), on top of the
Blazor `/agent` page. Both are built on Thalos's `ChannelPump` + `IChannelAdapter`/`IChannelSource` seam
(`Thalos.NET.Channels` + `Thalos.NET.Channels.Telegram`); Daedalus supplies durable conversation binding
(`PostgresConversationMap` → `ChannelConversations`, PK `(ChannelId, ConversationId)`) and host wiring
(`AddDaedalusChannels`, `Daedalus.Agents/Channels/DaedalusChannelsServiceCollectionExtensions.cs`). Design:
[docs/plans/2026-08-20-thalos-channels-design.md](docs/plans/2026-08-20-thalos-channels-design.md).

**Two configuration sections**, both under `Thalos:Channels` — see the [Configuration](#configuration-thalos-in-srcdaedalusapiappsettingsjson)
table above for every key:

- `Thalos:Channels` — `DefaultAgent` (**an agent `Name`, not an `Id`** — see the table above; this shipped
  wrong once and is worth re-reading), plus `Enabled`, `IdleTimeout` (default 12h) and `FlushInterval`
  (default 1s, shared by every registered channel — there is no per-channel cadence).
- `Thalos:Channels:Telegram` — `Enabled`, `BotToken`, `PrincipalId`, `AllowedUserIds`, `Roles`. Present in
  `src/Daedalus.Api/appsettings.json` as a shape only (blank token, empty allow-list); see the config comment
  right above the section for the single-instance and secrets rules repeated below.

**The bot token never lives in the repository.** Locally:
`dotnet user-secrets set Thalos:Channels:Telegram:BotToken <token> --project src/Daedalus.Api`. In the
container: the `telegram-bot-token` Aspire parameter (`src/Daedalus.AppHost/Program.cs`), forwarded as
`Thalos__Channels__Telegram__BotToken`. **An empty token is not an error** — `AddDaedalusChannels` checks
`BotToken` before calling `AddTelegramChannel` at all (`IsTelegramConfigured`), so a host with no token simply
never registers the Telegram channel: it starts cleanly and logs nothing about it. A deployer who *intended*
Telegram to run and forgot the token gets silence, not a startup failure — check that the channel is actually
registered, don't just check that the host started.

**Telegram must run single-instance.** `getUpdates` long-polling refuses concurrent pollers for one bot
token; two replicas fight over it and silently drop messages. If the API host is ever scaled past one
replica, `Thalos:Channels:Telegram:Enabled` must be `true` on exactly one of them. This is an operational
trap no test in this repo can catch — it is called out in the `appsettings.json` comment right next to the
section.

**Delivery is at-most-once, by design.** The inbound update offset is acknowledged *before* the turn runs, so
a crash mid-turn loses that message rather than silently duplicating one. The alternative — acking after —
would re-run a turn that may have already written memories or touched a repository, which is worse.

**An empty `AllowedUserIds` fails host start, on purpose** — never "allow everyone". The recommended
`Roles` posture is an **empty role set**, not an invented `"user"` role: the *absence* of `developer`/`admin`
is what stops a phone-reachable bot from running mutating Roslyn tools (`roslyn__apply_*`, `roslyn__rename_*`)
or deleting shared-owner memories. An empty role set still gives the Telegram principal full access to its
own sessions — ownership is by caller id, not by role.

**The bot token stays out of traces.** The token sits in the URL path of every Bot API call. Daedalus's service
defaults export an outgoing HTTP call's origin only, `https://api.telegram.org`, so the path and the token never
reach a trace. See [Observability](#observability). A host that does not use `AddServiceDefaults` must do the same
itself.

**`Daedalus.Cli`** is a plain console host, run by hand — it is **not** registered in the Aspire AppHost,
because it needs a real TTY and an AppHost-managed process has none:

```bash
dotnet run --project src/Daedalus.Cli
```

Six commands, identical across every channel because the pump parses them once: `/new [agent name]` (bind a
fresh session; no argument uses `DefaultAgent`), `/end`, `/status`, `/agents` (list `AgentDefinition`s),
`/cancel` (abort an in-flight turn) and `/help`. Plain text with nothing bound implicitly runs `/new`.

Two known console limitations, both observed behaviour of the shipped library (verified by hand in this
phase's manual CLI session, not documented in Thalos's XML docs) rather than bugs to chase:
- **Ctrl+C burns the host shutdown timeout.** `Console.In`'s `ReadLineAsync(ct)` does not observe
  cancellation, so a graceful shutdown request waits out the full timeout instead of returning immediately.
- **On `SessionBusy` / `SessionNotFound` / `SessionClosed` the current line is not closed.** The adapter never
  sees a terminal event for these, so the next delta starts on a fresh line instead — cosmetic only.

**The outbox has no producer in this phase.** `ChannelMessageQueued` / `ChannelMessageQueuedDispatcher`
(`ZeroAlloc.Outbox` + `ZeroAlloc.Outbox.EfCore`, migration `AddChannelMessageOutbox`) are built, tested and
wired, but **nothing writes to the outbox table** — terminal messages are delivered straight from the pump to
`IChannelAdapter.DeliverAsync`, exactly as the design's §5 "one message, edited in place" behaviour requires.
Routing terminal messages through the outbox instead would double-post every reply and leave the streamed
message's activity line dangling — see the design's [§9 correction](docs/plans/2026-08-20-thalos-channels-design.md#9-outbox-errors-and-edge-cases)
for the full account of why this was tried and reverted. The infrastructure is kept for **phase 1.5**
(proactive/unsolicited pushes from scheduled and subagent runs), which is the first producer with no live turn
to conflict with. Do not spend time wondering why terminal messages never appear in the outbox table — they
are not supposed to, yet.

### `.mcp.json` (API content root)

`src/Daedalus.Api/.mcp.json` has the same shape as Claude Code's `.mcp.json`. Tools are exposed to agents as
`<server>__<tool>` (e.g. `roslyn__find_callers`). A server that fails to start only fails the agent build for that turn
(`ProviderError`) and is retried on the next one, so CI/E2E hosts do not need `roslyn` installed.

```json
{
  "mcpServers": {
    "roslyn":   { "type": "stdio", "command": "dnx", "args": ["RoslynCodeLens.Mcp", "--yes", "--", "C:/Projects/Prive/daedalus/Daedalus.sln"] },
    "context7": { "type": "http",  "url": "https://mcp.context7.com/mcp" }
  }
}
```

> Adjust the solution path in the `roslyn` entry for your checkout.

### Authorization

- Every `/api/agents/*` and `/api/agent-memories/*` endpoint requires the `AgentUse` policy = any authenticated user.
- Sessions are owner-scoped: another user's session answers `404` (not `403`, so ids cannot be probed); users with the
  `admin` role can read/close any session.
- Memories are scoped the same way the `memory__*` tools are: a caller reads their own memories, the ones pinned to the
  agent passed as `agentId`, and the shared owner's. Foreign, archived and unknown ids answer `404`. Forgetting is
  own-only; forgetting a **shared-owner** memory additionally needs the `developer` policy (`developer` or `admin`
  role) and answers `403` otherwise.
- Mutating Roslyn tools (`roslyn__apply_*`, `roslyn__rename_*`) are bound to the `developer` policy (`Daedalus.Agents`
  `DeveloperPolicy`): the caller needs the realm role **`developer` or `admin`**. Anyone else gets a `Tool call denied`
  tool result (the turn continues) plus a `ToolCallDeniedNotification`. The bundled Keycloak realm ships an `admin` role
  (user `admin`/`admin123`); add a `developer` realm role for non-admin developers.

### Endpoints

| Method   | Route                                           | Purpose                                      | Responses                                                                        |
|----------|-------------------------------------------------|----------------------------------------------|----------------------------------------------------------------------------------|
| `GET`    | `/api/agents`                                   | List configured agents                       | `200`                                                                            |
| `POST`   | `/api/agents/{agentId}/sessions`                | Create a session owned by the caller         | `201`, `400`, `404`                                                              |
| `GET`    | `/api/agents/sessions?skip=&take=`              | Caller's sessions, newest first              | `200`                                                                            |
| `GET`    | `/api/agents/sessions/{sessionId}`              | Session header + transcript (owner or admin) | `200`, `404`                                                                     |
| `POST`   | `/api/agents/sessions/{sessionId}/turns`        | Run one turn, buffered result                | `200`, `400`, `404`, `409` busy/closed, `422` quarantined                        |
| `POST`   | `/api/agents/sessions/{sessionId}/turns/stream` | Run one turn as **SSE** (`text/event-stream`) | `200`; events below                                                              |
| `DELETE` | `/api/agents/sessions/{sessionId}`              | Close the session (terminal)                 | `204`, `404`, `409`                                                              |
| `GET`    | `/api/agent-memories?agentId=&kind=&tag=&includeArchived=&page=&pageSize=` | Memories visible to the caller, newest updated first | `200`, `400`                                          |
| `GET`    | `/api/agent-memories/{id}?agentId=`             | One visible memory (hydrates `memory-recalled` ids) | `200`, `400`, `404` (also when archived)                     |
| `DELETE` | `/api/agent-memories/{id}?hard=`                | Forget: archive (`hard=false`) or delete     | `204`, `400`, `403` (shared owner, no `developer`), `404`                        |

The SSE endpoint writes `event: <kind>` + `data: <AgentEventDto JSON>` per event, flushes each frame immediately
(response buffering/compression disabled) and always ends with `done` or `error`. Kinds:

| Kind                                                               | Payload                                              |
|---------------------------------------------------------------------|--------------------------------------------------------|
| `text-delta`, `tool-call`, `tool-result`, `usage`, `done`, `error` | The turn itself (`text`, `toolCall`, `usage`, `result`, `errorCode`/`errorMessage`/`errorDetail`). |
| `memory-recalled`                                                  | `memory.ids`, `memory.count`, `memory.chars` — what was injected into this turn. |
| `memory-stored`                                                    | `memory.memoryId`, `memory.kind`, `memory.deduped` (a near-duplicate was merged). |
| `memory-recall-failed`                                             | `memory.code` — recall failed (index down); the turn continues without memories. |
| `memory-index-pending`                                             | `memory.memoryId` — stored but not embedded yet; the sweeper will index it. |
| `memory-quarantined`                                               | `memory.memoryId`, `memory.detail` — the untrusted-content scanner dropped a recalled memory from the injected block. |

The five `memory-*` kinds put their payload in the nested `memory` object (`MemoryEventDto`); only the members relevant
to the kind are set, and unknown kinds arrive with `kind` only, so clients can ignore what they do not know.

`agentId` on the memory endpoints is the caller's **agent context**, not a filter: it widens the visible scope with that
agent's pinned memories (the Agent page passes the session's agent id). Paging happens before visibility is applied, so a
page can hold fewer than `pageSize` items and `Total` can over-count. Turn endpoints sit behind the `llm-operations`
rate limiter.

### Operational notes

- **Crash recovery:** `AgentSessionCrashRecovery` (hosted service) resets any session left in `Running` by a crashed host
  back to `Idle` before Kestrel accepts requests, so no session is stuck answering `409 SessionBusy`. An unreachable
  database is logged and skipped. Daedalus runs a single API instance; multi-instance deployments would need a lease.
- **PostgreSQL image:** Rag.NET's `rag_chunks` table (the memory index) needs the `vector` extension, so every Postgres
  instance uses **`pgvector/pgvector:pg16`** — `docker-compose.yml`, `docker-compose.full.yml`, the Aspire AppHost
  (`.WithImage("pgvector/pgvector").WithImageTag("pg16")`) and the Testcontainers fixtures. Note that only the *image*
  and the `CREATE EXTENSION` are ours: `Pgvector.EntityFrameworkCore` and every `UseVector()` call were removed in
  phase 1.2 — no EF entity maps a vector column any more, Rag.NET owns `rag_chunks` on its own connection. Existing
  data volumes created with `postgres:16` keep working after the image switch (same major version); if PostgreSQL warns
  about index or collation versions run `REINDEX DATABASE daedalus;` once, or drop the volume
  (`docker volume rm daedalus_postgres_data`) for a clean start.
- **A missing skills root fails startup.** If the host's content root has no `skills/` folder, `AddDaedalusAgents`
  throws an `InvalidOperationException` naming the resolved path. That is deliberate: an agent that silently lost every
  procedure is indistinguishable from a healthy one. The fix is the `Content` item in `Daedalus.Api.csproj` that copies
  `skills/**/SKILL.md` next to each host — **not** setting `Thalos:Skills:Enabled` to `false`, which only makes the
  silence official. Test hosts get the same copy through their `Daedalus.Api` project reference.
- **A malformed `SKILL.md` is skipped, not fatal.** A file with broken frontmatter (missing `---`, an unknown key, a
  `name` that does not match its folder, a block sequence for `tags`) is logged at warning and skipped, and the skipped
  count is logged with the sync report — a bad file costs one procedure, never the host. Check the startup log after
  adding one. Note the asymmetry: bad *files* are survivable, an unreachable *store* is not.
- **Concurrent syncs can flap a skill.** `SkillSyncService.SyncAsync` is an unguarded read-modify-write: two hosts
  syncing the same roots at once can leave a skill briefly deactivated and then reactivated, because each computes the
  "seen" set independently. Daedalus runs a single API instance, so this only matters during a rolling deploy, and it
  self-corrects on the next sync. A lease would be the fix if that ever changes.
- **`rag_chunks` dimension mismatch:** the table is created once with `Thalos:Memory:VectorDimensions` columns. Switching
  the embedding model (or that setting) makes startup fail on the mismatch. The index is a rebuildable cache, so the fix
  is to drop it and mark every memory for re-indexing, then restart:
  ```sql
  DROP TABLE rag_chunks;
  UPDATE "AgentMemories" SET "IndexPending" = true WHERE NOT "IsArchived";
  ```
  The `UPDATE` is **not** optional: `ReindexPendingMemoriesHostedService` sweeps with `PendingOnly = true`, so without it
  only memories that were already pending get embedded and everything else stays invisible to recall.
- **Ralph's learnings live in the agent memory:** phase 1.6 moved them from `StructuredLearnings` and a hand-rolled
  embedding service to the shared owner `daedalus`, kind `learning`. Recall ranks purely by similarity to the query, so
  learnings are cross-project. `HitCount` became Thalos' `RecallCount`/`LastRecalledAt`. Phase 2.8 deleted the loop and
  the write half of the learnings code; what is left is read by `ILearningsMemory` for the frozen `search_learnings`
  tool.
- **Startup order:** the AppHost uses `WaitForCompletion(migrations)` for `api` — `WaitFor` releases as soon
  as a one-shot job *starts*, which let hosts boot against an un-migrated database.
- **`Thalos:Channels:DefaultAgent` is a `Name`, not an `Id`, and nothing validates it at startup.** A wrong
  value (a ULID, a typo) only fails the first time a channel needs to bind — see [Channels](#channels).
- **Telegram is single-instance only.** `getUpdates` refuses concurrent pollers; scaling the API host past one
  replica requires `Thalos:Channels:Telegram:Enabled` to be `true` on exactly one of them — see [Channels](#channels).
- **The channel outbox has no producer yet.** `ChannelMessageQueued` is wired and tested but nothing writes to
  it in this phase — terminal messages are delivered directly by the pump. Phase 1.5 is the first writer. See
  [Channels](#channels).
- **Thalos.NET feed:** the packages come from nuget.org (`Thalos.NET*` 0.4.0 in `Directory.Packages.props`, eleven
  packages incl. `Thalos.NET.Channels` and `Thalos.NET.Channels.Telegram`). For unreleased Thalos changes use
  `scripts/pack-local.ps1` in the Thalos.NET repo and add its folder as a source temporarily.

---

## Authentication & Keycloak Setup

Daedalus uses **Keycloak** as the OIDC (OpenID Connect) identity provider for both the API and Web frontend.
Keycloak is integrated into the .NET Aspire AppHost and starts automatically alongside PostgreSQL.

### Quick Start

#### 1a. Via Aspire (recommended)

```bash
# Keycloak starts automatically with the Aspire AppHost
dotnet run --project src/Daedalus.AppHost

# Keycloak URL shown in Aspire Dashboard at http://localhost:17300
```

#### 1b. Via Docker Compose (standalone)

```bash
# Start PostgreSQL, Keycloak, and pgAdmin
docker compose up -d

# Keycloak will be available at http://localhost:8082
```

**Service Endpoints**:

| Service    | Port             | URL                                            |
|------------|------------------|------------------------------------------------|
| Keycloak   | `localhost:8082` | Admin Console: http://localhost:8082/admin     |
| PostgreSQL | `localhost:5432` | Username: `daedalus` / Password: `daedalus`    |
| pgAdmin    | `localhost:5050` | Email: `admin@example.com` / Password: `admin` |

#### 2. Access Keycloak Admin Console

1. Navigate to http://localhost:8082/admin
2. Login with:
    - **Username**: `admin`
    - **Password**: `changeme`

#### 3. Verify Realm & Clients

The `keycloak-realm.json` is automatically imported on startup, creating:

- **Realm**: `daedalus`
- **Clients**:
    - `daedalus-api` — Backend API (confidential client)
    - `daedalus-wasm` — Web frontend (public client)
- **Test Users**:
    - `dev` / `dev123` — Development user
    - `admin` / `admin123` — Admin user

#### 4. Verify OIDC Configuration

Check that Keycloak is properly configured by visiting the OpenID Connect metadata endpoint:

```bash
curl http://localhost:8082/realms/daedalus/.well-known/openid-configuration
```

This endpoint returns issuer, token endpoints, JWKS URI, and other OIDC discovery information.

### Architecture

```
┌─────────────────┐
│   Blazor WASM   │
│ (daedalus-wasm) │──┐
└─────────────────┘  │
                     ├──→ Keycloak (8082)
┌─────────────────┐  │    • Authenticates users
│   REST API      │  │    • Issues JWT tokens
│ (daedalus-api)  │──┘    • Validates tokens
└─────────────────┘
        ↓
   PostgreSQL
    (5432)
```

### API Authorization

All API endpoints require JWT authentication via the `[Authorize]` attribute:

```csharp
[Authorize]
[ApiController]
[Route("api/tasks")]
public class TasksController : ControllerBase { }
```

**Request Flow**:

1. Web frontend logs in via Keycloak OAuth 2.0 authorization code flow
2. User receives access token (JWT)
3. Web attaches token to API requests: `Authorization: Bearer <token>`
4. API validates token signature against Keycloak's JWKS endpoint
5. If valid, request is processed; otherwise, returns `401 Unauthorized`

### Web Frontend Authentication

The Blazor WASM frontend uses OIDC authentication with automatic token handling:

```csharp
// In Daedalus.Web/Program.cs
builder.Services.AddOidcAuthentication(options =>
{
    builder.Configuration.Bind("Oidc", options.ProviderOptions);
});
```

**Key Features**:

- Automatic token refresh before expiration
- Tokens stored in browser IndexedDB
- Login/logout UI via `LoginDisplay.razor`
- `BaseAddressAuthorizationMessageHandler` auto-attaches tokens to API calls

### User Management

#### Create a New User

1. Go to Keycloak Admin Console → http://localhost:8082/admin
2. Select Realm: `daedalus`
3. Click **Users** → **Add user**
4. Fill in username and email
5. Set password: **Credentials** → **Set password**
6. Uncheck **Temporary** (so password doesn't need resetting)
7. Click **Save**

#### Assign Roles

1. Go to user's details
2. Click **Role mapping** tab
3. Assign roles as needed (currently `default-roles-daedalus`)

### Development vs Production

#### Development

- Authority: `http://localhost:8082/realms/daedalus`
- Web redirect: `http://localhost:8081/authentication/login-callback`
- API audience: `daedalus-api`
- HTTPS metadata validation: **Disabled** in `appsettings.json`

```json
{
    "Authentication": {
        "Authority": "http://keycloak:8082/realms/daedalus",
        "Audience": "daedalus-api"
    }
}
```

#### Production

- Update `Authority` to your production Keycloak instance
- Use HTTPS for all URLs
- Enable HTTPS metadata validation
- Update client secrets in Keycloak (currently `daedalus-api-secret-change-in-production`)
- Update redirect URIs to production domain
- Use strong admin password (not `changeme`)

### Troubleshooting

| Issue                           | Cause                                         | Solution                                                       |
|---------------------------------|-----------------------------------------------|----------------------------------------------------------------|
| `401 Unauthorized` on API calls | Token validation failed                       | Check token expiration and signature in jwt.io                 |
| Login redirects to blank page   | Keycloak not ready or redirect URI mismatch   | Verify Keycloak is healthy, check CORS settings                |
| `Invalid client ID`             | Client not found in Keycloak realm            | Verify `daedalus-wasm` client exists in Keycloak               |
| CORS errors in browser          | Web origin not in Keycloak client web origins | Add `http://localhost:8081` to CORS settings in Keycloak       |
| Database connection error       | Keycloak cannot connect to PostgreSQL         | Ensure PostgreSQL is running and keycloak user has permissions |

### Further Reading

- [Keycloak Documentation](https://www.keycloak.org/documentation)
- [OpenID Connect Protocol](https://openid.net/connect/)
- [RFC 6749 - OAuth 2.0 Authorization Framework](https://tools.ietf.org/html/rfc6749)

---

## Running Tests

### Test Suite Overview

| Project                              | Framework          | Scope                                     | Docker Required      |
|--------------------------------------|--------------------|-------------------------------------------|----------------------|
| `Daedalus.Tests.Unit`                | xUnit              | General utilities, Spectre Console        | No                   |
| `Daedalus.Tests.Unit.Domain`         | xUnit              | Domain entities, value objects            | No                   |
| `Daedalus.Tests.Unit.Application`    | xUnit              | CQRS handlers, services                   | No                   |
| `Daedalus.Tests.Unit.Infrastructure` | xUnit              | LLM services, agent factory               | No                   |
| `Daedalus.Tests.Integration`         | xUnit              | Database with Testcontainers (`pgvector/pgvector:pg16`), agent controllers + SSE | **Yes** |
| `Daedalus.Tests.Playwright.Api`      | NUnit              | API endpoint E2E                          | **Yes**              |
| `Daedalus.Tests.Playwright.Browser`  | NUnit + Playwright | Browser E2E                               | **Yes** + Playwright |

### Testing Libraries

| Library                      | Version | Purpose                                                |
|------------------------------|---------|--------------------------------------------------------|
| NSubstitute                  | 5.3.0   | Mocking (sole mocking library, Moq removed)            |
| NSubstitute.Analyzers.CSharp | 1.0.17  | Compile-time NSubstitute usage validation              |
| AwesomeAssertions            | 7.0.0   | Fluent assertions (community fork of FluentAssertions) |
| Bogus                        | 35.6.5  | Test data generation                                   |
| Respawn                      | 7.0.0   | Database cleanup between integration tests             |
| Testcontainers.PostgreSql    | 4.14.0  | PostgreSQL Docker containers for integration tests (image `pgvector/pgvector:pg16`) |
| Thalos.NET.Testing           | 0.4.0   | `ScriptedChatClient`, in-memory stores, `MemoryStoreContractTests`, `SkillStoreContractTests` |
| TngTech.ArchUnitNET.xUnit    | 0.13.2  | Layer/boundary rules (`CleanArchitectureTests`)        |

### Commands

```bash
# All unit tests (no Docker needed)
dotnet test --filter "FullyQualifiedName~Tests.Unit"

# Individual test suites
dotnet test tests/Daedalus.Tests.Unit.Domain
dotnet test tests/Daedalus.Tests.Unit.Application
dotnet test tests/Daedalus.Tests.Unit.Infrastructure

# Integration tests (requires Docker for Testcontainers)
dotnet test tests/Daedalus.Tests.Integration

# All tests
dotnet test

# With code coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Playwright E2E Setup (one-time)

Playwright tests require browser binaries. Install them once:

```bash
# Build the Playwright test projects first
dotnet build tests/Daedalus.Tests.Playwright.Browser

# Install Playwright browsers (Chromium, Firefox, WebKit)
pwsh tests/Daedalus.Tests.Playwright.Browser/bin/Debug/net10.0/playwright.ps1 install
```

Then run:

```bash
# API E2E tests
dotnet test tests/Daedalus.Tests.Playwright.Api

# Browser E2E tests
dotnet test tests/Daedalus.Tests.Playwright.Browser
```

> If you see errors about missing browser executables, re-run the `playwright.ps1 install` command above.

### Notes for the Thalos agent tests

- Integration tests that touch `ApplicationDbContext` build their options with `PostgresFixture.CreateDbContextOptions()`.
  The fixture container is `pgvector/pgvector:pg16` and installs the `vector` extension for Rag.NET's `rag_chunks`; no EF
  registration configures a vector plugin any more (`Pgvector.EntityFrameworkCore` is gone).
- `PostgresMemoryStore` is verified against Thalos' own `MemoryStoreContractTests` (21 facts) on a real Postgres, plus
  Daedalus facts for keyset streaming and the AND tag filter. The `AddAgentMemories` migration has its own tests
  (`StructuredLearnings` → `AgentMemories` copy, and that the whole `Down` chain still runs).
- The Browser suite hosts the WASM app in-process (`E2EServerFixture`, `TestMode` appsettings override) with the real API
  composition root and a scripted `IAgentRuntime` stub — no model, no MCP servers. `AgentPageBrowserTests` writes its
  screenshot to `TestResults/…/regression-screenshots/` by default; set `DAEDALUS_REGRESSION_SCREENSHOTS=1` to write into
  `docs/regression-screenshots/` for a regression report.
- Keycloak-backed integration tests are filtered out in day-to-day runs:
  `dotnet test tests/Daedalus.Tests.Integration --filter "FullyQualifiedName!~Keycloak&FullyQualifiedName!~Authentication"`.

---

## Running Benchmarks

BenchmarkDotNet performance benchmarks are in `benchmarks/Daedalus.Benchmarks/`:

```bash
# Run all benchmarks (must use Release config)
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release

# Run a specific benchmark class
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release --filter "StringValidationBenchmarks"

# Run a specific method
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release --filter "AllocationBenchmarks.StringBuilderConcatenation"
```

| Benchmark Suite                        | Focus                                           |
|----------------------------------------|-------------------------------------------------|
| `StringValidationBenchmarks`           | Zero-allocation string validation               |
| `AllocationBenchmarks`                 | Memory efficiency patterns                      |
| `CommandHandlerBenchmarks`             | Write operation hotpath performance             |
| `QueryHandlerBenchmarks`               | Read operation query patterns                   |
| `RailwayOrientedProgrammingBenchmarks` | `Result<T>` monadic chain performance           |
| `DtoMappingBenchmarks`                 | DTO mapping overhead                            |
| `JsonSerializationBenchmarks`          | JSON serialization strategies                   |
| `LlmResponseBenchmarks`                | LLM response processing                         |
| `DomainEntityBenchmarks`               | Entity creation, state transitions, collections |
| `DependencyResolutionBenchmarks`       | Dependency graph resolution                     |
| `ResponseExtractionBenchmarks`         | LLM response extraction & prompt injection      |

Results are output to `BenchmarkDotNet.Artifacts/results/`. See
[benchmarks/Daedalus.Benchmarks/README.md](benchmarks/Daedalus.Benchmarks/README.md) for detailed documentation.

---

## Docker Compose (without Aspire)

Two Docker Compose files are provided for running without Aspire:

### Database Only

```bash
# Start PostgreSQL + pgAdmin + Keycloak
docker compose up -d
```

| Service    | Port             | Credentials                                              |
|------------|------------------|----------------------------------------------------------|
| PostgreSQL | `localhost:5432` | `daedalus` / `daedalus` (image `pgvector/pgvector:pg16`) |
| pgAdmin    | `localhost:5050` | `admin@example.com` / `admin`                            |
| Keycloak   | `localhost:8082` | `admin` / `changeme` (admin console)                     |

> The Postgres image changed from `postgres:16` to `pgvector/pgvector:pg16` (needed by the `vector` column migrations).
> Existing volumes keep working; run `REINDEX DATABASE daedalus;` once if PostgreSQL warns about index versions, or drop
> the volume for a fresh start.

### Full Stack

```bash
# Start all services (DB, Keycloak, migrations, API, web, pgAdmin)
docker compose -f docker-compose.full.yml up -d
```

| Service        | Port             | Notes                                       |
|----------------|------------------|---------------------------------------------|
| PostgreSQL     | `localhost:5432` | DB name: `daedalus` (houses app + Keycloak) |
| Keycloak       | `localhost:8082` | OIDC provider, auto-imports realm           |
| API            | `localhost:8080` | Health check: `GET /health`, requires auth  |
| Web            | `localhost:8081` | Blazor WASM frontend, login via Keycloak    |
| pgAdmin        | `localhost:5050` | Database management UI                      |

---

## Development Guidelines

### Code Formatting

```bash
# Format code (required before commits)
dotnet format
```

The project uses `.editorconfig` for consistent style rules enforced by `dotnet format` and the three code analyzers.

### Key Principles

- **Railway-Oriented Programming**: Use `Result<T>` instead of exceptions for expected failures
- **Primary Constructors**: C# 12+ primary constructors for dependency injection
- **Zero-Allocation**: Use ZLinq, `Span<T>`, `ArrayPool<T>`, `stackalloc` in hot paths
- **Async Best Practices**: Always pass `CancellationToken`, never block on async, use `ConfigureAwait(false)` in libs
- **EF Core**: `AsNoTracking()` for reads, `ExecuteUpdateAsync()` for bulk ops, DbContext pooling
- **Compile-Time Logging**: `[LoggerMessage]` attributes for zero-allocation logging
- **Testing**: NSubstitute for mocking, AwesomeAssertions for fluent assertions, Bogus for test data

See [CLAUDE.md](CLAUDE.md) and the [development guide](docs/development-guide.md) for the coding standards, patterns, and
forbidden anti-patterns. `.github/copilot-instructions.md` is superseded.

### Commits, versioning and releases

Same setup as [Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET) and
[Thalos.NET](https://github.com/MarcelRoozekrans/Thalos.NET); the runbook is [docs/release.md](docs/release.md).

- **Conventional commits** (`feat:`, `fix:`, `chore:`, … — rules in `.commitlintrc.yml`), enforced on pull requests
  by the `commitlint` CI job. release-please reads them to propose the next version.
- **Version** comes from git history via [GitVersion](GitVersion.yml) (`dotnet tool restore && dotnet dotnet-gitversion`);
  CI stamps it into every assembly (`-p:Version`) and image (`APP_VERSION` build-arg). Stable versions only — no
  prereleases. Nothing is hand-edited: `VersionPrefix` in `Directory.Build.props` is only the fallback for a plain
  local build.
- **Releases** are cut by [release-please](.github/workflows/release-please.yml): dispatch → review/merge the
  `chore(main): release X.Y.Z` PR → dispatch → `vX.Y.Z` tag + GitHub release. Then a manual CI dispatch with
  `publish_release=true` pushes the `daedalus-api` and `daedalus-web` images to ghcr.io as
  `X.Y.Z`, `X.Y` and `latest` — only from the tagged commit. Every push to `main` publishes the moving tip as
  `<sha>` and `main`. The `daedalus-console` image ended with phase 2.8: an external compose file that still pulls
  `daedalus-console:latest` keeps a frozen worker running against the new schema.

---

## Troubleshooting

### Aspire / DCP Issues

| Problem                            | Solution                                                                                                               |
|------------------------------------|------------------------------------------------------------------------------------------------------------------------|
| DCP not starting                   | Ensure Docker Desktop is running, WSL 2 enabled (`wsl --list --verbose`)                                               |
| Dashboard OTLP errors              | Set `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` and `*_HTTP_ENDPOINT_URL` before running                                      |
| Ports in use (17300, 18889, 18890) | Stop conflicting processes or change ports in `launchSettings.json`                                                    |
| Aspire CLI not found               | Run `dotnet tool restore` or reinstall: `Invoke-RestMethod -Uri "https://aspire.dev/install.ps1" \| Invoke-Expression` |
| PostgreSQL container fails         | Check Docker is running, port 5432 is free, remove stale volumes: `docker volume rm daedalus_postgres_data`            |

### Build Issues

| Problem                    | Solution                                                                             |
|----------------------------|--------------------------------------------------------------------------------------|
| Warnings treated as errors | All projects have `TreatWarningsAsErrors=true`. Fix the warning or add to `<NoWarn>` |
| Missing WASM workload      | Run `dotnet workload install wasm`                                                   |
| Missing tools              | Run `dotnet tool restore` to get `dotnet-ef` and `aspire` CLI                        |

### Test Issues

| Problem                         | Solution                                                                                                |
|---------------------------------|---------------------------------------------------------------------------------------------------------|
| Integration tests fail (Docker) | Ensure Docker is running — Testcontainers provisions PostgreSQL at runtime                              |
| Playwright: missing browsers    | Run `pwsh tests/Daedalus.Tests.Playwright.Browser/bin/Debug/net10.0/playwright.ps1 install`             |
| Testhost process hangs          | Kill stale processes: `Get-Process -Name testhost -ErrorAction SilentlyContinue \| Stop-Process -Force` |

---

## Documentation

| Document                                                 | Description                                       |
|----------------------------------------------------------|---------------------------------------------------|
| [Architecture Diagrams](docs/architecture-diagrams.md)   | 14+ Mermaid diagrams covering the full system     |
| [Thalos agent core design](docs/plans/2026-08-16-thalos-agent-core-design.md) | Design of the Thalos.NET-based agent stack (phase 1.1) |
| [Roadmap](docs/planning/ROADMAP.md)                      | Milestone 1 phases and status                     |
| [Regression report 2026-08-16](docs/regression-report-2026-08-16.md) | Browser regression evidence for the Agent page  |
| [Ralph Wiggum Technique](docs/ralph-wiggum-technique.md) | The retired iteration loop, kept as history       |
| [Development Guide](docs/development-guide.md)           | C# patterns, performance, forbidden anti-patterns; see also CLAUDE.md |
| [Context7 Auto Usage](.github/context7-auto-usage.md)    | When to query Context7 for library documentation  |
