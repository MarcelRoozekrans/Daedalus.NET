# Daedalus Codebase Map

**Date:** 2026-08-16
**Purpose:** Ground the Milestone 1 pivot (Ralph Loop orchestrator → Hermes-style .NET agent framework with Rag.NET + AI.Sentinel) in what actually exists today.
**Branch:** `main` @ `964b4e8`

---

## 1. Tech Stack

| Category | Actual (verified in csproj) | README claims |
|---|---|---|
| Framework | .NET 10 (`net10.0`), C# 13 | ✅ matches |
| Orchestration | .NET Aspire 13.1.0 (AppHost + DCP) | ✅ matches |
| Database | PostgreSQL 16 + **pgvector** (`Pgvector.EntityFrameworkCore` 0.3.0) | pgvector not mentioned |
| ORM | EF Core 10.0.2 (Npgsql 10.0.0) | ✅ matches |
| ROP | CSharpFunctionalExtensions 3.6.0 | says 3.3.0 (props file stale) |
| Agent runtime | **Microsoft.Agents.AI 1.0.0-rc1** + `Microsoft.Agents.AI.Anthropic` + `Microsoft.Extensions.AI` 10.3.0 | not mentioned |
| LLM SDK | **Anthropic 12.8.0** | says 1.0.0 (stale) |
| Tools protocol | **ModelContextProtocol 1.0.0-rc.1** | ✅ mentioned |
| Embeddings | `OllamaSharp` 5.1.12 + Aspire Ollama (`nomic-embed-text`) | not mentioned |
| Git | LibGit2Sharp 0.31.0 | not mentioned |
| Frontend | Blazor WASM + Radzen.Blazor 8.7.5 | ✅ matches |
| Auth | Keycloak 26.1 (OIDC), JWT Bearer | ✅ matches |
| API | Controllers + Asp.Versioning + Scalar (OpenAPI UI) + rate limiting | partially |
| Testing | xUnit 2.9.3, NUnit 4.4.0, Playwright 1.58, NSubstitute, AwesomeAssertions, Bogus, Respawn, Testcontainers 4.10, **ArchUnitNET 0.13.2** | ArchUnit not mentioned |
| Analyzers | SonarAnalyzer, Meziantou, NetAnalyzers @ `latest-all`, `TreatWarningsAsErrors=true` | ✅ matches |

> **Note:** `Directory.Packages.props` exists but central package management is **not** enabled (no `ManagePackageVersionsCentrally`). Every version is pinned inline in each `.csproj`, and the props file is stale/unused. This is a latent trap — edits there have no effect.

---

## 2. Solution Layout

```
Daedalus.sln
├── src/
│   ├── Daedalus.AppHost/          Aspire orchestration (98 lines)      → refs Api, Web
│   ├── Daedalus.ServiceDefaults/  OTel + logging + DB registration     → refs Infrastructure ⚠
│   ├── Daedalus.Domain/           37 files / 2.5k lines                → refs nothing
│   ├── Daedalus.Application/      162 files / 9.1k lines               → refs Domain
│   ├── Daedalus.Infrastructure/   74 files / 15.3k lines               → refs Domain, Application
│   ├── Daedalus.Api/              22 files / 2.5k lines                → refs Domain, Application, Infrastructure, ServiceDefaults
│   ├── Daedalus.Console/          2 files / 540 lines (Ralph worker)   → refs Domain, Application, Infrastructure, ServiceDefaults
│   ├── Daedalus.Web/              24 files / 718 lines (Blazor WASM)   → refs Application ⚠
│   └── Daedalus.Migrations/       migration runner (36 lines)
├── tests/   7 projects, ~27.5k lines
└── benchmarks/Daedalus.Benchmarks/  14 files, 12 benchmark suites
```

**Dependency notes worth flagging for the pivot:**
- `ServiceDefaults → Infrastructure` is backwards from the Aspire convention (ServiceDefaults is normally leaf/standalone). It exists so `AddApplicationDatabase` can live there.
- `Web (Blazor WASM) → Application` drags the whole Application layer into the browser payload. DTOs are duplicated in `Web/Services/*Dto.cs` anyway, so the reference may be vestigial.
- Infrastructure references **Application** (not just Domain) — interfaces live in Application, implementations in Infrastructure. Enforced by ArchUnit.

---

## 3. Entry Points

| Entry point | File | What it does |
|---|---|---|
| **Aspire AppHost** | `src/Daedalus.AppHost/Program.cs` | Provisions Postgres 16 (data volume), Keycloak 26.1 (realm import + health-check workaround for dotnet/aspire#7787), **Ollama** (`nomic-embed-text`), then starts `migrations` → `console` + `api` → `web`. Fixed ports: api 5010, web 5290, keycloak 8082. |
| **REST API** | `src/Daedalus.Api/Program.cs` | 11 controllers, JWT/Keycloak auth, 7 authorization policies (`TaskManagement`, `CodeAnalysis`, `Admin`, …), rate limiting (named `llm-operations` / `write-operations` windows), response compression, restricted CORS, Scalar OpenAPI UI, `/health` with Npgsql check. |
| **Console worker** | `src/Daedalus.Console/Program.cs` → `RalphLoopWorker.cs` | `BackgroundService` running three concurrent loops: 5s task poll, 30s heartbeat, 5min stale-claim reclaim. Registers a worker session (`worker-{machine}-{guid}`) so multiple workers can share one backlog. Direct DB access, no HTTP. |
| **Blazor WASM** | `src/Daedalus.Web/Program.cs` | OIDC via Keycloak, Radzen UI, 10 pages. |
| **Migrations** | `src/Daedalus.Migrations/Program.cs` | Standalone runner; Console is kept AOT-friendly by not running migrations itself. |

---

## 4. The Agentic Core (what the pivot will replace or absorb)

This is the part that matters most for Milestone 1. **A Microsoft Agent Framework foundation already exists** — the pivot is less green-field than the roadmap implies.

### 4.1 Middleware pipeline (`Application`)

`IRalphLoopMiddleware` (`Order`, `InvokeAsync(context, continuation, ct)`) is an ASP.NET-style composable pipeline. `RalphLoopPipelineService` builds the chain inside-out per iteration, then records a `TaskExecution` row (prompt, response, duration, **input/output tokens, model id**) and checks termination.

Registered stages (`RalphLoopMiddlewareExtensions`):

1. `LearningsEnrichmentMiddleware` — injects prior learnings (mode-switches on `IKnowledgeBaseToolStatus`)
2. `PromptBuildingMiddleware`
3. `LlmInvocationMiddleware`
4. `CodeChangeApplicationMiddleware`
5. `CompletionDetectionMiddleware` — the "completion promise" marker
6. `InlineLearningsExtractionMiddleware`
7. `LoopbackEvaluationMiddleware`
8. `GitCheckpointMiddleware`

**This pipeline is the single most reusable asset for a Hermes-style agent loop.** Stages 4–8 are Ralph-specific; 1–3 are generic.

### 4.2 Agent factory (`Infrastructure/Agents`)

`RalphAgentFactory : IRalphAgentFactory` — builds an `IChatClient` from `AnthropicClient.AsIChatClient(model, maxTokens)`, wrapped in `ChatClientBuilder().UseFunctionInvocation()` for automatic tool calling. Provides:

- `InvokeAsync` — single call with MCP tools attached
- `InvokeSubagentAsync` — isolated context, own system prompt, model override, per-call timeout
- `RunParallelSubagentsAsync` — semaphore-throttled fan-out (default 10)

Token usage is extracted from `ChatResponse.Usage` and flows through to persistence — the cost dashboard depends on this.

`McpToolBuilder` converts `McpServerConfig` entries into `AITool` instances. Two in-process MCP tool sets already exist: `DaedalusLearningsTools`, `DaedalusFailurePatternsTools`.

### 4.3 Other agent abstractions

`IAgentExecutor` / `AgentExecutor` + `AgentMetadata` / `AgentExecutionContext` / `AgentExecutionChain` — a sequential multi-agent chain executor, separate from the Ralph loop. `IAgentSelector` picks agents. This is closer to what a Hermes-style framework needs than the Ralph loop is, but it is thin (one test file).

`PhaseOrchestrator` + `IPhaseOrchestrator` — dependency-graph phase execution (benchmarked in `DependencyResolutionBenchmarks`).

---

## 5. Domain Model

`Daedalus.Domain/Entities` — `Entity` / `AggregateRoot` base types, then:

| Aggregate | Notes |
|---|---|
| `Task` + `TaskExecution` | The Ralph unit of work. Execution rows carry tokens + model id. |
| `Project` | Owns tasks. |
| `ExecutionSession` | Worker session / heartbeat / claim tracking. |
| `BrainstormSession` + `BrainstormMessage` + `BrainstormPhase` | Conversational design flow (recent work). |
| `StructuredLearningEntry` | Cross-task knowledge, **with pgvector embedding column**. |
| `RepositoryConfiguration` | Git platform + credentials config. |
| `PromptSection` / `PromptSectionCategory` | Configurable prompt assembly. |

`Daedalus.Domain/CodeAnalysis` — a second, separate model (18 types): `CodeAnalysisRequest`, `AnalysisIteration`, `RepositoryInfo`, `GitDiff`, `PullRequestResult`, etc. Driven by `RalphLoopOrchestrator` in Infrastructure.

**10 `DbSet`s**, 17 migration files (8 migrations), latest `20260302134329_AddBrainstormSessions`. Vector search is live: `AddSemanticEmbeddings` + `AddEmbeddingHnswIndex`.

---

## 6. Knowledge / RAG — already partially built

The roadmap names **Rag.NET** as an integration target. **No Rag.NET or AI.Sentinel reference exists anywhere in the code** — they appear only in `docs/planning/*.md`. However a hand-rolled RAG slice is already working:

- `IEmbeddingService` → `OllamaEmbeddingService` (via `IEmbeddingGenerator<string, Embedding<float>>`), with `NoOpEmbeddingService` fallback
- `StructuredLearningEntry.Embedding` + pgvector HNSW index
- `LearningsRepository` semantic search
- MCP tools exposing learnings + failure patterns back to the agent
- Design docs: `docs/plans/2026-03-01-mcp-knowledge-base-design.md`, `…-plan.md`

**Implication for Milestone 1:** integrating Rag.NET is a *replacement* decision (swap this slice out) rather than a from-scratch build. That trade-off should be made explicitly during brainstorming.

Nothing analogous exists for **AI.Sentinel** — there is no security/approval/detection layer at the model boundary today. The middleware pipeline is the natural insertion point.

---

## 7. Web UI

10 Radzen pages: `Home`, `Projects`, `Tasks`, `Executions`, `Sessions`, `Costs`, `Brainstorm`, `RalphConfig`, `RepositoryConfiguration`, `Authentication`. Components include a 4-step `PrdGenerator` wizard. `ApiClient` + `ProjectApiClient` wrap the REST API; DTOs are hand-duplicated in `Web/Services/`.

Most of this UI is Ralph-shaped (`RalphConfig`, `Executions`, `Sessions`). `Costs` and `Brainstorm` are the most portable.

---

## 8. Testing

| Project | Framework | Tests | Status |
|---|---|---|---|
| `Tests.Unit` | xUnit | 68 | ✅ pass — includes **ArchUnit clean-architecture rules** |
| `Tests.Unit.Domain` | xUnit | 253 | ✅ pass |
| `Tests.Unit.Application` | xUnit | 291 | ✅ pass |
| `Tests.Unit.Infrastructure` | xUnit | 127 | ✅ pass |
| `Tests.Integration` | xUnit + Testcontainers | not run (needs Docker) | — |
| `Tests.Playwright.Api` | NUnit | not run | — |
| `Tests.Playwright.Browser` | NUnit + Playwright | not run | — |

**739 unit tests pass, 0 failures** (verified 2026-08-16, `--no-build` against existing binaries).

`CleanArchitectureTests` enforces: Domain depends on nothing; Application must not reference Infrastructure or Api; Infrastructure must not reference Api; controllers live in Api; repository *interfaces* in Application, *implementations* in Infrastructure. **Any new framework layering must satisfy these or the rules must be updated deliberately.**

---

## 9. Build Health — two active blockers ⚠

`dotnet build` currently **fails**. Neither failure is a code defect.

**(a) NuGet audit, 127 errors.** `TreatWarningsAsErrors=true` promotes `NU1902`/`NU1903` advisories to errors. Vulnerable transitive packages:

| Package | Severity | Advisories |
|---|---|---|
| `MessagePack` 2.5.192 | high + moderate | 11 advisories × 16 projects |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.0 | moderate | 3 |
| `OpenTelemetry.Api` 1.15.0 | moderate | 1 |
| `Microsoft.OpenApi` 2.0.0 | high | 1 |
| `SSH.NET` 2025.1.0 | high | 1 |

Fix by bumping/pinning the transitives, or scoping `NoWarn`. Renovate is configured (`.github/renovate.json`) but has not resolved these.

**(b) SourceLink git task crash (local only).** With auditing suppressed, the build then fails with:

```
MSB4018: Microsoft.Build.Tasks.Git.GetUntrackedFiles failed
System.ArgumentException: The path is empty. (Parameter 'path')
   at Microsoft.Build.Tasks.Git.GitIgnore.LoadFromFile(...)
```

**Cause:** the *global* git config `C:/Users/MarcelRoozekrans/.gitconfig` sets `core.excludesFile = .gitignore` — a relative path the MSBuild git task cannot resolve. Fix by removing that global setting or making it absolute. This is a workstation issue, so CI is unaffected — but it blocks local builds today.

CI (`.github/workflows/ci.yml`) builds Release, runs unit then integration tests with coverage → badge, then builds/pushes 3 Docker images to ghcr.io. CI is subject to blocker (a) as well.

---

## 10. Conventions to Follow

`.github/copilot-instructions.md` (1174 lines) is the authoritative standard. Highlights actually observed in code:

- **Railway-Oriented Programming** — `Result` / `Result<T>` everywhere; exceptions only for the truly unexpected. `CA1031` is suppressed at LLM boundaries deliberately.
- **Primary constructors** for DI (`sealed partial class Foo(IBar bar) : IFoo`).
- **Compile-time logging** — `[LoggerMessage]` partial methods with explicit `EventId` ranges per class (e.g. 400-423 in `RalphAgentFactory`).
- **ZLinq** (`.AsValueEnumerable()`) for zero-allocation LINQ in hot paths.
- `ConfigureAwait(false)` in library code, `CancellationToken` on every async method.
- EF Core: `AsNoTracking()` reads, `ExecuteUpdateAsync()` bulk ops, DbContext pooling, row-version concurrency tokens.
- Configuration binding is wrapped in `#pragma warning disable IL2026` (reflection vs. trimming) — a recurring, accepted pattern.
- `dotnet format` before commit; 0 warnings expected.

---

## 11. Recommended Starting Points for Milestone 1

| Concern | Where to look first |
|---|---|
| Agent loop core | `Application/Services/RalphLoopPipelineService.cs` + `Abstractions/IRalphLoopMiddleware.cs` — generalize `RalphIterationContext` into an agent-agnostic context |
| Tool use | `Infrastructure/Agents/McpToolBuilder.cs`, `Agents/Tools/*` |
| Model boundary (AI.Sentinel insertion) | `Application/Services/Middleware/LlmInvocationMiddleware.cs` — wrap or bracket this stage |
| Memory / RAG (Rag.NET decision) | `Infrastructure/Services/OllamaEmbeddingService.cs`, `Persistence/LearningsRepository.cs`, `Domain/Entities/StructuredLearningEntry.cs` |
| Sessions | `Domain/Entities/ExecutionSession.cs` + `Console/RalphLoopWorker.cs` |
| Multi-agent | `Application/Services/AgentExecutor.cs`, `PhaseOrchestrator.cs` — thin, likely to be rewritten |
| Layering guardrails | `tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs` |

### Open questions to settle during brainstorming

1. **Rag.NET vs. the existing pgvector/Ollama slice** — replace, wrap, or run both? Neither library is referenced yet, so their APIs and packaging (NuGet? source? private feed?) are unknown to this repo.
2. **AI.Sentinel integration shape** — middleware stage, `IChatClient` decorator, or out-of-process gateway? The `ChatClientBuilder` pipeline in `RalphAgentFactory` makes a decorator cheap.
3. **How much Ralph survives** — the middleware pipeline generalizes well; the completion-promise/loopback/git-checkpoint stages and the whole `CodeAnalysis` sub-domain are Ralph-specific. Retiring them touches 10 DbSets, 11 controllers, and 10 UI pages.
4. **Retire or keep the Console worker** as the agent host?
5. **Naming/versioning** — is this a new namespace root (`Daedalus.Agents.*`) or an in-place evolution? ArchUnit rules and 739 tests are anchored to today's layout.
6. **Fix build blockers first** — Milestone 1's DoD includes "all tests passing"; the audit failures should be cleared before, not during, framework work.

---

## Appendix: Pre-Pivot Design Docs (baseline)

| Doc | Status |
|---|---|
| `2026-03-01-costs-dashboard-design.md` / `-plan.md` | implemented (token columns, `Costs.razor`, `CostAnalyticsService`) — **untracked in git** |
| `2026-03-01-mcp-knowledge-base-design.md` / `2026-03-02-…-plan.md` | implemented (pgvector, Ollama, MCP tools) |
| `2026-03-02-brainstorm-sessions-design.md` / `-plan.md` | implemented (BrainstormSession aggregate, `Brainstorm.razor`) |
| `docs/architecture-diagrams.md` (1210 lines, 13+ Mermaid) | Ralph-era; will need a rewrite after the pivot |
| `docs/ralph-wiggum-technique.md` | Ralph-era methodology doc |
| `docs/regression-report-2026-03-01-1800.md` + screenshots | **untracked in git** |
