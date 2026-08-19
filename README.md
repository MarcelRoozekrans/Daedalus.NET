# Daedalus

[![CI](https://github.com/MarcelRoozekrans/Daedalus.NET/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/MarcelRoozekrans/Daedalus.NET/actions/workflows/ci.yml)
[![Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/MarcelRoozekrans/883ece1e357faef9d6bdfb459e31fe66/raw/daedalus-coverage.json)](https://github.com/MarcelRoozekrans/Daedalus.NET/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-13.0-239120?logo=csharp)](https://learn.microsoft.com/dotnet/csharp/)
[![Docker](https://img.shields.io/badge/Docker-ghcr.io-2496ED?logo=docker)](https://github.com/MarcelRoozekrans/Daedalus.NET/pkgs/container/)

High-performance .NET 10 application using Railway-Oriented Programming for AI-driven task execution with LLM iteration
loops. Features dual presentation layers (Blazor Web + Console Worker) sharing one Application + Infrastructure stack,
orchestrated via .NET Aspire.

## Table of Contents

- [About the Name](#about-the-name)
- [Architecture Overview](#architecture-overview)
- [The Ralph Wiggum Technique](#the-ralph-wiggum-technique)
- [Tech Stack](#tech-stack)
- [Project Structure](#project-structure)
- [Prerequisites](#prerequisites)
- [Setup](#setup)
- [Running the Application](#running-the-application)
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

Daedalus has **dual presentation layers** sharing one Application + Infrastructure stack:

| Layer                    | Project                                     | Purpose                                                         |
|--------------------------|---------------------------------------------|-----------------------------------------------------------------|
| **Web** (Blazor WASM)    | `Daedalus.Api` + `Daedalus.Web`             | REST API + browser UI via Radzen components                     |
| **Console** (Ralph Loop) | `Daedalus.Console`                          | Background worker with direct DB access for low-latency polling |
| **Agents** (Thalos)      | `Daedalus.Agents` (hosted in `Daedalus.Api`) | Thalos.NET agent stack: sessions, tools, AI.Sentinel, SSE chat  |
| **Shared**               | `Application` + `Infrastructure` + `Domain` | CQRS handlers, EF Core repositories, PostgreSQL persistence     |

The Console worker bypasses HTTP to minimise latency (5-second polling cycles). Both layers use the same CQRS services
and repositories.

For detailed diagrams covering component interactions, data flow, Git integration, and multi-worker coordination, see
[Architecture Diagrams](docs/architecture-diagrams.md) (14+ Mermaid diagrams, including the Thalos agent turn).

---

## The Ralph Wiggum Technique

This project demonstrates the **Ralph Wiggum AI Loop Technique** — an iterative AI development methodology that feeds
an AI agent the same prompt repeatedly until a completion signal is received:

- **Iteration > Perfection**: Automated loops refine work iteratively
- **Failures Are Data**: Deterministic failures are informative and drive progress
- **Clear Completion Criteria**: Explicit success conditions and output markers
- **Automatic Verification**: Tests, linters, and type checkers as built-in gates
- **Subagent Delegation**: Primary context window acts as scheduler, spawning isolated subagents for expensive work

See [Ralph Wiggum Technique Documentation](docs/ralph-wiggum-technique.md) for detailed guidance.

---

## Tech Stack

| Category             | Technology                                                                          |
|----------------------|-------------------------------------------------------------------------------------|
| Language             | C# 13.0                                                                             |
| Framework            | .NET 10                                                                             |
| Orchestration        | .NET Aspire 13.1.0                                                                  |
| Database             | PostgreSQL 16 with pgvector (`pgvector/pgvector:pg16`)                              |
| ORM                  | Entity Framework Core 10 (Npgsql 10.0.3)                                            |
| Pattern Library      | CSharpFunctionalExtensions (Railway-Oriented Programming)                           |
| ZeroAlloc            | ZeroAlloc.Results 1.2.0, ZeroAlloc.Authorization 2.1.0, ZeroAlloc.Validation 1.5.6, ZeroAlloc.Mapping 1.6.1 (via Thalos.NET) |
| Zero-Allocation LINQ | ZLinq 1.5.4                                                                         |
| Agent framework      | Thalos.NET 0.3.0 (nuget.org) on Microsoft Agent Framework 1.17, Microsoft.Extensions.AI 10.9         |
| Agent memory         | `Thalos.NET.Memory` 0.3.0 + `Thalos.NET.Memory.RagNet` 0.3.0 (→ Rag.NET 0.1.x pgvector store, `rag_chunks`) |
| Agent skills         | `Thalos.NET.Skills` 0.3.0 (markdown procedures from git, in-process cosine search, no Rag.NET dependency) |
| Embeddings           | Ollama `nomic-embed-text` (768 dims) via OllamaSharp 5.4.12                          |
| LLM security         | AI.Sentinel 2.0.1 (via `Thalos.NET.Sentinel`)                                       |
| LLM Providers        | GitHub Copilot SDK 0.1.21 (Ralph), Anthropic SDK 12.40.0 (Ralph + Thalos)           |
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
├── Daedalus.Api/              # REST controllers (incl. /api/agents + SSE), JWT auth, health checks, .mcp.json for Thalos
├── Daedalus.Console/          # Ralph Loop worker (background hosted service)
├── Daedalus.Web/              # Blazor WASM frontend (Radzen components)
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

The project uses **.NET Aspire 13.1.0** with DCP for orchestration. All services (PostgreSQL, migrations, API, Console
worker, frontend) are started and wired together automatically.

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
5. **Migrations** run automatically (`Daedalus.Migrations`, waits for DB + Keycloak). API and Console wait for the job to
   *complete* (`WaitForCompletion`), not merely to start, so no host boots against an un-migrated database
6. **API** starts (REST + JWT auth via Keycloak, port `5000`; creates the Rag.NET `rag_chunks` schema)
7. **Console Worker** starts (Ralph Loop, direct DB polling)
8. **Web Frontend** starts (Blazor WASM, OIDC login via Keycloak)
9. **Aspire Dashboard** available with real-time monitoring

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

## Configuration

### Environment Variables

| Variable                                  | Required     | Default                  | Purpose                                               |
|-------------------------------------------|--------------|--------------------------|-------------------------------------------------------|
| `PARAMETERS__DB_USERNAME`                 | Yes (Aspire) | `postgres`               | PostgreSQL username                                   |
| `PARAMETERS__DB_PASSWORD`                 | Yes (Aspire) | `postgres`               | PostgreSQL password                                   |
| `ANTHROPIC_API_KEY`                       | No           | —                        | Claude API key (Ralph fallback; required for Thalos agent turns) |
| `DAEDALUS_REGRESSION_SCREENSHOTS`         | No           | —                        | `1` → Browser E2E writes regression screenshots into `docs/regression-screenshots/` |
| `GITHUB_TOKEN`                            | No           | —                        | GitHub API authentication for Git operations          |
| `GITLAB_TOKEN`                            | No           | —                        | GitLab API authentication                             |
| `AZURE_DEVOPS_TOKEN`                      | No           | —                        | Azure DevOps API authentication                       |
| `E2E_BASE_URL`                            | No           | —                        | Override base URL for Playwright E2E tests            |
| `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL`      | Yes (Aspire) | `http://localhost:18889` | OTLP gRPC endpoint                                    |
| `ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL` | Yes (Aspire) | `http://localhost:18890` | OTLP HTTP endpoint                                    |
| `ASPIRE_ALLOW_UNSECURED_TRANSPORT`        | No           | `true` (dev)             | Allow HTTP transport in dev                           |

### Application Settings (`appsettings.json`)

Both the **API** and **Console** projects have their own `appsettings.json` with the following key sections:

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

```json
{
    "ExternalServices": {
        "Llm": {
            "Provider": "copilot",
            "Timeout": 30000,
            "Claude": {
                "Enabled": false,
                "ApiKey": null,
                "Model": "claude-sonnet-4-20250514",
                "MaxTokens": 8192,
                "Timeout": 60000,
                "MaxParallelSubagents": 10,
                "SubagentModel": null
            }
        }
    }
}
```

| Setting                           | Description                                            |
|-----------------------------------|--------------------------------------------------------|
| `Llm.Provider`                    | Default provider: `"copilot"` or `"claude"`            |
| `Llm.Claude.Enabled`              | Set to `true` to register the Claude provider          |
| `Llm.Claude.ApiKey`               | Anthropic API key (or set `ANTHROPIC_API_KEY` env var) |
| `Llm.Claude.Model`                | Claude model identifier                                |
| `Llm.Claude.MaxParallelSubagents` | Max concurrent subagent invocations                    |

#### Copilot Configuration (Console only)

```json
{
    "Copilot": {
        "Model": "gpt-4",
        "CliPath": null,
        "LogLevel": "warning"
    }
}
```

#### Ralph Loop Configuration

```json
{
    "RalphLoop": {
        "IterationDelayMs": 100,
        "MaxConsecutiveFailures": 5,
        "MaxIterations": 0,
        "RequestTimeoutSeconds": 300,
        "EnableDetailedLogging": false
    }
}
```

| Setting                  | Description                              |
|--------------------------|------------------------------------------|
| `MaxIterations`          | `0` = unlimited iterations               |
| `MaxConsecutiveFailures` | Abort threshold for consecutive failures |
| `RequestTimeoutSeconds`  | Per-LLM-request timeout                  |

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
        },
        "Context7": {
            "Enabled": true,
            "Timeout": 10000,
            "ApiKey": null
        }
    }
}
```

> `Context7.ApiUrl` defaults to `https://context7.com/api` and only needs to be set when using a custom endpoint.

#### Repository Platform Tokens

Git operations support GitHub, GitLab, and Azure DevOps. Tokens are read from environment variables:

| Platform     | Env Variable         | Config Section                           |
|--------------|----------------------|------------------------------------------|
| GitHub       | `GITHUB_TOKEN`       | `ExternalServices:Platforms:GitHub`      |
| GitLab       | `GITLAB_TOKEN`       | `ExternalServices:Platforms:GitLab`      |
| Azure DevOps | `AZURE_DEVOPS_TOKEN` | `ExternalServices:Platforms:AzureDevOps` |

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
AI.Sentinel at the model boundary). It runs **alongside** the Ralph Loop as a strangler: nothing in Ralph changes until
phase 1.6 retires it. Design: [docs/plans/2026-08-16-thalos-agent-core-design.md](docs/plans/2026-08-16-thalos-agent-core-design.md);
sequence diagram: [Architecture Diagrams §14](docs/architecture-diagrams.md#14-agent-turn-thalos).

Phase 1.2 adds **memory** on top: `Thalos.NET.Memory` + `Thalos.NET.Memory.RagNet` (Thalos.NET 0.2.0). Design:
[docs/plans/2026-08-17-thalos-memory-design.md](docs/plans/2026-08-17-thalos-memory-design.md); see
[Memory](#memory) below.

Phase 1.3 adds **skills** on top: `Thalos.NET.Skills` (Thalos.NET 0.3.0), a library of markdown procedures authored in
git that agents can see the titles of every turn and pull into context on demand. Design:
[docs/plans/2026-08-18-thalos-skills-design.md](docs/plans/2026-08-18-thalos-skills-design.md); see
[Skills](#skills) below.

**What you get:** a signed-in user opens `/agent` in the Blazor app, picks an agent (default: *Daedalus Architect*),
starts a session and chats. Each turn is streamed back as Server-Sent Events (text deltas, tool calls/results, usage,
memory events), tools come from MCP servers (`roslyn__*`, `context7__*`), local knowledge tools (`daedalus__*` — failure
patterns), memory tools (`memory__remember/recall/forget/list`) and skill tools (`skills__load`, `skills__search`),
relevant memories are injected before each turn, a catalogue of available procedures is appended to the agent's
instructions, and every prompt/response passes through AI.Sentinel. Sessions, transcripts, memories and skills are
persisted in PostgreSQL (`AgentSessions`, `AgentMessages`, `AgentMemories`, `Skills`).

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
            "RalphRecall": { "TopK": 10, "MinScore": 0.5 },
            "Reindex": { "Enabled": true, "StartupDelay": "00:00:10", "RetryInterval": "00:02:00", "SweepInterval": "00:15:00" }
        },
        "Skills": {
            "Enabled": true,
            "Roots": [ "skills" ],
            "Catalogue": { "MaxChars": 2000 },
            "Search": { "TopK": 5, "MinScore": 0.6 }
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
| `Thalos:Memory:Enabled`                        | Master switch for memory: auto-recall, `memory__*` tools, the Ralph learnings paths and the reindex sweeper.                                                                                                 |
| `Thalos:Memory:SharedOwnerId`                  | Owner of host-written project knowledge (Ralph learnings). Recalled for every caller, written only by host code; deleting one needs the `developer` policy. Default `daedalus`.                              |
| `Thalos:Memory:Recall:{TopK,MinScore,MaxChars}`| Auto-recall budget per turn (and for `memory__recall`): how many memories, the cosine-similarity floor and the character cap on the injected block.                                                          |
| `Thalos:Memory:Dedupe:{Enabled,Threshold}`     | Thalos refuses a near-duplicate `remember` above the similarity threshold and reports the existing memory (`deduped`).                                                                                       |
| `Thalos:Memory:ExposeTools`                    | Registers the `memory` tool source. Which agents actually see `memory__*` is still decided by their `Tools` glob.                                                                                            |
| `Thalos:Memory:VectorDimensions`               | Embedding width of the Rag.NET index (`nomic-embed-text` = **768**). Must match the existing `rag_chunks` table — see [Operational notes](#operational-notes).                                              |
| `Thalos:Memory:RalphRecall:{TopK,MinScore}`    | Daedalus-only: how the Ralph enrichment middleware and the `search_learnings` MCP tool recall shared learnings. `TopK` is clamped to 1–50 (1–20 for the MCP tool).                                           |
| `Thalos:Memory:Reindex:*`                      | Daedalus-only: `ReindexPendingMemoriesHostedService` (API host only) — `Enabled`, `StartupDelay`, `RetryInterval` (index unavailable/failed rows), `SweepInterval` (all clear).                              |
| `Thalos:Skills:Enabled`                        | Master switch for the catalogue and the `skills__*` tools. `false` skips root validation entirely, so a host with no skills folder still starts.                                                              |
| `Thalos:Skills:Roots[]`                        | Folders holding `<name>/SKILL.md`, resolved against the host content root (like `McpConfigPath`). **A configured root that does not exist fails host start, on purpose** — see [Operational notes](#operational-notes). Empty by default. |
| `Thalos:Skills:Catalogue:MaxChars`             | Character budget for the catalogue block appended to the agent's instructions. Overflow is reported with an explicit "and N more" line, never silently truncated.                                             |
| `Thalos:Skills:Search:{TopK,MinScore}`         | `skills__search` defaults. Search is in-process cosine over the same Ollama generator; without it search reports unavailable and the catalogue stays authoritative.                                           |
| `Thalos:ToolPolicies[]`                        | Glob over qualified tool names → `[Policy]` name that must pass before the tool runs (authorization at the function boundary).                                                                               |
| `Thalos:Agents[]`                              | Agent definitions: stable `Id` (ULID or GUID — sessions reference it, never change it), `Name`, `Description`, `Instructions`, optional `Model`/`MaxOutputTokens`, `Tools` glob allow-list (empty → `*`).   |
| `Thalos:Agents[]:Skills`                       | Glob allow-list over skill names (`*` for all, `daedalus-*` for a family). **Empty by default** — unlike `Tools`, procedures are granted explicitly, because a catalogue costs tokens on every turn.          |
| `Thalos:Agents[]:Memory`                       | Per-agent overrides `{ Enabled, TopK }`; `null` members inherit `Thalos:Memory`. Tool visibility is *not* configured here — use the agent's `Tools` globs.                                                   |

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
- **The shared owner is Ralph's learnings.** Everything written under `Thalos:Memory:SharedOwnerId` (`daedalus`) with
  kind `learning` is recalled for every caller. The Ralph Loop writes there through the Application port
  `ILearningsMemory` (adapter `ThalosLearningsMemory` in `Daedalus.Agents`), so learnings and agent memory are one store.
- **Where it lives.** The `AgentMemories` table (`PostgresMemoryStore`, `Daedalus.Agents`) is the source of truth; the
  Rag.NET index (`rag_chunks`, same database, `vector(768)`) is a **rebuildable cache** of embeddings. Vectors are never
  stored on `AgentMemories`.
- **Embeddings.** Ollama `nomic-embed-text` (768 dims), the same client AI.Sentinel uses. Without Ollama the index probes
  as unavailable: memories are still stored, flagged `index_pending`, and are not recalled until they are indexed.
  `ReindexPendingMemoriesHostedService` (API host, `Thalos:Memory:Reindex:*`) sweeps them in the background once the
  index comes up. It sweeps **pending rows only**, so rebuilding the whole index means marking the rows pending first —
  see [Operational notes](#operational-notes).
- **Hosts.** `AddDaedalusAgents` (API) registers agents **and** memory, owns the Rag.NET schema (`EnsureSchemaOnStartup`)
  and runs the sweeper. `AddDaedalusMemory` (the `Daedalus.Console` Ralph worker) registers memory only and creates no
  schema, so the two hosts cannot race for `rag_chunks`. The two are **mutually exclusive**: calling both in one host
  throws at registration time.
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
- **Ralph's learnings live in the agent memory:** the parser is unchanged, but persistence and recall now go through the
  Application port `ILearningsMemory` (shared owner `daedalus`, kind `learning`) instead of `StructuredLearnings` and a
  hand-rolled embedding service. Consequence of the shared owner: enrichment no longer filters by project or excludes the
  current task — recall ranks purely by similarity to the task prompt, so learnings are cross-project and a task can
  recall one it produced itself. `HitCount` became Thalos' `RecallCount`/`LastRecalledAt`. Ralph is retired in phase 1.6.
- **Startup order:** the AppHost uses `WaitForCompletion(migrations)` for `api` and `console` — `WaitFor` releases as soon
  as a one-shot job *starts*, which let hosts boot against an un-migrated database.
- **Thalos.NET feed:** the packages come from nuget.org (`Thalos.NET*` 0.3.0 in `Directory.Packages.props`, nine
  packages incl. `Thalos.NET.Memory` and `Thalos.NET.Memory.RagNet`). For unreleased Thalos changes use
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
| `Daedalus.Tests.Unit.Infrastructure` | xUnit              | LLM services, factory, workspace provider | No                   |
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
| Thalos.NET.Testing           | 0.3.0   | `ScriptedChatClient`, in-memory stores, `MemoryStoreContractTests`, `SkillStoreContractTests` |
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
| `PromptBuildingBenchmarks`             | Ralph loop prompt building pipeline             |
| `DependencyResolutionBenchmarks`       | Phase orchestration dependency graphs           |
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
# Start all services (DB, Keycloak, migrations, API, web, console, pgAdmin)
docker compose -f docker-compose.full.yml up -d
```

| Service        | Port             | Notes                                       |
|----------------|------------------|---------------------------------------------|
| PostgreSQL     | `localhost:5432` | DB name: `daedalus` (houses app + Keycloak) |
| Keycloak       | `localhost:8082` | OIDC provider, auto-imports realm           |
| API            | `localhost:8080` | Health check: `GET /health`, requires auth  |
| Web            | `localhost:8081` | Blazor WASM frontend, login via Keycloak    |
| Console Worker | —                | No exposed ports (background worker)        |
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

See [copilot-instructions.md](.github/copilot-instructions.md) for the full set of coding standards, patterns, and
forbidden anti-patterns.

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
  `publish_release=true` pushes the `daedalus-api`, `daedalus-console` and `daedalus-web` images to ghcr.io as
  `X.Y.Z`, `X.Y` and `latest` — only from the tagged commit. Every push to `main` publishes the moving tip as
  `<sha>` and `main`.

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
| [Ralph Wiggum Technique](docs/ralph-wiggum-technique.md) | AI iteration loop methodology                     |
| [Coding Standards](.github/copilot-instructions.md)      | C# patterns, performance, forbidden anti-patterns |
| [Context7 Auto Usage](.github/context7-auto-usage.md)    | When to query Context7 for library documentation  |
