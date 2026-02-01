# Daedalus

[![CI](https://github.com/MarcelRoozekrans/daedalus/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/MarcelRoozekrans/daedalus/actions/workflows/ci.yml)
[![Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/MarcelRoozekrans/883ece1e357faef9d6bdfb459e31fe66/raw/daedalus-coverage.json)](https://github.com/MarcelRoozekrans/daedalus/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-13.0-239120?logo=csharp)](https://learn.microsoft.com/dotnet/csharp/)
[![Docker](https://img.shields.io/badge/Docker-ghcr.io-2496ED?logo=docker)](https://github.com/MarcelRoozekrans/daedalus/pkgs/container/)

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
| **Shared**               | `Application` + `Infrastructure` + `Domain` | CQRS handlers, EF Core repositories, PostgreSQL persistence     |

The Console worker bypasses HTTP to minimise latency (5-second polling cycles). Both layers use the same CQRS services
and repositories.

For detailed diagrams covering component interactions, data flow, Git integration, and multi-worker coordination, see
[Architecture Diagrams](docs/architecture-diagrams.md) (13+ Mermaid diagrams).

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
| Database             | PostgreSQL 16                                                                       |
| ORM                  | Entity Framework Core 10 (Npgsql)                                                   |
| Pattern Library      | CSharpFunctionalExtensions (Railway-Oriented Programming)                           |
| Zero-Allocation LINQ | ZLinq 1.5.4                                                                         |
| LLM Providers        | GitHub Copilot SDK 0.1.21, Anthropic (Claude) 1.0.0                                 |
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
├── Daedalus.Api/              # REST controllers, JWT auth, health checks
├── Daedalus.Console/          # Ralph Loop worker (background hosted service)
├── Daedalus.Web/              # Blazor WASM frontend (Radzen components)
└── Daedalus.Migrations/       # EF Core database migration runner

tests/
├── Daedalus.Tests.Unit/              # General unit tests (xUnit)
├── Daedalus.Tests.Unit.Domain/       # Domain layer unit tests (xUnit)
├── Daedalus.Tests.Unit.Application/  # Application layer unit tests (xUnit)
├── Daedalus.Tests.Unit.Infrastructure/ # Infrastructure unit tests (xUnit)
├── Daedalus.Tests.Integration/       # Integration tests with Testcontainers (xUnit)
├── Daedalus.Tests.Playwright.Api/    # API E2E tests (NUnit + Playwright)
└── Daedalus.Tests.Playwright.Browser/ # Browser E2E tests (NUnit + Playwright)

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
2. **PostgreSQL 16** container starts (with persistent data volume)
3. **Keycloak 26.0** container starts (with realm auto-import from `keycloak-realm.json`)
4. **Migrations** run automatically (`Daedalus.Migrations`, waits for DB + Keycloak)
5. **API** starts (REST + JWT auth via Keycloak, port `5000`)
6. **Console Worker** starts (Ralph Loop, direct DB polling)
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

## Configuration

### Environment Variables

| Variable                                  | Required     | Default                  | Purpose                                               |
|-------------------------------------------|--------------|--------------------------|-------------------------------------------------------|
| `PARAMETERS__DB_USERNAME`                 | Yes (Aspire) | `postgres`               | PostgreSQL username                                   |
| `PARAMETERS__DB_PASSWORD`                 | Yes (Aspire) | `postgres`               | PostgreSQL password                                   |
| `ANTHROPIC_API_KEY`                       | No           | —                        | Claude API key (fallback if not set in `appsettings`) |
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
| `Daedalus.Tests.Integration`         | xUnit              | Database with Testcontainers              | **Yes**              |
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
| Testcontainers.PostgreSql    | 4.10.0  | PostgreSQL Docker containers for integration tests     |

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

| Service    | Port             | Credentials                          |
|------------|------------------|--------------------------------------|
| PostgreSQL | `localhost:5432` | `daedalus` / `daedalus`              |
| pgAdmin    | `localhost:5050` | `admin@example.com` / `admin`        |
| Keycloak   | `localhost:8082` | `admin` / `changeme` (admin console) |

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
| [Architecture Diagrams](docs/architecture-diagrams.md)   | 13+ Mermaid diagrams covering the full system     |
| [Ralph Wiggum Technique](docs/ralph-wiggum-technique.md) | AI iteration loop methodology                     |
| [Coding Standards](.github/copilot-instructions.md)      | C# patterns, performance, forbidden anti-patterns |
| [Context7 Auto Usage](.github/context7-auto-usage.md)    | When to query Context7 for library documentation  |
