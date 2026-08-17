# Thalos.NET — Plan B (phase 1.2): Daedalus consumes `Thalos.NET.Memory` + `Thalos.NET.Memory.RagNet`

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make Daedalus the first consumer of Thalos.NET 0.2.0 memory: persist curated memories in Postgres (`AgentMemories` + `PostgresMemoryStore`), index them through the Rag.NET adapter on the app database (`rag_chunks`, nomic-embed-text 768 dims via Ollama), move the Ralph learnings write/read paths onto the memory service through an Application port, migrate `StructuredLearnings` rows in one EF migration and delete the hand-rolled learnings/embedding slice (incl. `Pgvector.EntityFrameworkCore` and every `UseVector()`), add `GET/DELETE /api/agent-memories`, a Memories panel on the Agent page, and finish by consuming Thalos.NET 0.2.0 from nuget.org.

**Architecture:** `Daedalus.Agents` (the only project allowed to reference Thalos/Rag.NET) gains a `Memory/` folder: `PostgresMemoryStore : IMemoryStore` over `ApplicationDbContext` (same shape as `PostgresAgentSessionStore`), `ThalosLearningsMemory : ILearningsMemory` (adapter over `IMemoryService` for Ralph, shared owner `daedalus`), `ReindexPendingMemoriesHostedService`, and memory wiring inside `AddDaedalusAgents` (+ a memory-only `AddDaedalusMemory` for the Ralph console host). `Daedalus.Domain` gets the Thalos-free `AgentMemory` aggregate; `Daedalus.Application` gets the port `ILearningsMemory` + `ParsedLearning` (Ralph code and Infrastructure MCP tools stay Thalos-free per ArchUnit); `Daedalus.Api` adds `AgentMemoriesController`; `Daedalus.Web` adds `AgentApiClient` memory calls and the panel. Store is the source of truth, the Rag.NET index is a rebuildable cache.

**Tech Stack:** as phase 1.1 (.NET 10, EF Core 10 + Npgsql 10, Blazor WASM + Radzen, xUnit/NUnit/Playwright, Testcontainers `pgvector/pgvector:pg16`) + `Thalos.NET.*` 0.2.0 (local pack `0.2.0-local.<stamp>` during development), `Thalos.NET.Memory`, `Thalos.NET.Memory.RagNet` (→ `Rag.NET.Abstractions`, `Rag.NET.VectorStores.PgVector` 0.1.x transitively), OllamaSharp 5.4.12 (already referenced by Api).

**Prerequisite:** Plan A complete in `C:\Projects\Prive\Thalos.NET`; `pwsh scripts/pack-local.ps1` produced **eight** packages in `C:\Projects\Prive\.nuget-local` at version `0.2.0-local.<stamp>` (note the exact stamp: `Get-ChildItem C:\Projects\Prive\.nuget-local\Thalos.NET.Memory.0.2.0-local.*.nupkg`).

**Design doc:** `docs/plans/2026-08-17-thalos-memory-design.md` (§6–§9 are this plan's scope) · **Tracking:** #228 · **Phase-1.1 plan (conventions):** `docs/plans/2026-08-16-thalos-net-plan-b.md`

---

## 0. Read this first

### 0.1 Facts and conventions (verified in the Daedalus repo, HEAD `a58c8c4`, 2026-08-17)

**Layout / namespaces**
- `src/Daedalus.Agents` (`namespace Daedalus.Agents`, `.Sessions`, `.Tools`, `.Security`, `.Api`) references Domain, Application, Infrastructure and packages `Thalos.NET`, `.Mcp`, `.Anthropic`, `.Sentinel`, `ZeroAlloc.Mapping`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.AI`, `Configuration.Binder`, `Hosting.Abstractions`, `Options`. `IsTrimmable=false`. Thalos namespaces are `Thalos`, `Thalos.Anthropic`, `Thalos.Mcp`, `Thalos.Sentinel`, `Thalos.Tools`, `Thalos.Testing`. Memory packages are assumed to follow suit: **`Thalos.Memory`** and **`Thalos.Memory.RagNet`** (verify in Task 1).
- Composition root: `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs` — `AddDaedalusAgents(services, configuration, environment, IEmbeddingGenerator<string,Embedding<float>>? embeddingGenerator = null)`; binds `DaedalusAgentsOptions` from section `Thalos` (`McpConfigPath`, `Agents[]`, `ToolPolicies[]`, `Sentinel`), registers `DaedalusLearningsTools`/`DaedalusFailurePatternsTools` scoped, `AgentSessionCrashRecovery` hosted service, then `services.AddThalos(t => t.UseAnthropic(configuration).UseSessionStore<PostgresAgentSessionStore>().AddLocalTools("daedalus", typeof(DaedalusKnowledgeTools)).AddMcpServersFromFile(...).AddPolicy<DeveloperPolicy>(); …RequireToolPolicy…; AddAgent(ToDefinition(a)); UseAISentinel(...)`). Options classes `AgentConfig`, `ToolPolicyConfig`, `SentinelConfig` live in `DaedalusAgentsOptions.cs` (same namespace, separate top-level classes; `IList<>` collections initialised inline because the binder appends).
- `PostgresAgentSessionStore(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock)`: fresh DbContext per call, ZeroAlloc `Result<T, AgentError>` / `UnitResult<AgentError>`, `AgentError.SessionNotFound/StoreError/Validation`, `ExecuteUpdateAsync` for atomic updates, `DateTime` UTC in the entity, `DateTimeOffset(…, TimeSpan.Zero)` outward. Copy this shape for the memory store.
- `AgentSessionCrashRecovery` is the hosted-service pattern (`internal sealed partial class … : IHostedService`, `[LoggerMessage]` EventIds 400–402, never fails host start).
- `AgentDtoMapper` (`Daedalus.Agents.Api`) maps Thalos records → `Daedalus.Application.DTOs.Agents` records; `ToDto(AgentEvent)` currently **throws** for unknown event types (`_ => throw new ArgumentOutOfRangeException`) — new memory events would kill the SSE stream, so the mapper is updated in Task 5 before memory is enabled.
- API: `AgentSessionsController` (`[ApiController][ApiVersion("1.0")][Route("api/agents")][Authorize(Policy = "AgentUse")][Produces("application/json")]`), caller via `HttpSecurityContextFactory.TryCreate(User, out caller)` (401 otherwise), errors via `AgentErrorResults.ToActionResult(this)` (`src/Daedalus.Api/Agents/AgentErrorResults.cs`, `code` extension = `AgentErrorCode` name), SSE frames `event: {Kind}\ndata: {json}\n\n` serialised with `ApiJsonSerializerContext.Default.AgentEventDto`. `ApiJsonSerializerContext` lists every response DTO with `[JsonSerializable]`. Existing `PagedResultDto<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)` in `Daedalus.Application.DTOs`.
- Web: `src/Daedalus.Web/Services/AgentApiClient.cs` (CSFE `Result`, `GetAsync<T>` helper, ProblemDetails `detail` as error text, `StreamTurnAsync` via `SseReader`), `Pages/Agent.razor` (+ `Agent.razor.cs` view models `ChatMessage`, `ToolCallView`, `ErrorView`), `Apply(ChatMessage, AgentEventDto)` switches on `evt.Kind`, `data-testid`s drive Playwright. `wwwroot/js/agent-page.js` only handles Escape.
- Ralph learnings slice (to be replaced/deleted): `Application/Abstractions/{IEmbeddingService,ILearningsRepository,ILearningsService,IKnowledgeBaseToolStatus}.cs`, `Application/Services/LearningsService.cs` (parser `ParseRawLearnings`, `ClassifyLine`, `DetermineSeverity`, `ExtractTags` are `internal static` and unit-tested in `tests/Daedalus.Tests.Unit.Application/Services/LearningsServiceParsingTests.cs`; `ParseAndPersistLearningsAsync` is called from `RalphLoopPipelineService` ~line 208 with `(learnings, task.Id, projectId, ct)`; `GetEnrichmentContextAsync` is called by `LearningsEnrichmentMiddleware` on iteration 1 and every 3rd), `Domain/Entities/StructuredLearningEntry.cs` (+ `LearningCategory`, `LearningSeverity` enums — **keep the enums**), `Infrastructure/Persistence/{LearningsRepository.cs, Configurations/StructuredLearningEntryConfiguration.cs}` (`vector(384)` column, `Pgvector.Vector` conversion), `Infrastructure/Services/{OllamaEmbeddingService.cs, NoOp/NoOpEmbeddingService.cs, KnowledgeBaseToolStatus.cs}` (`LearningsCount` counts `StructuredLearnings` synchronously), `Infrastructure/Agents/Tools/DaedalusLearningsTools.cs` (`[McpServerToolType]`, `SearchLearnings(query, projectId?, maxResults)`), `Infrastructure/Extensions/InfrastructureServiceExtensions.cs` (`AddExternalServices` registers `ILearningsRepository`, `IKnowledgeBaseToolStatus`; `AddAgentFrameworkServices` registers `IEmbeddingService` Ollama-or-NoOp), `Agents/Tools/DaedalusKnowledgeTools.cs` (`daedalus__search_learnings`, `daedalus__search_failure_patterns`).
- **The Ralph Loop worker runs in `src/Daedalus.Console`** (`AddExternalServices` + `AddAgentFrameworkServices` + `AddRalphLoopMiddleware`, `RalphLoopWorker`), which does **not** reference `Daedalus.Agents` and has no Ollama. The learnings write path therefore needs a memory registration in the console host too (Task 10: `AddDaedalusMemory` + `WithReference(ollama)` in the AppHost).
- Ollama: `src/Daedalus.Api/Program.cs` lines 55–68 build `new OllamaSharp.OllamaApiClient(new Uri(cs), "nomic-embed-text")` when `ConnectionStrings:ollama` is set, register it as `IEmbeddingGenerator<string, Embedding<float>>` singleton and pass it into `AddDaedalusAgents` (Sentinel). nomic-embed-text = **768** dims. AppHost: `builder.AddOllama("ollama").WithDataVolume().AddModel("nomic-embed-text")`; only `api` has `.WithReference(ollama)`.
- Database: `ServiceDefaults/AspireExtensions.cs` `AddApplicationDatabase` registers `AddDbContextPool` + `AddPooledDbContextFactory`, both `UseNpgsql(cs, o => o.UseVector())`; `Infrastructure/Persistence/ApplicationDbContextFactory.cs` (design-time) also `UseVector()`. Postgres image everywhere is `pgvector/pgvector:pg16` — **keep it** (Rag.NET's `rag_chunks` needs the `vector` extension). Migrations live in `src/Daedalus.Infrastructure/Migrations/`; ids are timestamped, latest `20260816174546_AddAgentSessions`; `StructuredLearnings` was created in `20260223170600_AddRepositoryConfigurations` (columns `Id uuid, Category int, Pattern varchar(2000), Resolution varchar(2000), SourceTaskId uuid?, ProjectId uuid?, Severity int, HitCount int, CreatedAt timestamptz, LastReferencedAt timestamptz?, Tags text[]`), `20260302080051_AddSemanticEmbeddings` added `Embedding vector(384)` (`AddColumn<Vector>` + `CREATE EXTENSION IF NOT EXISTS vector`), `20260302120000_AddEmbeddingHnswIndex` the HNSW index. Four `*.Designer.cs` files + the snapshot carry `using Pgvector;` / `b.Property<Vector>("Embedding")` — these must be edited when the package goes (Task 11).
- Migration command (verified: 1.1 used the Api as startup project; README documents the same): `dotnet ef migrations add <Name> --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations`. Applying: `dotnet run --project src/Daedalus.Migrations` (Aspire runs it before api/console). EF 10 throws on `Migrate()` when the model has pending changes not in the snapshot — always regenerate the snapshot with `migrations add`.
- Test fixtures: `tests/Daedalus.Tests.Integration/Fixtures/PostgresFixture.cs` (Testcontainers pgvector, **`EnsureCreatedAsync()`** — not migrations — after `CREATE EXTENSION IF NOT EXISTS vector`, Respawn reset, `CreateDbContextOptions()`/`CreateDbContext()` helpers with `UseVector()`), `AspirePostgresFixture.cs` (same), `InMemoryDbContextFactory.cs` (ignores `StructuredLearningEntry.Embedding`), `[Collection(DatabaseCollection.Name)]`, `ApiWebApplicationFactory(connectionString, IAgentRuntime)` boots the real `Program` with `HeaderTestAuthHandler` (`X-Test-User` style header, see `AgentEndpointsSmokeTests.Send(...)`), controller tests instantiate controllers directly (`AgentSessionsControllerIntegrationTests.Controller("alice", role?)`, `AssertProblem(result, status, code)`, `RequestServices` with `AddMvcCore().AddApiExplorer()`). Unit tests: `tests/Daedalus.Tests.Unit.Application/Agents/*` (registration, mapper, knowledge tools), `.../Services/LearningsServiceParsingTests.cs`, `.../Services/LearningsEnrichmentMiddlewareTests.cs`, `tests/Daedalus.Tests.Unit.Domain/Entities/StructuredLearningEntryTests.cs`, `tests/Daedalus.Tests.Unit.Infrastructure/Persistence/BrainstormRepositoryTests.cs` (~line 278 `Ignore(e => e.Embedding)` workaround), ArchUnit `tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs` (Domain/Application/Infrastructure/Web must not depend on `^Thalos(\.|$)`; Agents must not depend on Api). Playwright: `tests/Daedalus.Tests.Playwright.Browser/Fixtures/{E2EServerFixture.cs, StubAgentRuntime.cs, TestAuthHandler.cs}` (test user `e2e-test-user-id` with roles admin/task-manager/project-manager/analyst; fixture calls `AddDaedalusAgents` with `Daedalus.Api.appsettings.json` linked in, replaces `IAgentRuntime` with `StubAgentRuntime(IAgentSessionStore, IAgentCatalog)`; DB via `EnsureCreatedAsync` + `SeedTestDataAsync(dbContext)`), `PageObjects/AgentPage.cs`, `Scenarios/AgentPageBrowserTests.cs` (NUnit, `[Category("Agent")]`). `tests/Daedalus.Tests.Playwright.Api/Fixtures/E2EServerFixture.cs` also has two `UseVector()` sites.
- CI (`.github/workflows/ci.yml`): build `dotnet build Daedalus.sln --configuration Release`; unit `--filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"`; integration `--filter "FullyQualifiedName~Integration&FullyQualifiedName!~Playwright&Category!=AuthenticationFlow"` (Docker available). CI cannot see `C:\Projects\Prive\.nuget-local` → the local packs are committed under `packages-local/` while developing (as in 1.1) and removed at the end.
- Conventions: primary constructors, `[LoggerMessage]` partial methods with per-class EventId ranges, `ConfigureAwait(false)` in library code, `TreatWarningsAsErrors` (0 warnings), `dotnet format` before the final review, CSFE `Result` in Ralph code / ZeroAlloc `Result` at the Thalos boundary, conventional commits enforced by commitlint (**header ≤ 100 chars**), release-please cuts releases from `feat:`/`fix:`, commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. Planning state: `docs/planning/{ROADMAP,MILESTONE,STATE}.md` (1.2 is "active").

### 0.2 Assumed `Thalos.NET.Memory` 0.2.0 API (from the design §3–§5; **reconcile in Task 1**)

The design fixes the interfaces; the record *member* names below are this plan's assumptions where the design left them implicit. Task 1 step 4 dumps the real surface from the package XML docs; where a name differs, search-replace it throughout the code in this plan (every use is listed here). Contract tests are the truth for store semantics.

| Item | Assumed shape (namespace `Thalos.Memory` unless noted) | Used in |
|---|---|---|
| `MemoryId` | Guid-backed typed id like `SessionId`: `new MemoryId(Guid)`, `.Value`, `New()`, `ToString()` (26-char ULID), `TryParse(string, IFormatProvider?, out MemoryId)` | store, controller, mapper, stub |
| `MemoryKind` | `sealed record MemoryKind(string Value)` with statics `Fact/Preference/Decision/Learning/Note`; construct arbitrary via `new MemoryKind("…")` | store, adapter, controller |
| `MemoryRecord` | as design §3 (`Id, OwnerId, AgentId?, Kind, Text, Tags, Source, Importance, CreatedAt, UpdatedAt, LastRecalledAt?, RecallCount, IsArchived, IndexPending`), `required` init props | store, mapper |
| `MemoryScope` | `readonly record struct MemoryScope(string OwnerId, AgentId? AgentId, string? SharedOwnerId)` | adapter, controller |
| `MemoryUpdate` | `{ string? Text; IReadOnlyList<string>? Tags; double? Importance; bool? IsArchived; bool? IndexPending }` (init props, all optional) | store |
| `MemoryQuery` | `{ IReadOnlyList<string> OwnerIds (required); AgentId? AgentId; IReadOnlyList<MemoryKind>? Kinds; IReadOnlyList<string>? Tags; bool IncludeArchived; bool? IndexPending; int Page = 1; int PageSize = 20 }` | store, controller, adapter, stub |
| `MemoryPage` | `(IReadOnlyList<MemoryRecord> Items, int Total, int Page, int PageSize)` | store, controller |
| `RememberRequest` | `{ required string OwnerId; AgentId? AgentId; required MemoryKind Kind; required string Text; IReadOnlyList<string> Tags = []; string Source = ""; double Importance = 0.5 }` | adapter |
| `RecalledMemory` | `(MemoryRecord Record, double Score)` | adapter |
| `RecallOptions` | `{ int TopK = 5; double MinScore = 0.6; int MaxChars = 2000 }` | adapter, options |
| `ReindexOptions` / `ReindexReport` | `{ bool PendingOnly = true; int BatchSize = 64 }` / `(int Scanned, int Indexed, int Failed)` | reindex service |
| `MemoryIndexHealth` | `(bool Available, int? Dimensions, string? Reason)` | reindex service, E2E stub |
| `IMemoryStore`, `IMemoryIndex`, `IMemoryService` | exactly design §3 | everywhere |
| `MemoryOptions` | `{ Enabled = true; string? SharedOwnerId; RecallOptions Recall; DedupeOptions Dedupe { Enabled, Threshold }; ExposeTools = true }` | wiring |
| `AgentMemorySettings` | `{ bool? Enabled; int? TopK; bool? ExposeTools }` on `AgentDefinition.Memory` | wiring |
| `ThalosBuilder.UseMemory(Action<MemoryOptions>?)` | registers `IMemoryService`, in-memory store/index defaults, context provider, `memory` tool source | wiring |
| **`ThalosBuilder.UseMemoryStore<TStore>()`** | replaces the store (singleton, like `UseSessionStore<T>`) — **verify name**; fallback: `UseMemory(o => …)` overload taking a store type, or `services.AddSingleton<IMemoryStore, PostgresMemoryStore>()` *after* `AddThalos` (last registration wins only if Thalos used `TryAdd`) | wiring |
| `Thalos.Memory.RagNet.RagNetThalosBuilderExtensions.UseRagNetMemory(Action<RagNetMemoryOptions>)` | `{ string ConnectionString; int VectorDimensions; bool EnsureSchemaOnStartup = true }`; resolves the host's `IEmbeddingGenerator<string, Embedding<float>>` from DI (optional — absent generator ⇒ probe unavailable) | wiring |
| Events (`AgentEvent` subclasses, so they reach `RunTurnStreamingAsync`) | `MemoryRecalledEvent(SessionId, TurnId, int Count, IReadOnlyList<MemoryId> Ids, int Chars)` kind `memory-recalled`; `MemoryStoredEvent(SessionId, TurnId, MemoryId Id, MemoryKind Kind, bool Deduped)` `memory-stored`; `MemoryRecallFailedEvent(SessionId, TurnId, AgentErrorCode Code)` `memory-recall-failed`; `MemoryIndexPendingEvent(SessionId, TurnId, MemoryId Id)` `memory-index-pending`; constants `MemoryEventKinds.*` (or on `AgentEventKinds`) | mapper, Web, stub |
| `AgentErrorCode` additions | `MemoryNotFound, MemoryStoreFailed, MemoryIndexUnavailable, MemoryIndexFailed, MemoryValidationFailed, MemoryForbidden`; factories `AgentError.MemoryNotFound(MemoryId)`, `AgentError.MemoryStoreFailed(string message, string? detail = null)`, `AgentError.MemoryValidationFailed(string)`, `AgentError.MemoryForbidden(string)` | store, controller mapping |
| `Thalos.Testing.MemoryStoreContractTests` | `protected abstract ValueTask<IMemoryStore> CreateStoreAsync(TimeProvider clock)` (mirrors `SessionStoreContractTests`) | store tests |

> If the memory events ship only as hub notifications (`IAgentNotificationPublisher`) and not as `AgentEvent`s, Task 5/17/18 need a different transport (a per-turn `RecordingNotificationPublisher` bridged into the SSE writer). Stop and report before implementing Task 5 in that case.

### 0.2b Reconciled against Plan A (2026-08-17) — apply these over §0.2 before Task 1

Plan A (`docs/plans/2026-08-17-thalos-memory-plan-a.md`, §0.5/§0.6 and Tasks 2–3, 18, 23) fixes the real surface. Differences from the assumptions above — use the Plan A shape everywhere in this plan:

| Item | Plan A (authoritative) | Impact here |
|---|---|---|
| Namespaces | `MemoryId`, `AgentMemorySettings`, `AgentErrorCode.Memory*`, `AgentError.Memory*` factories and all `Memory*Event`s live in **`Thalos`** (Abstractions); model/ports/service in `Thalos.Memory`; contract tests + `HashedBagOfWordsEmbeddingGenerator` in `Thalos.Testing`; adapter in `Thalos.Memory.RagNet` | add `using Thalos;` where §0.2 assumed `Thalos.Memory` |
| `MemoryPage` | `MemoryPage(IReadOnlyList<MemoryRecord> Items, int Page, int PageSize, int TotalCount)` | Task 15 controller and Task 17 panel use `.TotalCount`, not `.Total` |
| `MemoryQuery` | `OwnerIds` is `IReadOnlyList<string>?` (null = all owners at store level; `IMemoryService.ListAsync` requires ≥ 1); `MaxPageSize = 100`; `IndexPending` filter exists | as assumed |
| `RememberRequest` | `Kind` defaults to `MemoryKind.Note` (not required) | fine |
| Events | `MemoryRecalledEvent(SessionId, TurnId, IReadOnlyList<MemoryId> MemoryIds, int Chars)` (no `Count` — use `MemoryIds.Count`); `MemoryStoredEvent(SessionId, TurnId, MemoryId MemoryId, string MemoryKind, bool Deduped)`; `MemoryRecallFailedEvent(SessionId, TurnId, AgentErrorCode Code)`; `MemoryIndexPendingEvent(SessionId, TurnId, MemoryId MemoryId)`; **plus `MemoryQuarantinedEvent(SessionId, TurnId, MemoryId MemoryId, string? Detail)`** kind `memory-quarantined`; kind constants on `AgentEventKinds.Memory*` | Task 5 maps **five** kinds; the memory event DTO carries `MemoryIds`, `MemoryId`, `MemoryKind`, `Deduped`, `Code`, `Chars`, `Detail`; Task 17 shows "memory quarantined" as a status line |
| `AgentError` factories | `MemoryNotFound(MemoryId)`, `MemoryStoreFailed(string message, string? detail = null)`, `MemoryIndexUnavailable(string, string? = null)`, `MemoryIndexFailed(string, string? = null)`, `MemoryValidationFailed(string)`, **`MemoryForbidden(MemoryId id)`** (not a message) | Task 15: `AgentError.MemoryForbidden(memoryId)`; the ProblemDetails detail is the factory's message |
| `AgentMemorySettings` | `{ bool? Enabled; int? TopK }` — **no `ExposeTools`**; which agents see `memory__*` is governed by `AgentDefinition.Tools` globs | Task 6: `AgentConfig.Memory` maps `Enabled`/`TopK` only; put `memory__*` in the agent's `Tools` list in `appsettings.json` |
| `IMemoryIndex` | `UpsertAsync(IReadOnlyList<MemoryRecord>, ct)`, `SearchAsync(string query, MemoryScope, MemorySearchOptions(TopK, MinScore), ct) → IReadOnlyList<MemoryHit(MemoryId Id, double Score)>`, `RemoveAsync(MemoryId, ct)`, `ProbeAsync(ct) → MemoryIndexHealth(bool Available, int? Dimensions, string? Detail = null)` — no `RebuildAsync` | Task 18 `StubMemoryIndex` implements exactly these four; `MemoryIndexHealth` third member is `Detail` |
| Builder | `UseMemory(Action<MemoryOptions>? = null)`, `UseMemoryStore<T>()` (Replace + telemetry proxy, like `UseSessionStore<T>`), `UseMemoryIndex<T>()`; default index = `InMemoryMemoryIndex` when a generator is registered else `UnavailableMemoryIndex` | Task 6 uses `UseMemoryStore<PostgresMemoryStore>()` — confirmed to exist |
| `UseRagNetMemory` | `UseRagNetMemory(Action<RagNetMemoryOptions>)` and shorthand `UseRagNetMemory(string connectionString, int vectorDimensions)`; options `{ ConnectionString; VectorDimensions; EnsureSchemaOnStartup = true }`; **tolerates a missing generator** (Plan A §0.6 item 13: unavailable index + schema still created) | Task 6 wires it unconditionally; integration/E2E hosts without Ollama get the unavailable index (E2E swaps in `StubMemoryIndex` anyway); set `EnsureSchemaOnStartup=false` in test hosts that have no `vector` extension |
| Contract tests | `MemoryStoreContractTests.CreateStoreAsync(TimeProvider clock)`; `MemoryIndexContractTests.CreateIndexAsync(IEmbeddingGenerator<…>)` | Task 4 as assumed |
| Memory events transport | `AgentEvent` subclasses published through `TurnScope` inside a turn (stream + hub), through the hub outside a turn | the SSE path in Task 5 works as planned; no bridge needed |
| Ids | `MemoryId` is a `[TypedId]` Guid-backed struct in `Thalos` (`Ids.cs`) | §0.3 uuid column confirmed |

### 0.3 Deviations from the design (deliberate, documented)
- `AgentMemories.Id` is **`uuid`**, not `text`: `MemoryId` is Guid-backed (like `SessionId`), the migration copies `StructuredLearnings.Id` 1:1, and the adapter converts (`new MemoryId(guid)`), exactly as sessions do. If Task 1 finds `MemoryId` is *not* Guid-backed, switch the column to `text` and generate ids in the migration with `gen_random_uuid()::text` → `MemoryId` parse; the rest of the plan is unchanged.
- Two extra indexes on top of the three in the design: `IX_AgentMemory_IndexPending` (reindex scan) and `IX_AgentMemory_CreatedAt_Id` (row-value keyset paging in `StreamAsync`, added in the G2 review — see §0.7).
- One extra endpoint `GET /api/agent-memories/{id}` so the panel can hydrate "recalled this turn" (the event carries ids only).
- `IKnowledgeBaseToolStatus.LearningsCount` is dropped (design: "count via ListAsync or drop"); `FailurePatternsCount` stays.
- **Behaviour change — Ralph enrichment loses the self-task and project filters (Task 7).** The old `GetEnrichmentContextAsync` fetched learnings by `projectId` (or the global top-N), then excluded rows whose `SourceTaskId == currentTaskId` and bumped each row's `HitCount`. On the memory port there is neither filter: learnings live under one **shared owner** (`daedalus`) with no project dimension, and recall ranks purely by cosine similarity to the task prompt. Consequences, accepted deliberately: (1) a task can now recall a learning it produced itself in an earlier iteration — harmless and often useful, since the text is only injected when it is semantically close to the current prompt; (2) learnings are **cross-project** — the shared owner is global, so a Blazor learning can surface in an API task if the prompts are similar; per-project scoping would need a second owner or a tag facet and is out of scope for 1.2; (3) recall bookkeeping moved from `HitCount` to Thalos' `RecallCount`/`LastRecalledAt`, maintained by `MarkRecalledAsync` inside `IMemoryService.RecallAsync`. `ILearningsService.GetEnrichmentContextAsync` keeps its `projectId`/`currentTaskId` parameters (the interface and its callers are unchanged) but ignores them.
- **The API host is the only host that creates the Rag.NET schema.** `AddDaedalusAgents` passes `EnsureSchemaOnStartup = true`, `AddDaedalusMemory` (console) passes `false` — see §0.7, Tasks 6–10 review.

### 0.4 Commands
```powershell
dotnet build --nologo                                                                     # 0 warnings
dotnet test --nologo --filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"   # unit (CI filter)
dotnet test tests/Daedalus.Tests.Integration --nologo --filter "Category!=AuthenticationFlow"       # integration (Docker)
dotnet test tests/Daedalus.Tests.Playwright.Browser --nologo --filter "FullyQualifiedName~AgentPage" # browser (Playwright installed)
dotnet ef migrations add AddAgentMemories --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations
dotnet format
```

### 0.5 Branching
`git switch -c feature/thalos-memory` off `main`. Small commits per task; `pre-push-review` before merge/PR.

### 0.6 Task map (execution order; group labels G1–G8 as in the design brief)

| # | Group | Task |
|---|---|---|
| 1 | G1 | Branch, local pack pin (`packages-local/`, `nuget.config`, CPM), csproj refs, API reconciliation |
| 2 | G2 | Domain `AgentMemory` aggregate + tests |
| 3 | G2 | EF configuration + `DbSet` (no migration yet) |
| 4 | G2 | `PostgresMemoryStore` + `MemoryStoreContractTests` on Postgres |
| 5 | G1 | DTO + `AgentDtoMapper` for memory events, JSON context |
| 6 | G1 | Options + memory wiring in `AddDaedalusAgents`, `AddDaedalusMemory`, appsettings, registration tests |
| 7 | G3 | Application port `ILearningsMemory`, `ParsedLearning`, `LearningMemoryMapping`; `LearningsService` on the port + tests |
| 8 | G3 | `ThalosLearningsMemory` adapter + tests |
| 9 | G3 | MCP `search_learnings` → port; `KnowledgeBaseToolStatus`; middleware; `DaedalusKnowledgeTools` drops `search_learnings`; appsettings tools/instructions; tests |
| 10 | G3 | Console host memory registration + AppHost Ollama reference |
| 11 | G5 | Delete the slice (interfaces, impls, entity, config, DbSet, tests, Pgvector + `UseVector()`, fixture workarounds, historical migration `Vector` refs) |
| 12 | G2 | Migration `AddAgentMemories` (create + copy + drop) + migration test |
| 13 | G4 | `ReindexPendingMemoriesHostedService` + tests |
| 14 | G6 | `MemoryDto`, `AgentDtoMapper.ToDto(MemoryRecord)`, `AgentErrorResults` memory codes |
| 15 | G6 | `AgentMemoriesController` (list/get/forget) + JSON context + tests |
| 16 | G7 | `AgentApiClient` memory methods |
| 17 | G7 | Memories panel on `Agent.razor` |
| 18 | G7 | Playwright: stub memory events + index stub + seed + page object + scenario |
| 19 | G8 | ArchUnit rule, README, diagrams |
| 20 | G8 | Regression run, pre-push review, merge |
| 21 | G8 | nuget.org 0.2.0 switch, ROADMAP/MILESTONE/STATE, `complete` phase 1.2 |

### 0.7 Amendments (append here during execution, like Plan A)

- **Task 1 (2026-08-17) — nuget.org instead of local packs:** Thalos.NET **0.2.0 was already published on nuget.org** (all eight packages) when execution started, so Task 1 skipped `packages-local/`, the `thalos-local` source and the `0.2.0-local.<stamp>` pins entirely: `Directory.Packages.props` bumps the six existing `Thalos.NET*` pins `0.1.1 → 0.2.0` and adds `Thalos.NET.Memory` / `Thalos.NET.Memory.RagNet` at `0.2.0`; `nuget.config` stays nuget.org-only; Task 21's "nuget.org switch" is therefore already done (only the ROADMAP/MILESTONE/STATE part remains). **Transitive pin:** `Rag.NET.VectorStores.PgVector 0.1.0` (via `Thalos.NET.Memory.RagNet`) requires `Npgsql >= 10.0.3` → NU1109 with the central `10.0.1`; bumped `Npgsql` to `10.0.3` (no `Rag.NET.*` pin needed). **API reconciliation** (XML docs of the restored packages, `%USERPROFILE%\.nuget\packages\thalos.net.*\0.2.0\lib\net10.0\*.xml`, cross-checked with the Thalos.NET sources at `C:\Projects\Prive\Thalos.NET` HEAD `4f06277`): §0.2b is correct. Confirmed: `MemoryId` is Guid-backed (`Thalos.MemoryId(Guid)`, `.Value`, `New()`, `Parse/TryParse`), all `Memory*Event`s live in `Thalos` and derive from `AgentEvent` (`MemoryRecalledEvent` **does** expose a `Count` convenience property besides `MemoryIds`), the store replacement is `Thalos.Memory.MemoryThalosBuilderExtensions.UseMemoryStore<T>()`, `MemoryPage(Items, Page, PageSize, TotalCount)` (Task 4 code in this plan passes them in the wrong order — fixed in the implementation), `MemoryQuery.PageSize` defaults to **50** (not 20), `MemoryQuery.Tags` is **AND** ("every listed tag must be present" — the plan's Task 4 sketch used overlap/ANY; implemented as `@>` semantics), `MemoryQuery.AgentId` is an **exact** pinned-agent filter (the plan's `m.AgentId == null ||` half is dropped — the suite decides, as the plan allowed), `MemoryUpdate.TouchesContent` bumps `UpdatedAt` only for Text/Tags/Importance/IsArchived (the domain `AgentMemory.Update` follows suit — see Task 2 note), texts round-trip **untrimmed** (contract `Boundary_lengths_roundtrip` stores a text ending in `\n` — the domain must not `Trim()` text; see Task 2 note). `Thalos.Testing.MemoryStoreContractTests` = 22 facts, `NewClock()` fake clock at 2026-08-17 12:00 UTC, 1 ms tolerance. **Guard test:** `tests/Daedalus.Tests.Unit/Controllers/AgentErrorResultsTests.Every_AgentErrorCode_value_has_an_explicit_mapping_test` pins the `AgentErrorCode` count (12) and failed on the pin bump (0.2.0 has 18); the Task 14 `AgentErrorResults.ToStatusCode` arms for the six `Memory*` codes were pulled forward into Task 1 (mapping exactly as Task 14 specifies, theory rows added, count → 18) so the unit suite stays green per task; Task 14 keeps only `MemoryDto` + mapper.
- **Tasks 2–4 (2026-08-17):** **Task 2:** `AgentMemory` differs from the plan sketch in three contract-driven ways: (1) text is stored **verbatim** (no `Trim()`; validation checks whitespace-only and `Length ≤ 4000` on the raw string) because `Boundary_lengths_roundtrip` stores a text ending in `\n`; (2) `Update()` bumps `UpdatedAt` **only when text/tags/importance/isArchived is supplied** (`IndexPending` alone is bookkeeping — `Update_of_IndexPending_alone_does_not_bump_UpdatedAt`); (3) `Create()` also validates `Source ≤ 256` (`MaxSourceLength`) and rejects `NaN` importance, and — for Task 4's "insert as given" — takes optional trailing `updatedAt` (must not precede `utcNow`), `isArchived`, `recallCount` (≥ 0), `lastRecalledAt` (defaults reproduce the plan's behaviour; the plan's 10-argument calls compile unchanged). Analyzer-driven: kind lower-casing lives in `NormaliseKind` under a CA1308 pragma, `list.Exists` instead of `Any` (MA0020), no nested ternary (S3358). 12 domain facts. **Task 3:** as written (+ `Source` max length uses `AgentMemory.MaxSourceLength`). **Task 4:** `PostgresMemoryStore` — `MemoryPage(items, page, size, total)` argument order; `ListAsync` computes the skip in `long` and returns an empty page without querying when `skip ≥ total` (`Page = int.MaxValue` fact); tags filter is **AND** via one `EF.Property<List<string>>(m, "_tags").Contains(tag)` per normalised query tag (`@tag = ANY("Tags")`; a blank query tag → no match, like `MemoryQuery.Matches`); agent filter is exact (`AgentId == @agent`); `MarkRecalledAsync` de-duplicates ids client-side and uses one `ExecuteUpdateAsync`; **`StreamAsync` uses keyset paging** (batches of 256, fresh DbContext per batch, ordered `(CreatedAt, Id)`; the next batch is `CreatedAt > last OR (CreatedAt == last AND Id NOT IN <ids already yielded at last>)` — no uuid comparison in SQL, so ties are handled by exclusion) rather than one long-lived streaming query, so no connection is held while reindex embeds/updates yielded rows; S4456 forced the split into a validating `StreamAsync` + private iterator `StreamCoreAsync`. Test seam: `internal PostgresMemoryStore(factory, clock, int streamBatchSize)` + `InternalsVisibleTo("Daedalus.Tests.Integration")` in `Daedalus.Agents.csproj`. The published `Thalos.NET.Testing 0.2.0` contract suite has **21** facts (not 22) — all green on Postgres; `PostgresMemoryStoreTests` adds two Daedalus facts (multi-batch keyset stream with `CreatedAt` ties larger than a page while the consumer clears `IndexPending`; AND tag filter incl. blank tag). Full integration suite (324) green.
- **G2 code-quality follow-ups (after Task 4, commit `fix(memory): row-value keyset streaming, stream index, stricter store validation`):** (1) `PostgresMemoryStore.StreamAsync` now uses a **row-value keyset predicate** — `EF.Functions.GreaterThan(ValueTuple.Create(m.CreatedAt, m.Id), ValueTuple.Create(last.CreatedAt, last.Id))` → `("CreatedAt", "Id") > (@p0, @p1)` — with `ORDER BY "CreatedAt", "Id"`, replacing the boundary-exclusion list (same total order in SQL on both sides, so uuid byte order vs .NET `Guid` order is irrelevant); still batches of 256 with a fresh DbContext per batch, internal `streamBatchSize` seam kept. (2) `AgentMemoryConfiguration`: new `IX_AgentMemory_CreatedAt_Id` (supports the keyset scan; §0.3 updated), `Text` is an unbounded `text` column (design §6 parity; the aggregate enforces the 4000 limit), `Kind` max length uses `AgentMemory.MaxKindLength`. (3) Aggregate validates `Kind` with Thalos' rule (`^[a-z][a-z0-9_-]{0,31}$`, `MaxKindLength = 32`) and `OwnerId ≤ 256` (`MaxOwnerIdLength`) so violations surface as `MemoryValidationFailed`, never as a varchar `MemoryStoreFailed`; kind lower-casing/trim dropped — stored as given, like `InMemoryMemoryStore` (the service validates upstream). (4) `UpdateAsync` maps `DbUpdateConcurrencyException` (row hard-deleted between read and save) to `MemoryNotFound` before the general `DbUpdateException` → `MemoryStoreFailed`. (5) `AgentMemory.RecordRecall` removed (unused — `MarkRecalledAsync` is one `ExecuteUpdateAsync`); store remarks state that Npgsql/connection exceptions propagate (session-store policy). Domain facts: 12 → 14 (kind rule theory incl. length 32/33, owner 256/257; `RecordRecall` fact dropped); memory store integration facts unchanged (23), all green.
- **Task 6 (2026-08-17):** as written except: (1) `AgentMemoryConfig` has **no `ExposeTools`** (§0.2b — `AgentMemorySettings` is `{ Enabled, TopK }`; tool visibility comes from `AgentDefinition.Tools` globs), so `ToDefinition` maps two members. (2) `AddDaedalusAgents` does `services.TryAddSingleton(embeddingGenerator)` when the caller passes a generator: `UseRagNetMemory` resolves the generator from **DI**, so a host that only handed the instance to this call (for Sentinel) would otherwise get an unavailable index; `TryAdd` keeps a host's own registration winning (the Api registers the Ollama client before this call, so it is a no-op there). (3) `ILearningsMemory` registration and the two port assertions the plan puts in Task 6's tests were deferred to **Task 8** (the type does not exist yet); Task 6's registration tests instead assert the store/index/hosted-service shape and add four facts the plan left implicit (index is `UnavailableMemoryIndex.Instance` without a generator, `Thalos:Memory:Enabled=false` reaches `MemoryOptions`, an agent without a `Memory` section gets `Memory == null`, `AddDaedalusMemory` registers no agents). (4) Connection string resolution is `configuration.GetConnectionString("daedalus") ?? DatabaseSettings.GetDefaultConnectionString()` in a shared `ResolveConnectionString` helper. (5) **The reindex hosted-service registration that Task 6's step 3 lists (`if (options.Memory.Enabled && options.Memory.Reindex.Enabled) services.AddHostedService<ReindexPendingMemoriesHostedService>();`) is deliberately deferred to Task 13**, which creates the type; `MemoryConfig.Reindex` is bound and validated from Task 6 on, and the registration tests already assert the service is *absent* from the console host, so Task 13 only has to add the line and flip that assertion for the API host. (6) The `dotnet run --project src/Daedalus.AppHost` smoke check in step 4 was **not** run at the time; see the Tasks 6–10 review bullet below for the outcome of the later attempt.
- **Task 7 (2026-08-17):** as written. `FailurePatternRecord` has `required`-init members (not positional), so the persistence test builds it with an object initialiser. `LearningMemoryMapping.Source` is a plain interpolated string (MA0185 rejects `string.Create(CultureInfo.InvariantCulture, …)` when every hole is culture-invariant); `Tags` lower-casing sits under a CA1308 pragma. `LearningsService`'s now-unused `FormatCategory` helper was deleted; `GetEnrichmentContextAsync` keeps its `projectId`/`currentTaskId` parameters (interface contract) but no longer uses them — recall ranks by prompt similarity and the shared owner is global. `LearningMemoryMappingTests` builds its fixed task id with the `new Guid(int, short, short, byte…)` constructor rather than `Guid.Parse`, because MA0176 ("optimize guid creation") rejects both `Guid.Parse("…")` and `new Guid("…")` on a constant. Application facts 327 → 340 (+8 mapping, +5 persistence). **Behaviour change:** enrichment lost the self-task and project filters — recorded in §0.3, not only here.
- **Task 8 (2026-08-17):** as written except (1) `RecallOptions.MaxChars = 0` rather than `int.MaxValue` — the 0.2.0 XML doc defines "0 or negative = no budget", which is the documented way to say "TopK alone caps the result"; (2) both `[LoggerMessage]` methods take `AgentErrorCode` instead of `string` (CA1873 rejects `Error.Code.ToString()` as an argument to a `Debug`-level log). Tests: the plan's three facts plus two (recall failure maps to a CSFE failure, blank query short-circuits without touching `IMemoryService`). The two port assertions from Task 6 were added here and are green.
- **Task 9 (2026-08-17):** as written. `DaedalusLearningsTools` is *not* re-registered in DI — `McpToolBuilder` discovers `[McpServerToolType]` classes by reflection and builds them with `ActivatorUtilities.CreateInstance`, so it only needs `ILearningsMemory` in the container (a missing registration is caught and logged, not fatal). `tests/Daedalus.Tests.Unit/Configuration/ApiThalosConfigurationTests` pins the agent's `Tools` list and instruction text, so it was updated for `memory__*`. Added one fact beyond the plan (`maxResults` clamping to 1..20). Infrastructure facts 127 → 130, Application facts 345 → 343 (the two `SearchLearnings` wrapper facts were deleted, one `LocalToolSource` fact rewritten).
- **Task 10 (2026-08-17):** as written; `Daedalus.Console` gains the `Daedalus.Agents` project reference and `OllamaSharp`, and the AppHost's `console` project gains `.WithReference(ollama)`. The `dotnet run --project src/Daedalus.AppHost` check was not run; `AddDaedalusMemory_registers_memory_for_the_ralph_console_host_without_agents` covers that the console wiring resolves without Ollama.
- **G3 review follow-ups (after Task 10, commit `fix(memory): only the API host creates the Rag.NET schema; enforce memory limits; console memory config`):**
  1. **Schema race (blocker).** `ConfigureMemory` set `EnsureSchemaOnStartup = true` in *both* extension methods, and the AppHost starts `api` and `console` concurrently against one database — two racing `CREATE EXTENSION`/`CREATE TABLE`/`CREATE INDEX` sweeps can fail on the pg catalog with a duplicate-key error, and the initializer throws to fail host start. `ConfigureMemory` now takes an `ensureSchema` flag: **`true` from `AddDaedalusAgents` (the API host owns the schema), `false` from `AddDaedalusMemory`.** Until the API has created `rag_chunks` the console degrades along the designed path (memories stored `index_pending`; the API's reindex sweeper repairs them). Four tests pin it: the API path registers exactly one `RagNetMemorySchemaInitializer` with `EnsureSchemaOnStartup = true`, the console path registers none with `false` (once against synthetic config, once against the shipped `Daedalus.Console/appsettings.json`).
  2. **Thalos limits enforced in `LearningMemoryMapping`.** The parser feeds it raw LLM output, so an over-long pattern or keyword made `MemoryRules.Validate` reject the whole learning. `Text` is now truncated to `AgentMemory.MaxTextLength` (4000) and every tag to `AgentMemory.MaxTagLength` (32); `MaxFreeTags` is derived as `AgentMemory.MaxTags - 2` instead of a literal 8. The Domain aggregate mirrors the Thalos rules, so the constants stay Thalos-free (Application must not reference `Thalos*`). Three new facts, including one that round-trips a 5000-char/90-char-tag learning through `AgentMemory.Create`. The "failed to persist learning" log was already `LogLevel.Warning` (EventId 101) — no change needed.
  3. **Console config drift.** `src/Daedalus.Console/appsettings.json` gained the same `Thalos:Memory` block as the API's. Rather than accept silent duplication, the console file is now linked into `Daedalus.Tests.Unit` as `Daedalus.Console.appsettings.json` and `Console_and_api_agree_on_the_shared_memory_settings` fails the build if the two hosts diverge on `SharedOwnerId`, `VectorDimensions` or `RalphRecall`. (Linking the API's file into the console instead was rejected: both projects ship an `appsettings.json`, and the API's carries Anthropic/Sentinel/Authentication/ModelPricing sections the worker has no business binding.)
  4. **Daedalus-only memory keys validated.** `ValidateMemoryConfig` runs in both extension methods right after binding (registration time, like `ParseAgentId`) and throws `InvalidOperationException` naming the offending key: `SharedOwnerId` non-blank, `VectorDimensions > 0`, `RalphRecall.TopK ≥ 1`, `RalphRecall.MinScore ∈ [0,1]` (NaN rejected), and each `Reindex` interval `> 0`. A five-row theory covers one failing key each. Thalos still validates its own `MemoryOptions` on start.
  5. **`RalphRecall.TopK` wired, and the type moved.** The knob was dead. `RalphRecallConfig` moved from `Daedalus.Agents` to **`Daedalus.Application.Configuration.RalphRecallConfiguration`** (a plain POCO; `Daedalus.Agents` already references Application, so `MemoryConfig.RalphRecall` just points at it). `AddRalphLoopMiddleware` binds it from `Thalos:Memory:RalphRecall` and `TryAddSingleton`s it — every Ralph host calls that method — and `LearningsEnrichmentMiddleware` now takes its max-learnings from `recall.TopK` (default 10) instead of the hardcoded `_maxLearnings`. One class, one configuration key, three consumers (middleware, MCP tool, Thalos adapter), so they cannot diverge. Two facts (configured 25 reaches the call; default is 10).
  6. **Mutual exclusion.** `MemoryConfig`, `RalphRecallConfiguration` and `ILearningsMemory` are registered with `TryAddSingleton` in both extension methods, and `AddDaedalusMemory`'s doc states it is for hosts that do **not** call `AddDaedalusAgents` (a double call is harmless but contributes nothing).
  7. **Minors.** `ConfigureAwait(false)` on the two new `LearningsService` awaits (M2). `ThalosLearningsMemory` reads the shared owner from `IOptions<MemoryOptions>.Value.SharedOwnerId` — the value Thalos itself uses, already whitespace-normalised — instead of `MemoryConfig.SharedOwnerId`, and returns a CSFE failure (EventId 502 warning) rather than writing under a blank owner if it ever resolves to null (M8). The recall clamps moved onto `RalphRecallConfiguration` as `MinTopK`/`MaxTopK`/`MaxToolTopK` (1/50/20) (M4). The now-parameterised `EnsureSchemaOnStartup` literal is gone (M3). M5–M7 left to Task 19.
  8. **AppHost smoke run (done this time).** `dotnet run --project src/Daedalus.AppHost` with `PARAMETERS__DB_USERNAME/DB_PASSWORD/ANTHROPIC_API_KEY` set (a dummy Anthropic key is fine — the provider reads it lazily). Ollama pulled `nomic-embed-text` (274 MB) and both hosts came up: **`rag_chunks` exists in `daedalus` with `embedding vector(768)`** and the `vector` extension is installed — created by the API's `RagNetMemorySchemaInitializer` — and `Daedalus.Api` **and** `Daedalus.Console` both stayed up 12+ minutes (a DI or `ValidateMemoryConfig` failure kills the host in seconds), i.e. the console resolves `AddDaedalusMemory`/`ILearningsMemory`/`RalphRecallConfiguration` and no longer races the API for the schema. Two unrelated observations on this machine, **not** caused by this work and not in scope here: the pgvector data volume is stale (`collation version mismatch` warnings, created under 2.41, OS provides 2.36) and the `migrations` resource exited without creating the EF tables — `public` held only `rag_chunks`. Worth a look before Task 12 adds `AddAgentMemories`; a `docker volume rm` of the postgres volume is the usual fix.
  Unit facts 858 → 872; integration 324, all green.
- **Task 11 (2026-08-17):** as written. `LearningCategory`/`LearningSeverity` already lived in their own files, so deleting `StructuredLearningEntry.cs` kept them. `Services/NoOp/` still holds `NoOpGitWorkflowService`/`NoOpLoopbackEvaluator`, so the folder stays; the `using Daedalus.Infrastructure.Services.NoOp;`, `Microsoft.Extensions.AI` and `Microsoft.Extensions.Logging` usings in `InfrastructureServiceExtensions` went with the `IEmbeddingService` block. `PostgresAgentSessionStoreTests.TestDbContextFactory` now calls `PostgresFixture.CreateDbContextOptions(connectionString)`. Only `ServiceDefaults/AspireExtensions.cs` and `Persistence/ApplicationDbContextFactory.cs` carried a `using Pgvector.EntityFrameworkCore;`; the four test fixtures resolved `UseVector()` without one, so they needed no `using` removal. Unit facts 872 → 853 (`StructuredLearningEntryTests`, 19 facts, deleted); integration 324, build 0 warnings.
- **Task 12 (2026-08-17):** three deviations. (1) **The plan's test snippet does not compile:** `'{{}}'` inside a single-`$` interpolated raw string is two consecutive opening braces, not an escape (CS9006) — the empty tag array is seeded as `ARRAY[]::text[]` instead. The test also uses `PostgresFixture.CreateDbContextOptions(connectionString)` rather than building options inline. (2) **`dotnet ef migrations add` crashed with a `NullReferenceException`** in `MigrationsModelDiffer.Initialize` (null `RelationalTypeMapping`). Cause: the scaffolder diffs *both* directions, and the **down** direction re-creates `StructuredLearnings` from the snapshot — whose `Embedding` property is `float[]` with store type `vector(384)`, a mapping that no longer exists once `Pgvector.EntityFrameworkCore` is gone (Task 11). Fix: delete the two `Embedding` lines from `ApplicationDbContextModelSnapshot.cs` before scaffolding (the whole `StructuredLearningEntry` block disappears from the regenerated snapshot anyway). Consequence, recorded in the migration's `Down` doc comment: `Down` recreates `StructuredLearnings` **without** the `vector(384)` column — acceptable, since `Down` restores no rows either. (3) EF put `DropTable("StructuredLearnings")` first; it was moved after `CreateTable` + the five `CreateIndex`es and after the copy SQL, per the plan. The file also carries the `#pragma warning disable CA1861` header that `20260816174546_AddAgentSessions.cs` uses (composite-index `new[]` arrays) and had its BOM stripped (`dotnet format` CHARSET). Category/severity/importance/source/text mapping matches the C# exactly; the free-tag handling did **not** (no trim, no per-tag truncation, no de-duplication, and the 8-tag cap applied too early) — judged cosmetic at the time and **corrected in the Tasks 11–13 review follow-ups below**, because the first edit of such a memory is rejected by `AgentMemory.Update`. The migration was verified against **Testcontainers** only (`dotnet run --project src/Daedalus.Migrations` against the developer's local volume was skipped on purpose: that volume is stale, see the AppHost bullet above). Integration facts 324 → 325.
- **Task 13 (2026-08-17):** as written. `MemoryIndexHealth`'s third member is `Detail` (Plan A §0.2b), not `Reason` as the plan's snippet has it. `Microsoft.Extensions.TimeProvider.Testing` 10.9.0 (the version `Thalos.NET.Testing` pulls in transitively for the integration project) was added to CPM and to `Daedalus.Tests.Unit.Application`, and `Daedalus.Agents.csproj` gained `<InternalsVisibleTo Include="Daedalus.Tests.Unit.Application" />` — the hosted service is `internal`. The test file qualifies `ZeroAlloc.Results.Result<,>` because `CSharpFunctionalExtensions` is a global using in that project. Registration: the one line in `AddDaedalusAgents` guarded by `options.Memory.Enabled && options.Memory.Reindex.Enabled`, plus three registration facts (present on the API host; absent for `Reindex:Enabled=false` **and** for `Memory:Enabled=false`; the pre-existing console fact already asserts absence). `ValidateMemoryConfig` rejects every non-positive `Reindex` interval, so the two `Task.Delay` calls cannot throw `ArgumentOutOfRangeException` and take the host down. **The AppHost end-to-end check in step 4 was not run** (the local Postgres volume is stale and out of scope here); the reindex path is covered by unit tests only. Unit facts 853 → 860.
- **Task 14 (2026-08-17):** reduced to `MemoryDto` + `AgentDtoMapper.ToDto(MemoryRecord, string? sharedOwnerId)` — the `AgentErrorResults.ToStatusCode` arms and their theory rows were pulled forward into Task 1 (see the Task 1 bullet), so this task only verified them. The two `ApiJsonSerializerContext` entries the plan lists under Task 15 were added here (the DTO exists from this commit on; the controller needs nothing else). Tests: the plan's mapper fact plus a four-row theory pinning the `IsShared` rule (shared owner matches **ordinally**, a null `SharedOwnerId` never marks anything shared). Application facts 362 → 367.
- **Task 15 (2026-08-17):** as written except three fixes to the snippet. (1) `MemoryPage.TotalCount`, not `.Total` (§0.2b). (2) `AgentError.MemoryForbidden` takes a `MemoryId`, not a message (§0.2b), so the 403 detail is Thalos' "Memory '<id>' belongs to another owner." rather than the plan's sentence — the `code` extension is what clients switch on. (3) The `kind` filter goes through `MemoryKind.TryParse` (which trims and lower-cases) instead of a raw `ToLowerInvariant()`: a kind Thalos could never have stored (`"Not a Kind!"`) is a client mistake and answers **400**, not an empty page — and it avoids a CA1308 pragma in the controller. Two shared helpers were extracted (`VisibleOwners`, `MaxPageSize = MemoryQuery.MaxPageSize`). Tests: 11 facts (the plan's 7 plus a shared-owner-as-caller listing that must not repeat the owner, a `MemoryStoreFailed` → 502 mapping, `hard=true` pass-through, and the filter-validation theory) and the smoke fact, which also asserts a foreign id answers 404 through the real pipeline. Integration facts 325 → 338.
- **Task 16 (2026-08-17):** as written (`using Daedalus.Application.DTOs;` added for `PagedResultDto<T>`).
- **Task 17 (2026-08-17):** as written, with the panel added as a third child of the page's existing horizontal stack instead of introducing a wrapper stack (the outer stack is already `Orientation.Horizontal`, so the plan's "wrap the chat column" step is a no-op); it renders under `_memoriesOpen && SessionId is not null` so closing a session cannot leave it stranded. The header's two buttons are now a small horizontal stack. `memory-quarantined` gets a fifth `Apply` arm ("A recalled memory was quarantined and not shown") — the plan listed four but the mapper emits five kinds. `Age`, the fields and the four methods live in the `@code` block with the rest of the page state (`Agent.razor.cs` holds only the view models and interop teardown). Prev/Next got `data-testid`s for later use.
- **Task 18 (2026-08-17):** as written plus one fix that was **not** optional. (1) **The Agent browser category had been inconclusive since Task 6**: `AddDaedalusAgents` resolves the memory connection string from configuration, and `E2EServerFixture` never set `ConnectionStrings:daedalus` (it hands the container string to the DbContext registrations directly), so Rag.NET's schema initializer dialled `127.0.0.1:5432`, host start threw, and all four tests reported `Inconclusive` — which `dotnet test` prints as `Skipped` with a **0-failure exit code**, so nobody noticed. The fixture now pushes `ConnectionStrings:daedalus` into the configuration with an in-memory source right before `AddDaedalusAgents`; `rag_chunks` is then created in the test container (the fixture already installs the `vector` extension). (2) `MemoryRecalledEvent` is `(SessionId, TurnId, IReadOnlyList<MemoryId>, int Chars)` — no `Count` argument (§0.2b), so the stub's `yield return` drops the plan's `ids.Count`. (3) The E2E user id moved onto `TestAuthHandler.UserId` (the seed and the auth handler must agree); `StubAgentRuntime.SharedOwnerId` is as planned. (4) The seed is a separate `SeedMemoriesAsync` guarded on `AgentMemories.AnyAsync()` (the existing `SeedTestDataAsync` returns early when projects exist, which would have skipped the memories). (5) The scenario uses the repo's `SaveRegressionScreenshotAsync("agent-memories.png")` helper rather than a hard-coded `docs/` path, and additionally asserts the browse list holds exactly two items before and one after the forget. Playwright Agent category 4 → 5 tests, all green.
- **Tasks 11–13 review follow-ups (2026-08-17, commit `fix(memory): normalise tags in the learnings copy; keep the Down chain runnable; cover the sweep loop`):**
  1. **The copy SQL did not normalise tags like `LearningMemoryMapping` (I1, blocker for later edits).** It lower-cased only, and capped at the first 8 array positions **before** blanks and duplicates were removed, so untrimmed, over-32-char, blank and duplicate tags reached `AgentMemories`. Nothing fails at migration time (EF materialises through the backing field and never calls `AgentMemory.Create`), but the first `AgentMemory.Update` of such a memory is rejected by `NormaliseTags` with an error the user cannot act on. The free-tag subquery now trims, lower-cases, `left(…, 32)`s, drops blanks/NULLs, de-duplicates keeping the first occurrence and *then* takes 8 — the exact `LearningMemoryMapping.Tags` pipeline, including its quirk that a free tag equal to the category/severity tag is **not** de-duplicated against them. **The plan's Task 12 snippet was amended** so the broken version is not re-derived. The migration comment now states precisely what it mirrors instead of claiming parity, and names the residual mismatch (M2): Postgres `left()`/`length` count characters, `AgentMemory` counts UTF-16 units, so non-BMP text can exceed the aggregate's limit — harmless, and `left()` being code-point-based means nothing splits mid-surrogate.
  2. **`Down` left a schema the predecessor's `Down` could not run against (I2).** The scaffolded `CreateTable("StructuredLearnings")` omits `Embedding` (the model lost the `vector(384)` mapping with Pgvector), so `AddSemanticEmbeddings.Down`'s `DropColumn` failed with `42703: column "Embedding" of relation "StructuredLearnings" does not exist` — **after** this migration's `Down` had already dropped `AgentMemories`, i.e. a rollback destroyed every memory and then stopped half-done. `Down` now re-adds the column (`ALTER TABLE … ADD COLUMN IF NOT EXISTS "Embedding" vector(384)`; the extension is still installed because Rag.NET needs it). Verified by removing the line again and watching the new fact fail with exactly that error. Beyond the review's ask, the check is kept as a permanent test (`Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain`, ~0.5 s): the failure mode is silent and destructive, and only a rollback exercises it.
  3. **The hosted-service tests never ran the loop (I3).** All four called `RunOnceAsync` directly and never advanced `FakeTimeProvider`, leaving `StartupDelay`, the wait-on-returned-interval behaviour and the cancellation path uncovered — and nothing pinned that the clock-aware `Task.Delay(TimeSpan, TimeProvider, CancellationToken)` overload is used (a wall-clock delay would still have passed). The new fact starts the service, asserts no probe one second short of `StartupDelay`, advances the last second and waits for the probe, asserts no second probe one second short of `SweepInterval`, advances and waits for it, then `StopAsync`s and asserts both passes reached `ReindexAsync(PendingOnly)`. It re-advances the fake clock between polls because the loop registers its next timer asynchronously.
  4. **Migration-test coverage (M1).** The seed now covers every `CASE` arm (`successpattern`/`dependencyinfo`/`architecturedecision`, `medium`/`critical`, importance 1.0 and 0.5), a `NULL LastReferencedAt`, and a 13-entry tag array with an untrimmed entry, a duplicate that only collides after lower-casing, a blank, a whitespace-only entry, a 40-char tag and enough tail entries that the 8-tag cap must be applied last — these assertions fail against the pre-I1 SQL. A second fact runs the migration on an empty `StructuredLearnings`, and every copied row is fed back through `AgentMemory.Update` to pin that a later edit passes validation. Integration facts 338 → 340 (the file went 1 → 3 facts).
  5. **Minors.** M3: the `AddHostedService<ReindexPendingMemoriesHostedService>()` call moved to just after `services.AddThalos(…)` so registration order matches intended start order (the Rag.NET schema initializer first). M6: `Down`'s doc now says the drop destroys **every** memory written since the upgrade, not only the migrated learnings. M7: `Microsoft.Extensions.TimeProvider.Testing` moved out of the `<!-- Analyzers -->` group into `<!-- Testing -->`.
  6. **Accepted / deferred, deliberately not done.** M4 (ramp the reindex log level down after N consecutive unavailable probes): the message is `Information` and fires at most every `RetryInterval` (2 min default), so the noise ceiling is low; revisit if an operator complains. M5 (a command timeout on the copy `INSERT … SELECT`): EF's default 30 s applies, the copy is a single set-based statement over a table that holds thousands of rows at most, and a migration that times out fails loudly and rolls back — a timeout knob would add a configuration surface with no failure mode to protect. M8 (kill the host mid-sweep and assert nothing is left inconsistent): the sweeper only clears `IndexPending` after a successful upsert, so a crash leaves rows pending and the next sweep repeats them — the property is Thalos' (`ReindexAsync`) and is covered by its own suite; a Daedalus-side crash test would exercise `BackgroundService`, not our code.
  7. **Spec-review advisories, both now fixed.** The `Down`-chain knock-on onto `AddSemanticEmbeddings` (item 2) and the copy SQL's divergence from `LearningMemoryMapping` in trim/blank ordering as well as truncation and de-duplication (item 1) were both raised in the spec review before the code review found them; they are recorded here rather than left as prose in the review thread.

---

## Task 1: Branch, local pack pin, package refs, API reconciliation (G1)

**Files:** create `packages-local/*.nupkg` (8 files), modify `nuget.config`, `Directory.Packages.props`, `src/Daedalus.Agents/Daedalus.Agents.csproj`.

**Step 1:** `git switch -c feature/thalos-memory`

**Step 2:** Copy the packs and point the feed at them (relative path works for CI):
```powershell
$stamp = (Get-ChildItem C:\Projects\Prive\.nuget-local\Thalos.NET.Memory.0.2.0-local.*.nupkg | Select-Object -First 1).BaseName -replace '^Thalos\.NET\.Memory\.',''
$stamp   # e.g. 0.2.0-local.20260818101500 — use this literal below
New-Item -ItemType Directory -Force packages-local | Out-Null
Copy-Item "C:\Projects\Prive\.nuget-local\Thalos.NET*.$stamp.nupkg" packages-local\
Get-ChildItem packages-local | Measure-Object | Select-Object -ExpandProperty Count   # expected 8
```
`nuget.config`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="thalos-local" value="packages-local" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
    <packageSource key="thalos-local"><package pattern="Thalos.NET*" /></packageSource>
  </packageSourceMapping>
</configuration>
```
`Directory.Packages.props` — replace the Thalos block:
```xml
    <!-- Thalos.NET (local pack during phase 1.2; switched to nuget.org 0.2.0 in the last task) -->
    <PackageVersion Include="Thalos.NET" Version="0.2.0-local.<stamp>" />
    <PackageVersion Include="Thalos.NET.Abstractions" Version="0.2.0-local.<stamp>" />
    <PackageVersion Include="Thalos.NET.Testing" Version="0.2.0-local.<stamp>" />
    <PackageVersion Include="Thalos.NET.Mcp" Version="0.2.0-local.<stamp>" />
    <PackageVersion Include="Thalos.NET.Anthropic" Version="0.2.0-local.<stamp>" />
    <PackageVersion Include="Thalos.NET.Sentinel" Version="0.2.0-local.<stamp>" />
    <PackageVersion Include="Thalos.NET.Memory" Version="0.2.0-local.<stamp>" />
    <PackageVersion Include="Thalos.NET.Memory.RagNet" Version="0.2.0-local.<stamp>" />
```
`Daedalus.Agents.csproj` — add after `Thalos.NET.Sentinel`:
```xml
        <PackageReference Include="Thalos.NET.Memory" />
        <PackageReference Include="Thalos.NET.Memory.RagNet" />
```
**Step 3:** `dotnet restore --force-evaluate; dotnet build --nologo` → 0 errors. If `NU1010`/transitive-pin errors name a `Rag.NET.*` package, add a `<PackageVersion Include="Rag.NET.Abstractions" Version="0.1.x" />` (+ `Rag.NET.VectorStores.PgVector`) matching the version Thalos.NET.Memory.RagNet depends on (`dotnet nuget why Thalos.NET.Memory.RagNet Rag.NET.Abstractions` shows it).

**Step 4 — API reconciliation (mandatory):**
```powershell
$v = "0.2.0-local.<stamp>"
Select-String -Path "$env:USERPROFILE\.nuget\packages\thalos.net.memory\$v\lib\net10.0\Thalos.NET.Memory.xml" -Pattern 'name="[TMPF]:' | ForEach-Object { $_.Matches[0].Value } | Sort-Object -Unique
Select-String -Path "$env:USERPROFILE\.nuget\packages\thalos.net.memory.ragnet\$v\lib\net10.0\Thalos.NET.Memory.RagNet.xml" -Pattern 'name="[TMP]:' | ForEach-Object { $_.Matches[0].Value } | Sort-Object -Unique
Select-String -Path "$env:USERPROFILE\.nuget\packages\thalos.net.abstractions\$v\lib\net10.0\Thalos.NET.Abstractions.xml" -Pattern 'Memory' | Select-Object -First 60
Select-String -Path "$env:USERPROFILE\.nuget\packages\thalos.net.testing\$v\lib\net10.0\Thalos.NET.Testing.xml" -Pattern 'MemoryStoreContractTests'
```
Compare with §0.2; correct any name in the plan's code before using it (keep a short list of substitutions in the commit body of this task). Confirm: `MemoryId.Value` is `Guid`; the events derive from `AgentEvent`; the store replacement method name.

**Step 5:** `dotnet test --nologo --filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"` → all green (nothing new used yet). Commit:
```
build: pin Thalos.NET 0.2.0-local packs incl. Memory and Memory.RagNet from packages-local
```

---

## Task 2: Domain — `AgentMemory` aggregate (G2)

**Files:** create `src/Daedalus.Domain/Entities/AgentMemory.cs`; test `tests/Daedalus.Tests.Unit.Domain/Entities/AgentMemoryTests.cs`.

**Step 1: failing tests**
```csharp
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

public sealed class AgentMemoryTests
{
    private static readonly DateTime T0 = new(2026, 8, 17, 10, 0, 0, DateTimeKind.Utc);

    private static AgentMemory Valid(string text = "The user prefers xUnit.") =>
        AgentMemory.Create(Guid.NewGuid(), "alice", null, "fact", text, ["Testing", " xunit "], "tool:memory__remember", 0.7, T0, indexPending: true).Value;

    [Fact]
    public void Create_sets_fields_and_normalises_tags()
    {
        var m = Valid();
        m.OwnerId.Should().Be("alice");
        m.AgentId.Should().BeNull();
        m.Kind.Should().Be("fact");
        m.Tags.Should().Equal("testing", "xunit");
        m.Importance.Should().Be(0.7);
        m.CreatedAt.Should().Be(T0);
        m.UpdatedAt.Should().Be(T0);
        m.RecallCount.Should().Be(0);
        m.IsArchived.Should().BeFalse();
        m.IndexPending.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("alice", "")]
    [InlineData("alice", " ")]
    public void Create_requires_owner_and_text(string owner, string text)
    {
        AgentMemory.Create(Guid.NewGuid(), owner, null, "fact", text, [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_empty_id_bad_kind_long_text_too_many_tags_and_importance_out_of_range()
    {
        AgentMemory.Create(Guid.Empty, "a", null, "fact", "t", [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "", "t", [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", new string('x', 4001), [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", "t", Enumerable.Range(0, 11).Select(i => $"t{i}"), "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", "t", [], "", 1.5, T0, false).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Update_changes_only_supplied_fields_and_bumps_updated_at()
    {
        var m = Valid();
        var later = T0.AddMinutes(5);

        m.Update(text: null, tags: ["b"], importance: null, isArchived: true, indexPending: false, later).IsSuccess.Should().BeTrue();

        m.Text.Should().Be("The user prefers xUnit.");
        m.Tags.Should().Equal("b");
        m.Importance.Should().Be(0.7);
        m.IsArchived.Should().BeTrue();
        m.IndexPending.Should().BeFalse();
        m.UpdatedAt.Should().Be(later);
    }

    [Fact]
    public void Update_validates_text_tags_and_importance()
    {
        var m = Valid();
        m.Update(" ", null, null, null, null, T0).IsFailure.Should().BeTrue();
        m.Update(null, null, 2.0, null, null, T0).IsFailure.Should().BeTrue();
        m.Update(null, Enumerable.Range(0, 11).Select(i => $"t{i}").ToList(), null, null, null, T0).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void RecordRecall_increments_and_stamps()
    {
        var m = Valid();
        m.RecordRecall(T0.AddHours(1));
        m.RecordRecall(T0.AddHours(2));
        m.RecallCount.Should().Be(2);
        m.LastRecalledAt.Should().Be(T0.AddHours(2));
    }
}
```
**Step 2:** run → fails (type missing).

**Step 3: implement** `src/Daedalus.Domain/Entities/AgentMemory.cs`
```csharp
using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     One curated agent memory (Thalos <c>MemoryRecord</c> persisted by Daedalus). Domain stays framework-free: ids are
///     GUIDs (Thalos typed ids are Guid-backed; the adapter converts), the kind is its wire string, times are UTC.
///     Vectors are not stored here — the Rag.NET index is a rebuildable cache; <see cref="IndexPending"/> marks rows
///     whose vector still has to be (re)built.
/// </summary>
public sealed class AgentMemory : Entity<Guid>
{
    public const int MaxTextLength = 4000;
    public const int MaxTags = 10;
    public const int MaxTagLength = 32;

    private readonly List<string> _tags = [];

    public string OwnerId { get; private set; } = string.Empty;
    public Guid? AgentId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public IReadOnlyList<string> Tags => _tags.AsReadOnly();
    public string Source { get; private set; } = string.Empty;
    public double Importance { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? LastRecalledAt { get; private set; }
    public int RecallCount { get; private set; }
    public bool IsArchived { get; private set; }
    public bool IndexPending { get; private set; }

    private AgentMemory() { } // EF Core

    public static Result<AgentMemory> Create(
        Guid id, string ownerId, Guid? agentId, string kind, string text, IEnumerable<string>? tags, string source,
        double importance, DateTime utcNow, bool indexPending)
    {
        if (id == Guid.Empty) return Result.Failure<AgentMemory>("Memory id is required.");
        if (string.IsNullOrWhiteSpace(ownerId)) return Result.Failure<AgentMemory>("Owner id is required.");
        if (string.IsNullOrWhiteSpace(kind)) return Result.Failure<AgentMemory>("Kind is required.");
        var textCheck = ValidateText(text);
        if (textCheck.IsFailure) return Result.Failure<AgentMemory>(textCheck.Error);
        var normalisedTags = NormaliseTags(tags);
        if (normalisedTags.IsFailure) return Result.Failure<AgentMemory>(normalisedTags.Error);
        if (importance is < 0 or > 1) return Result.Failure<AgentMemory>("Importance must be between 0 and 1.");

        var memory = new AgentMemory
        {
            Id = id,
            OwnerId = ownerId,
            AgentId = agentId,
            Kind = kind.Trim().ToLowerInvariant(),
            Text = text.Trim(),
            Source = source ?? string.Empty,
            Importance = importance,
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
            IndexPending = indexPending,
        };
        memory._tags.AddRange(normalisedTags.Value);
        return Result.Success(memory);
    }

    /// <summary>Applies a partial update; <see langword="null"/> leaves a field untouched. Always bumps <see cref="UpdatedAt"/>.</summary>
    public Result Update(string? text, IReadOnlyList<string>? tags, double? importance, bool? isArchived, bool? indexPending, DateTime utcNow)
    {
        if (text is not null)
        {
            var textCheck = ValidateText(text);
            if (textCheck.IsFailure) return textCheck;
        }

        Result<List<string>>? newTags = null;
        if (tags is not null)
        {
            newTags = NormaliseTags(tags);
            if (newTags.Value.IsFailure) return Result.Failure(newTags.Value.Error);
        }

        if (importance is < 0 or > 1) return Result.Failure("Importance must be between 0 and 1.");

        if (text is not null) Text = text.Trim();
        if (newTags is { } t) { _tags.Clear(); _tags.AddRange(t.Value); }
        if (importance is { } i) Importance = i;
        if (isArchived is { } a) IsArchived = a;
        if (indexPending is { } p) IndexPending = p;
        UpdatedAt = utcNow;
        return Result.Success();
    }

    public void RecordRecall(DateTime utcNow)
    {
        RecallCount++;
        LastRecalledAt = utcNow;
    }

    private static Result ValidateText(string text) =>
        string.IsNullOrWhiteSpace(text) ? Result.Failure("Text is required.")
        : text.Trim().Length > MaxTextLength ? Result.Failure($"Text must be at most {MaxTextLength} characters.")
        : Result.Success();

    private static Result<List<string>> NormaliseTags(IEnumerable<string>? tags)
    {
        var list = (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count > MaxTags) return Result.Failure<List<string>>($"At most {MaxTags} tags are allowed.");
        if (list.Any(t => t.Length > MaxTagLength)) return Result.Failure<List<string>>($"Tags must be at most {MaxTagLength} characters.");
        return Result.Success(list);
    }
}
```
**Step 4:** run → 6 pass. **Step 5:** commit `feat(memory): AgentMemory aggregate (Thalos-free record of a curated memory)`.

---

## Task 3: EF configuration + `DbSet` (G2)

**Files:** create `src/Daedalus.Infrastructure/Persistence/Configurations/AgentMemoryConfiguration.cs`; modify `ApplicationDbContext.cs`.

```csharp
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

internal sealed class AgentMemoryConfiguration : IEntityTypeConfiguration<AgentMemory>
{
    public void Configure(EntityTypeBuilder<AgentMemory> builder)
    {
        builder.ToTable("AgentMemories");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.OwnerId).IsRequired().HasMaxLength(256);
        builder.Property(m => m.AgentId);
        builder.Property(m => m.Kind).IsRequired().HasMaxLength(32);
        builder.Property(m => m.Text).IsRequired().HasMaxLength(AgentMemory.MaxTextLength);
        // Same backing-field mapping as the former StructuredLearningEntry: text[] column named Tags.
        builder.Property("_tags").HasColumnName("Tags").HasColumnType("text[]").IsRequired();
        builder.Ignore(m => m.Tags);
        builder.Property(m => m.Source).IsRequired().HasMaxLength(256);
        builder.Property(m => m.Importance).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.UpdatedAt).IsRequired();
        builder.Property(m => m.LastRecalledAt);
        builder.Property(m => m.RecallCount).IsRequired();
        builder.Property(m => m.IsArchived).IsRequired();
        builder.Property(m => m.IndexPending).IsRequired();
        builder.HasIndex(m => new { m.OwnerId, m.AgentId }).HasDatabaseName("IX_AgentMemory_Owner_Agent");
        builder.HasIndex(m => new { m.OwnerId, m.Kind }).HasDatabaseName("IX_AgentMemory_Owner_Kind");
        builder.HasIndex(m => m.IsArchived).HasDatabaseName("IX_AgentMemory_IsArchived");
        builder.HasIndex(m => m.IndexPending).HasDatabaseName("IX_AgentMemory_IndexPending");
    }
}
```
`ApplicationDbContext.cs` — add `public DbSet<AgentMemory> AgentMemories => Set<AgentMemory>();` after `AgentMessages`.

> No migration yet — the model is completed in Task 11 (slice removed) and the single `AddAgentMemories` migration is generated in Task 12. Integration fixtures use `EnsureCreatedAsync`, so the table exists in tests from now on.

Build → 0 warnings; unit tests green. Commit `feat(memory): EF mapping for AgentMemories`.

---

## Task 4: `PostgresMemoryStore` + contract tests (G2)

**Files:** create `src/Daedalus.Agents/Memory/PostgresMemoryStore.cs`; test `tests/Daedalus.Tests.Integration/Agents/PostgresMemoryStoreTests.cs`.

**Step 1: failing test** (mirrors `PostgresAgentSessionStoreTests`)
```csharp
using Daedalus.Agents.Memory;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Thalos.Memory;
using Thalos.Testing;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>Runs Thalos.NET's <see cref="IMemoryStore"/> contract suite against the Postgres-backed store.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresMemoryStoreTests(PostgresFixture fixture) : MemoryStoreContractTests, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    protected override ValueTask<IMemoryStore> CreateStoreAsync(TimeProvider clock) =>
        new(new PostgresMemoryStore(new FixtureDbContextFactory(fixture), clock));

    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
```
**Step 2:** run `dotnet test tests/Daedalus.Tests.Integration --filter "FullyQualifiedName~PostgresMemoryStore"` → fails.

**Step 3: implement**
```csharp
using System.Runtime.CompilerServices;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Memory;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Memory;

/// <summary>
///     Thalos memory store over <see cref="ApplicationDbContext"/> (table <c>AgentMemories</c>). Records only — vectors live
///     in the Rag.NET index. Fresh short-lived DbContext per call (the store is a singleton), time from the injected
///     <see cref="TimeProvider"/>, atomic <c>UPDATE</c>s for counters. Same patterns as <c>PostgresAgentSessionStore</c>.
/// </summary>
public sealed class PostgresMemoryStore(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock) : IMemoryStore
{
    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecord, AgentError>> CreateAsync(MemoryRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var created = AgentMemory.Create(
            record.Id.Value, record.OwnerId, record.AgentId?.Value, record.Kind.Value, record.Text, record.Tags, record.Source,
            record.Importance, record.CreatedAt.UtcDateTime, record.IndexPending);
        if (created.IsFailure)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryValidationFailed(created.Error));
        }

        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.AgentMemories.Add(created.Value);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<MemoryRecord, AgentError>.Success(ToRecord(created.Value));
        }
        catch (DbUpdateException ex)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryStoreFailed("Could not store the memory.", ex.GetType().Name));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecord, AgentError>> GetAsync(MemoryId id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.AgentMemories.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id.Value, ct).ConfigureAwait(false);
        return row is null
            ? Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryNotFound(id))
            : Result<MemoryRecord, AgentError>.Success(ToRecord(row));
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecord, AgentError>> UpdateAsync(MemoryId id, MemoryUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.AgentMemories.FirstOrDefaultAsync(m => m.Id == id.Value, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryNotFound(id));
        }

        var applied = row.Update(update.Text, update.Tags, update.Importance, update.IsArchived, update.IndexPending, UtcNow());
        if (applied.IsFailure)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryValidationFailed(applied.Error));
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryStoreFailed("Could not update the memory.", ex.GetType().Name));
        }

        return Result<MemoryRecord, AgentError>.Success(ToRecord(row));
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> DeleteAsync(MemoryId id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var affected = await db.AgentMemories.Where(m => m.Id == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return affected == 1 ? UnitResult<AgentError>.Success() : UnitResult<AgentError>.Failure(AgentError.MemoryNotFound(id));
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var filtered = Apply(db.AgentMemories.AsNoTracking(), query);
        var total = await filtered.CountAsync(ct).ConfigureAwait(false);
        var rows = await filtered
            .OrderByDescending(m => m.UpdatedAt).ThenByDescending(m => m.Id)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct).ConfigureAwait(false);

        IReadOnlyList<MemoryRecord> items = rows.ConvertAll(ToRecord);
        return Result<MemoryPage, AgentError>.Success(new MemoryPage(items, total, page, size));
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> MarkRecalledAsync(IReadOnlyList<MemoryId> ids, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return UnitResult<AgentError>.Success();
        }

        var guids = ids.Select(i => i.Value).ToList();
        var when = at.UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AgentMemories
            .Where(m => guids.Contains(m.Id))
            .ExecuteUpdateAsync(set => set
                .SetProperty(m => m.RecallCount, m => m.RecallCount + 1)
                .SetProperty(m => m.LastRecalledAt, when), ct)
            .ConfigureAwait(false);
        return UnitResult<AgentError>.Success();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MemoryRecord> StreamAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await foreach (var row in Apply(db.AgentMemories.AsNoTracking(), query).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            yield return ToRecord(row);
        }
    }

    private static IQueryable<AgentMemory> Apply(IQueryable<AgentMemory> source, MemoryQuery query)
    {
        var owners = query.OwnerIds.ToList();
        var q = source.Where(m => owners.Contains(m.OwnerId));

        if (query.AgentId is { } agent)
        {
            var agentGuid = agent.Value;
            q = q.Where(m => m.AgentId == null || m.AgentId == agentGuid); // shared-across-agents rows always match
        }

        if (query.Kinds is { Count: > 0 })
        {
            var kinds = query.Kinds.Select(k => k.Value).ToList();
            q = q.Where(m => kinds.Contains(m.Kind));
        }

        if (query.Tags is { Count: > 0 })
        {
            var tags = query.Tags.Select(t => t.Trim().ToLowerInvariant()).ToList();
            q = q.Where(m => EF.Property<List<string>>(m, "_tags").Any(t => tags.Contains(t))); // Npgsql: "Tags" && @tags
        }

        if (!query.IncludeArchived)
        {
            q = q.Where(m => !m.IsArchived);
        }

        if (query.IndexPending is { } pending)
        {
            q = q.Where(m => m.IndexPending == pending);
        }

        return q;
    }

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;

    private static MemoryRecord ToRecord(AgentMemory m) => new()
    {
        Id = new MemoryId(m.Id),
        OwnerId = m.OwnerId,
        AgentId = m.AgentId is { } a ? new AgentId(a) : null,
        Kind = new MemoryKind(m.Kind),
        Text = m.Text,
        Tags = m.Tags,
        Source = m.Source,
        Importance = m.Importance,
        CreatedAt = new DateTimeOffset(m.CreatedAt, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(m.UpdatedAt, TimeSpan.Zero),
        LastRecalledAt = m.LastRecalledAt is { } r ? new DateTimeOffset(r, TimeSpan.Zero) : null,
        RecallCount = m.RecallCount,
        IsArchived = m.IsArchived,
        IndexPending = m.IndexPending,
    };
}
```
> If Npgsql cannot translate the tag overlap through `EF.Property`, replace that branch with `q = q.Where(m => EF.Functions.ArrayOverlaps(…))`-equivalent raw SQL: `db.AgentMemories.FromSql($"""SELECT * FROM "AgentMemories" WHERE "Tags" && {tags}""")` composed before the other filters. If the contract suite expects a different agent-filter semantics (exact match only), drop the `m.AgentId == null ||` half — the suite decides.

**Step 4:** run the filter → contract tests pass. **Step 5:** commit `feat(memory): PostgresMemoryStore passing Thalos memory store contract tests`.

---

## Task 5: Memory events → SSE DTOs (G1)

**Files:** modify `src/Daedalus.Application/DTOs/Agents/AgentDtos.cs`, `src/Daedalus.Agents/Api/AgentDtoMapper.cs`, `src/Daedalus.Api/ApiJsonSerializerContext.cs`; test `tests/Daedalus.Tests.Unit.Application/Agents/AgentDtoMapperTests.cs`.

**Step 1: failing tests** (append to `AgentDtoMapperTests`)
```csharp
    [Fact]
    public void ToDto_maps_memory_events()
    {
        var id = MemoryId.New();

        var recalled = AgentDtoMapper.ToDto(new MemoryRecalledEvent(Session, Turn, 2, [id, MemoryId.New()], 180));
        recalled.Kind.Should().Be("memory-recalled");
        recalled.Memory.Should().BeEquivalentTo(new { Count = 2, Chars = 180 });
        recalled.Memory!.Ids.Should().HaveCount(2).And.Contain(id.ToString());

        var stored = AgentDtoMapper.ToDto(new MemoryStoredEvent(Session, Turn, id, MemoryKind.Fact, true));
        stored.Kind.Should().Be("memory-stored");
        stored.Memory.Should().BeEquivalentTo(new { MemoryId = id.ToString(), Kind = "fact", Deduped = true });

        var failed = AgentDtoMapper.ToDto(new MemoryRecallFailedEvent(Session, Turn, AgentErrorCode.MemoryIndexUnavailable));
        failed.Kind.Should().Be("memory-recall-failed");
        failed.Memory!.Code.Should().Be("MemoryIndexUnavailable");

        var pending = AgentDtoMapper.ToDto(new MemoryIndexPendingEvent(Session, Turn, id));
        pending.Kind.Should().Be("memory-index-pending");
        pending.Memory!.MemoryId.Should().Be(id.ToString());
    }

    [Fact]
    public void ToDto_passes_unknown_event_kinds_through_instead_of_killing_the_stream()
    {
        var dto = AgentDtoMapper.ToDto(new UnknownEvent(Session, Turn));
        dto.Kind.Should().Be("unknown-test-event");
    }

    private sealed record UnknownEvent(SessionId SessionId, TurnId TurnId) : AgentEvent(SessionId, TurnId)
    {
        public override string Kind => "unknown-test-event";
    }
```
(If `AgentEvent.Kind` is not overridable — `KindOf(Type)` suggests a static map — drop the second test and keep the pass-through arm.)

**Step 3: implement.** `AgentDtos.cs` — add
```csharp
/// <summary>Memory payload of the <c>memory-*</c> SSE kinds; only the members relevant to the kind are set.</summary>
public sealed record MemoryEventDto(
    int? Count = null,
    IReadOnlyList<string>? Ids = null,
    int? Chars = null,
    string? MemoryId = null,
    string? Kind = null,
    bool? Deduped = null,
    string? Code = null);
```
and extend `AgentEventDto` with a trailing optional `MemoryEventDto? Memory = null` (update its doc comment: kinds now include `memory-recalled | memory-stored | memory-recall-failed | memory-index-pending`).

`AgentDtoMapper.ToDto(AgentEvent)` — add `using Thalos.Memory;` and the arms before the default, and make the default a pass-through:
```csharp
            MemoryRecalledEvent r => new AgentEventDto(r.Kind, Memory: new MemoryEventDto(Count: r.Count, Ids: r.Ids.Select(i => i.ToString()).ToList(), Chars: r.Chars)),
            MemoryStoredEvent s => new AgentEventDto(s.Kind, Memory: new MemoryEventDto(MemoryId: s.Id.ToString(), Kind: s.Kind.Value, Deduped: s.Deduped)),
            MemoryRecallFailedEvent f => new AgentEventDto(f.Kind, Memory: new MemoryEventDto(Code: f.Code.ToString())),
            MemoryIndexPendingEvent p => new AgentEventDto(p.Kind, Memory: new MemoryEventDto(MemoryId: p.Id.ToString())),
            // Forward-compatible: a Thalos event kind this adapter does not know yet still reaches the client by name.
            _ => new AgentEventDto(agentEvent.Kind),
```
`ApiJsonSerializerContext.cs` — add `[JsonSerializable(typeof(MemoryEventDto))]`.

**Step 4:** unit tests green. Commit `feat(memory): map Thalos memory events to SSE DTOs; unknown kinds pass through`.

---

## Task 6: Options + wiring in `AddDaedalusAgents` / `AddDaedalusMemory`, appsettings, registration tests (G1)

**Files:** modify `src/Daedalus.Agents/DaedalusAgentsOptions.cs`, `DaedalusAgentsServiceCollectionExtensions.cs`, `src/Daedalus.Api/appsettings.json`; test `tests/Daedalus.Tests.Unit.Application/Agents/DaedalusAgentsRegistrationTests.cs`.

**Step 1: failing tests** (append; add `using Thalos.Memory;`, `using Daedalus.Agents.Memory;`)
```csharp
    [Fact]
    public void Memory_is_wired_with_the_postgres_store_and_the_ragnet_index()
    {
        using var sp = Build(Config(("ConnectionStrings:daedalus", "Host=localhost;Database=x;Username=u;Password=p")));

        sp.GetRequiredService<IMemoryService>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryStore>().GetType().Name.Should().BeOneOf("PostgresMemoryStore", "MemoryStoreInstrumented");
        sp.GetRequiredService<PostgresMemoryStore>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryIndex>().GetType().FullName.Should().Contain("RagNet");
        sp.GetRequiredService<Daedalus.Application.Abstractions.ILearningsMemory>().Should().BeOfType<ThalosLearningsMemory>();
    }

    [Fact]
    public void Memory_config_binds_from_Thalos_Memory_with_daedalus_defaults()
    {
        using var sp = Build(Config(("Thalos:Memory:Recall:TopK", "7"), ("Thalos:Memory:VectorDimensions", "512"), ("Thalos:Memory:Reindex:RetryInterval", "00:00:30")));

        var config = sp.GetRequiredService<MemoryConfig>();
        config.SharedOwnerId.Should().Be("daedalus");
        config.VectorDimensions.Should().Be(512);
        config.Reindex.RetryInterval.Should().Be(TimeSpan.FromSeconds(30));
        config.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Agent_memory_settings_bind_onto_the_definition()
    {
        using var sp = Build(Config(("Thalos:Agents:0:Memory:Enabled", "false"), ("Thalos:Agents:0:Memory:TopK", "3")));

        var agent = sp.GetRequiredService<IAgentCatalog>().Agents.Single();
        agent.Memory.Should().BeEquivalentTo(new { Enabled = (bool?)false, TopK = (int?)3 });
    }

    [Fact]
    public void AddDaedalusMemory_registers_memory_for_the_ralph_console_host_without_agents()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusMemory(Config());
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IMemoryService>().Should().NotBeNull();
        sp.GetRequiredService<Daedalus.Application.Abstractions.ILearningsMemory>().Should().BeOfType<ThalosLearningsMemory>();
        sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().NotContain(h => h.GetType().Name == "ReindexPendingMemoriesHostedService");
    }
```
(`ILearningsMemory`/`ThalosLearningsMemory` arrive in Tasks 7–8 — write these two assertions now, they turn green after Task 8; run the rest.)

**Step 3: implement.** `DaedalusAgentsOptions.cs` — add to `DaedalusAgentsOptions`: `/// <summary>Memory settings (<c>Thalos:Memory</c>): Thalos <c>MemoryOptions</c> keys plus Daedalus extras.</summary> public MemoryConfig Memory { get; } = new();`; add to `AgentConfig`: `public AgentMemoryConfig? Memory { get; set; }`; add classes:
```csharp
/// <summary>Per-agent memory overrides (<c>Thalos:Agents:N:Memory</c>); null members inherit <c>Thalos:Memory</c>.</summary>
public sealed class AgentMemoryConfig
{
    public bool? Enabled { get; set; }
    public int? TopK { get; set; }
    public bool? ExposeTools { get; set; }
}

/// <summary>
///     <c>Thalos:Memory</c>. The Thalos <c>MemoryOptions</c> members (<see cref="Enabled"/>, <see cref="SharedOwnerId"/>,
///     <c>Recall</c>, <c>Dedupe</c>, <c>ExposeTools</c>) are bound onto Thalos directly from the same section; this class
///     carries the Daedalus-only keys and the shared-owner default.
/// </summary>
public sealed class MemoryConfig
{
    public const string SectionName = "Thalos:Memory";
    public bool Enabled { get; set; } = true;
    /// <summary>Owner of host-written project knowledge (Ralph learnings). Recalled for every caller, writable only by host code.</summary>
    public string SharedOwnerId { get; set; } = "daedalus";
    /// <summary>Dimensions of the embedding model (nomic-embed-text = 768). Must match <c>rag_chunks</c>.</summary>
    public int VectorDimensions { get; set; } = 768;
    public RalphRecallConfig RalphRecall { get; } = new();
    public ReindexConfig Reindex { get; } = new();
}

/// <summary>How the Ralph enrichment/MCP paths recall shared learnings.</summary>
public sealed class RalphRecallConfig
{
    public int TopK { get; set; } = 10;
    public double MinScore { get; set; } = 0.5;
}

/// <summary><c>ReindexPendingMemoriesHostedService</c> settings.</summary>
public sealed class ReindexConfig
{
    public bool Enabled { get; set; } = true;
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Wait between attempts while the index is unavailable or rows failed.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Wait between sweeps once everything is indexed.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(15);
}
```
`DaedalusAgentsServiceCollectionExtensions.cs`:
- new usings: `Daedalus.Agents.Memory`, `Daedalus.Application.Abstractions`, `Daedalus.Infrastructure.Persistence` (for `DatabaseSettings`), `Thalos.Memory`, `Thalos.Memory.RagNet`.
- constant `public const string DatabaseConnectionName = "daedalus";`
- in `AddDaedalusAgents` after binding: `var connectionString = configuration.GetConnectionString(DatabaseConnectionName) ?? DatabaseSettings.GetDefaultConnectionString();` `services.AddSingleton(options.Memory);` `services.AddSingleton<ILearningsMemory, ThalosLearningsMemory>();` (Task 8 adds the type — add this line then). Replace `services.AddScoped<DaedalusLearningsTools>();` by keeping only `services.AddScoped<DaedalusFailurePatternsTools>();` in Task 9. Register the reindex hosted service (Task 13) here: `if (options.Memory.Enabled && options.Memory.Reindex.Enabled) services.AddHostedService<ReindexPendingMemoriesHostedService>();`
- inside the `AddThalos` lambda, first line: `ConfigureMemory(thalos, configuration.GetSection(MemoryConfig.SectionName), options.Memory, connectionString);`
- `ToDefinition`: `Memory = agent.Memory is null ? null : new AgentMemorySettings { Enabled = agent.Memory.Enabled, TopK = agent.Memory.TopK, ExposeTools = agent.Memory.ExposeTools },`
- new public method + private helper:
```csharp
    /// <summary>
    ///     Memory-only registration for hosts that run Ralph but no Thalos agents (the console worker): the same
    ///     <c>IMemoryService</c>, Postgres store and Rag.NET index as <see cref="AddDaedalusAgents"/>, plus the Ralph port
    ///     <see cref="ILearningsMemory"/>. No agents, tools, Sentinel or reindex service (the API host runs that one).
    /// </summary>
    public static IServiceCollection AddDaedalusMemory(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName) ?? DatabaseSettings.GetDefaultConnectionString();

        services.AddSingleton(options.Memory);
        services.AddSingleton<ILearningsMemory, ThalosLearningsMemory>();
        services.AddThalos(thalos => ConfigureMemory(thalos, configuration.GetSection(MemoryConfig.SectionName), options.Memory, connectionString));
        return services;
    }

    private static void ConfigureMemory(ThalosBuilder thalos, IConfigurationSection section, MemoryConfig config, string connectionString)
    {
        thalos.UseMemory(o =>
            {
                section.Bind(o);                       // Enabled, SharedOwnerId, Recall, Dedupe, ExposeTools straight from Thalos:Memory
                o.Enabled = config.Enabled;
                o.SharedOwnerId ??= config.SharedOwnerId;
            })
            .UseMemoryStore<PostgresMemoryStore>()
            .UseRagNetMemory(o =>
            {
                o.ConnectionString = connectionString;  // same database as the app; Rag.NET keeps its own pool (documented sharp edge)
                o.VectorDimensions = config.VectorDimensions;
                o.EnsureSchemaOnStartup = true;
            });
    }
```
> `UseRagNetMemory` embeds with the host's `IEmbeddingGenerator<string, Embedding<float>>` from DI (Api registers the Ollama client before `AddDaedalusAgents`; Console does the same in Task 10). Without it the index probe reports unavailable: remember → `index_pending`, recall → nothing, events explain, reindex retries.

`appsettings.json` — inside `"Thalos"` add:
```json
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
```
(Agent `Tools`/`Instructions` change in Task 9.)

`Program.cs` (Api): no change besides the existing call (the Ollama singleton is already registered before `AddDaedalusAgents`). Update the comment above it: "…the DbContext factory, the Ollama embedding generator (memory index + Sentinel)…".

**Step 4:** build; registration tests (except the two port assertions) green; `dotnet run --project src/Daedalus.AppHost` briefly → API log shows the Rag.NET schema hosted service creating `rag_chunks` without error (or the "dimension mismatch" fail-fast message if a stale table exists — then `DROP TABLE rag_chunks;` and restart). Commit `feat(memory): wire Thalos memory (Postgres store, Rag.NET index) into AddDaedalusAgents; AddDaedalusMemory`.

---

## Task 7: Application port + `LearningsService` on the port (G3)

**Files:** create `src/Daedalus.Application/Abstractions/ILearningsMemory.cs`, `src/Daedalus.Application/Services/LearningMemoryMapping.cs`; modify `src/Daedalus.Application/Services/LearningsService.cs`, `src/Daedalus.Application/Abstractions/ILearningsService.cs` (doc only), `tests/Daedalus.Tests.Unit.Application/Services/LearningsServiceParsingTests.cs`; create `tests/Daedalus.Tests.Unit.Application/Services/LearningsServicePersistenceTests.cs`, `tests/Daedalus.Tests.Unit.Application/Services/LearningMemoryMappingTests.cs`.

**Step 1: failing tests**

`LearningMemoryMappingTests.cs`
```csharp
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Application.Services;

public sealed class LearningMemoryMappingTests
{
    [Fact]
    public void Text_joins_pattern_and_resolution_unless_identical()
    {
        LearningMemoryMapping.Text("CS1061 missing member", "Add using ZLinq").Should().Be("CS1061 missing member\nAdd using ZLinq");
        LearningMemoryMapping.Text("same", "same").Should().Be("same");
    }

    [Fact]
    public void Tags_are_category_severity_then_at_most_eight_lowercased_tags()
    {
        var tags = LearningMemoryMapping.Tags(LearningCategory.ErrorPattern, LearningSeverity.High, ["EF Core", "postgresql", "a", "b", "c", "d", "e", "f", "g", "h"]);
        tags.Should().HaveCount(10);
        tags.Take(2).Should().Equal("errorpattern", "high");
        tags[2].Should().Be("ef core");
    }

    [Theory]
    [InlineData(LearningSeverity.Critical, 1.0)]
    [InlineData(LearningSeverity.High, 0.8)]
    [InlineData(LearningSeverity.Medium, 0.5)]
    [InlineData(LearningSeverity.Low, 0.3)]
    public void Importance_follows_severity(LearningSeverity severity, double expected) =>
        LearningMemoryMapping.Importance(severity).Should().Be(expected);

    [Fact]
    public void Source_names_the_task() =>
        LearningMemoryMapping.Source(Guid.Parse("11111111-2222-3333-4444-555555555555")).Should().Be("ralph:task/11111111-2222-3333-4444-555555555555");
}
```
`LearningsServicePersistenceTests.cs`
```csharp
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Application.Services;

public sealed class LearningsServicePersistenceTests
{
    private readonly ILearningsMemory _memory = Substitute.For<ILearningsMemory>();
    private readonly IFailurePatternDatabase _failures = Substitute.For<IFailurePatternDatabase>();

    private LearningsService Sut() => new(_memory, _failures, NullLogger<LearningsService>.Instance);

    [Fact]
    public async Task ParseAndPersist_remembers_every_parsed_entry_under_the_task_source()
    {
        var taskId = Guid.NewGuid();
        _memory.RememberAsync(Arg.Any<ParsedLearning>(), taskId, Arg.Any<CancellationToken>()).Returns(Result.Success("id"));

        var result = await Sut().ParseAndPersistLearningsAsync("⚠ 3 errors encountered:\n  - CS1061: Type does not contain definition\n✓ Success achieved at iteration 5", taskId, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);
        await _memory.Received(2).RememberAsync(Arg.Any<ParsedLearning>(), taskId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ParseAndPersist_counts_only_successful_remembers_and_never_throws()
    {
        var taskId = Guid.NewGuid();
        _memory.RememberAsync(Arg.Any<ParsedLearning>(), taskId, Arg.Any<CancellationToken>()).Returns(Result.Failure<string>("index down"));

        var result = await Sut().ParseAndPersistLearningsAsync("Error: Missing using directive for ZLinq\n- Use AsValueEnumerable() from ZLinq namespace", taskId, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
    }

    [Fact]
    public async Task Enrichment_recalls_learnings_for_the_prompt_and_appends_failure_patterns()
    {
        _memory.RecallAsync("implement the EF migration", 10, Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<RecalledLearning>>(
        [
            new RecalledLearning("m1", "Migration needs pgvector\nUse pgvector/pgvector:pg16", ["errorpattern", "high", "migration"], 0.91, DateTimeOffset.UtcNow),
        ]));
        _failures.SearchByPromptContextAsync("implement the EF migration", 5, Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<FailurePatternRecord>>(
        [
            new FailurePatternRecord("NU1903 vulnerable", "bump the package", Guid.NewGuid(), 1, 2, DateTime.UtcNow),
        ]));

        var result = await Sut().GetEnrichmentContextAsync("implement the EF migration", null, Guid.NewGuid(), 10, 5, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("=== CROSS-TASK LEARNINGS ===").And.Contain("Migration needs pgvector").And.Contain("[errorpattern, high, migration]");
        result.Value.Should().Contain("=== KNOWN FAILURE PATTERNS ===").And.Contain("NU1903 vulnerable");
    }

    [Fact]
    public async Task Enrichment_is_empty_when_nothing_is_recalled_and_no_patterns_match()
    {
        _memory.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<IReadOnlyList<RecalledLearning>>("unavailable"));
        _failures.SearchByPromptContextAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<FailurePatternRecord>>([]));

        var result = await Sut().GetEnrichmentContextAsync("x", null, Guid.NewGuid(), 10, 5, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
```
(Check `FailurePatternRecord`'s constructor in `Daedalus.Application.Abstractions` and adapt the positional args.)

`LearningsServiceParsingTests.cs`: change `ParseRawLearnings(raw, sourceTaskId, null)` calls to `ParseRawLearnings(raw)`; replace `ParseRawLearnings_ShouldSetSourceTaskId` with `ParseRawLearnings_ShouldKeepCategorySeverityAndTags` asserting `entries.Should().OnlyContain(e => e.Tags != null)` and one entry's `Severity` (e.g. `"build failed: fatal"` → `LearningSeverity.Critical`). Everything else stays.

**Step 3: implement.**

`ILearningsMemory.cs`
```csharp
using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>One parsed learning (output of <c>LearningsService.ParseRawLearnings</c>), not yet persisted.</summary>
public sealed record ParsedLearning(LearningCategory Category, string Pattern, string Resolution, IReadOnlyList<string> Tags, LearningSeverity Severity);

/// <summary>A learning recalled from memory for a prompt; <paramref name="Score"/> is cosine similarity in [0,1].</summary>
public sealed record RecalledLearning(string Id, string Text, IReadOnlyList<string> Tags, double Score, DateTimeOffset CreatedAt);

/// <summary>
///     Ralph's door to the agent memory (Thalos <c>IMemoryService</c> behind the adapter in <c>Daedalus.Agents</c>). Learnings
///     are written under the shared owner and recalled by semantic search. Application stays Thalos-free.
/// </summary>
public interface ILearningsMemory
{
    /// <summary>Stores one learning under the shared owner (kind <c>learning</c>, source <c>ralph:task/{sourceTaskId}</c>); returns the memory id.</summary>
    Task<Result<string>> RememberAsync(ParsedLearning learning, Guid sourceTaskId, CancellationToken ct);

    /// <summary>Recalls up to <paramref name="maxResults"/> shared learnings relevant to <paramref name="query"/>, best first.</summary>
    Task<Result<IReadOnlyList<RecalledLearning>>> RecallAsync(string query, int maxResults, CancellationToken ct);
}
```
`LearningMemoryMapping.cs`
```csharp
using System.Globalization;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Services;

/// <summary>
///     How a Ralph learning becomes a memory record. Mirrors the SQL in the <c>AddAgentMemories</c> migration (text, tags,
///     importance, source) — change both together.
/// </summary>
public static class LearningMemoryMapping
{
    public const int MaxFreeTags = 8; // + category + severity = the 10-tag limit

    public static string Text(string pattern, string resolution) =>
        string.Equals(pattern, resolution, StringComparison.Ordinal) ? pattern : $"{pattern}\n{resolution}";

    public static IReadOnlyList<string> Tags(LearningCategory category, LearningSeverity severity, IEnumerable<string> tags)
    {
        var list = new List<string> { category.ToString().ToLowerInvariant(), severity.ToString().ToLowerInvariant() };
        list.AddRange(tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(MaxFreeTags));
        return list;
    }

    public static double Importance(LearningSeverity severity) => severity switch
    {
        LearningSeverity.Critical => 1.0,
        LearningSeverity.High => 0.8,
        LearningSeverity.Medium => 0.5,
        _ => 0.3,
    };

    public static string Source(Guid sourceTaskId) => string.Create(CultureInfo.InvariantCulture, $"ralph:task/{sourceTaskId}");
}
```
`LearningsService.cs` — constructor `(ILearningsMemory memory, IFailurePatternDatabase failurePatternDatabase, ILogger<LearningsService> logger)`; class doc: "…persists parsed entries as shared agent memories (`ILearningsMemory`) and builds enrichment from semantic recall…". Replace the two public methods and the parser signature (everything else unchanged):
```csharp
    public async Task<Result<int>> ParseAndPersistLearningsAsync(string rawLearnings, Guid sourceTaskId, Guid? projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawLearnings))
        {
            return Result.Success(0);
        }

        var entries = ParseRawLearnings(rawLearnings);
        var persistedCount = 0;
        foreach (var entry in entries)
        {
            var remembered = await memory.RememberAsync(entry, sourceTaskId, ct);
            if (remembered.IsSuccess)
            {
                persistedCount++;
            }
            else
            {
                LogFailedPersistLearning(logger, entry.Pattern, remembered.Error);
            }
        }

        LogLearningsParsed(logger, persistedCount, entries.Count, sourceTaskId);
        return Result.Success(persistedCount);
    }

    public async Task<Result<string>> GetEnrichmentContextAsync(string taskPrompt, Guid? projectId, Guid currentTaskId, int maxLearnings, int maxFailurePatterns, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var hasContent = false;

        // 1. Shared learnings recalled by semantic similarity to the task prompt (the memory index does the ranking).
        var recalled = await memory.RecallAsync(taskPrompt, maxLearnings, ct);
        if (recalled.IsSuccess && recalled.Value.Count > 0)
        {
            sb.AppendLine("=== CROSS-TASK LEARNINGS ===");
            sb.AppendLine("Knowledge recalled from previous task executions (most relevant first):");
            sb.AppendLine();
            foreach (var learning in recalled.Value)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  - [{string.Join(", ", learning.Tags)}] {learning.Text.Replace("\n", " → ", StringComparison.Ordinal)}");
                sb.AppendLine();
            }

            sb.AppendLine();
            hasContent = true;
        }
        else if (recalled.IsFailure)
        {
            LogRecallFailed(logger, recalled.Error);
        }

        // 2. Known failure patterns (TaskExecutions-based, unchanged).
        var failureResult = await failurePatternDatabase.SearchByPromptContextAsync(taskPrompt, maxFailurePatterns, ct);
        if (failureResult.IsSuccess && failureResult.Value.Count > 0)
        {
            sb.AppendLine("=== KNOWN FAILURE PATTERNS ===");
            sb.AppendLine("Error→Fix pairs from past task executions:");
            sb.AppendLine();
            foreach (var pattern in failureResult.Value)
            {
                var errorSnippet = pattern.ErrorText.Length > 200 ? pattern.ErrorText[..200] + "..." : pattern.ErrorText;
                var resolutionSnippet = pattern.Resolution.Length > 300 ? pattern.Resolution[..300] + "..." : pattern.Resolution;
                sb.Append(CultureInfo.InvariantCulture, $"  ⚠ Error: {errorSnippet}");
                sb.AppendLine();
                sb.Append(CultureInfo.InvariantCulture, $"    Fix: {resolutionSnippet}");
                sb.AppendLine();
                sb.AppendLine();
            }

            hasContent = true;
        }

        return Result.Success(hasContent ? sb.ToString() : string.Empty);
    }

    internal static List<ParsedLearning> ParseRawLearnings(string rawLearnings)
    {
        // … identical loop; replace the StructuredLearningEntry.Create(...) block with:
        //   if (pattern.Length < 5) continue;
        //   entries.Add(new ParsedLearning(category, pattern, resolution, tags, severity));
    }
```
Add `[LoggerMessage(EventId = 103, Level = LogLevel.Debug, Message = "Learnings recall failed: {Error}")] private static partial void LogRecallFailed(ILogger logger, string error);`, delete `LogEmbeddingGenerationFailed`. Remove `using Daedalus.Domain.Entities;` only if no longer needed (the enums are still used → keep). `ILearningsService` doc: replace "no embeddings" wording with "persists to the agent memory".

**Step 4:** unit tests: `Daedalus.Tests.Unit.Application` builds only once `LearningsService`'s DI still resolves — `ApplicationServiceExtensions` registers `LearningsService` by type, fine. Run → the three new/changed test classes green; `LearningsEnrichmentMiddlewareTests` untouched. Commit `refactor(learnings): persist and recall Ralph learnings through the ILearningsMemory port`.

---

## Task 8: `ThalosLearningsMemory` adapter (G3)

**Files:** create `src/Daedalus.Agents/Memory/ThalosLearningsMemory.cs`; test `tests/Daedalus.Tests.Unit.Application/Agents/ThalosLearningsMemoryTests.cs`; modify `DaedalusAgentsServiceCollectionExtensions.cs` (add the two `AddSingleton<ILearningsMemory, ThalosLearningsMemory>()` lines from Task 6).

**Step 1: failing tests**
```csharp
using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;
using Thalos.Memory;

namespace Daedalus.Tests.Unit.Application.Agents;

public sealed class ThalosLearningsMemoryTests
{
    private readonly IMemoryService _service = Substitute.For<IMemoryService>();
    private readonly MemoryConfig _config = new() { SharedOwnerId = "daedalus" };

    private ThalosLearningsMemory Sut() => new(_service, _config, NullLogger<ThalosLearningsMemory>.Instance);

    private static MemoryRecord Record(string text, params string[] tags) => new()
    {
        Id = MemoryId.New(), OwnerId = "daedalus", Kind = MemoryKind.Learning, Text = text, Tags = tags, Source = "ralph:task/x",
        Importance = 0.8, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Remember_writes_under_the_shared_owner_as_a_learning()
    {
        var taskId = Guid.NewGuid();
        RememberRequest? seen = null;
        _service.RememberAsync(Arg.Do<RememberRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryRecord, AgentError>.Success(Record("x")));

        var result = await Sut().RememberAsync(new ParsedLearning(LearningCategory.ErrorPattern, "CS1061", "add using", ["ef core"], LearningSeverity.High), taskId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        seen.Should().NotBeNull();
        seen!.OwnerId.Should().Be("daedalus");
        seen.AgentId.Should().BeNull();
        seen.Kind.Should().Be(MemoryKind.Learning);
        seen.Text.Should().Be("CS1061\nadd using");
        seen.Tags.Should().Equal("errorpattern", "high", "ef core");
        seen.Importance.Should().Be(0.8);
        seen.Source.Should().Be($"ralph:task/{taskId}");
    }

    [Fact]
    public async Task Remember_maps_thalos_errors_to_a_failure_without_throwing()
    {
        _service.RememberAsync(Arg.Any<RememberRequest>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryStoreFailed("down")));

        var result = await Sut().RememberAsync(new ParsedLearning(LearningCategory.CodeConvention, "p", "r", [], LearningSeverity.Low), Guid.NewGuid(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("MemoryStoreFailed");
    }

    [Fact]
    public async Task Recall_queries_the_shared_scope_and_projects_hits()
    {
        MemoryScope scope = default;
        RecallOptions? options = null;
        _service.RecallAsync("npgsql timeout", Arg.Do<MemoryScope>(s => scope = s), Arg.Do<RecallOptions>(o => options = o), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<IReadOnlyList<RecalledMemory>, AgentError>.Success([new RecalledMemory(Record("Timeouts: raise CommandTimeout", "errorpattern"), 0.87)]));

        var result = await Sut().RecallAsync("npgsql timeout", 3, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Text = "Timeouts: raise CommandTimeout", Score = 0.87 });
        scope.OwnerId.Should().Be("daedalus");
        scope.AgentId.Should().BeNull();
        options!.TopK.Should().Be(3);
        options.MinScore.Should().Be(_config.RalphRecall.MinScore);
    }
}
```
**Step 3: implement**
```csharp
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Microsoft.Extensions.Logging;
using Thalos.Memory;

namespace Daedalus.Agents.Memory;

/// <summary>
///     <see cref="ILearningsMemory"/> over Thalos' <see cref="IMemoryService"/>: Ralph learnings are host-written project
///     knowledge, so they live under the shared owner (<see cref="MemoryConfig.SharedOwnerId"/>), agent-less, kind
///     <c>learning</c>. ZeroAlloc results are converted to CSFE at this boundary (Application convention).
/// </summary>
public sealed partial class ThalosLearningsMemory(IMemoryService memory, MemoryConfig config, ILogger<ThalosLearningsMemory> logger) : ILearningsMemory
{
    /// <inheritdoc />
    public async Task<Result<string>> RememberAsync(ParsedLearning learning, Guid sourceTaskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(learning);
        var request = new RememberRequest
        {
            OwnerId = config.SharedOwnerId,
            AgentId = null,
            Kind = MemoryKind.Learning,
            Text = LearningMemoryMapping.Text(learning.Pattern, learning.Resolution),
            Tags = LearningMemoryMapping.Tags(learning.Category, learning.Severity, learning.Tags),
            Source = LearningMemoryMapping.Source(sourceTaskId),
            Importance = LearningMemoryMapping.Importance(learning.Severity),
        };

        var result = await memory.RememberAsync(request, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            LogRememberFailed(logger, result.Error.Code.ToString(), result.Error.Message);
            return Result.Failure<string>($"{result.Error.Code}: {result.Error.Message}");
        }

        return Result.Success(result.Value.Id.ToString());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RecalledLearning>>> RecallAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Result.Success<IReadOnlyList<RecalledLearning>>([]);
        }

        var scope = new MemoryScope(config.SharedOwnerId, null, null);
        var options = new RecallOptions { TopK = Math.Clamp(maxResults, 1, 50), MinScore = config.RalphRecall.MinScore, MaxChars = int.MaxValue };
        var result = await memory.RecallAsync(query, scope, options, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            LogRecallFailed(logger, result.Error.Code.ToString(), result.Error.Message);
            return Result.Failure<IReadOnlyList<RecalledLearning>>($"{result.Error.Code}: {result.Error.Message}");
        }

        IReadOnlyList<RecalledLearning> learnings = result.Value
            .Select(h => new RecalledLearning(h.Record.Id.ToString(), h.Record.Text, h.Record.Tags, h.Score, h.Record.CreatedAt))
            .ToList();
        return Result.Success(learnings);
    }

    [LoggerMessage(EventId = 500, Level = LogLevel.Warning, Message = "Remembering a Ralph learning failed: {Code} {Message}")]
    private static partial void LogRememberFailed(ILogger logger, string code, string message);

    [LoggerMessage(EventId = 501, Level = LogLevel.Debug, Message = "Recalling Ralph learnings failed: {Code} {Message}")]
    private static partial void LogRecallFailed(ILogger logger, string code, string message);
}
```
Register in both `AddDaedalusAgents` and `AddDaedalusMemory` (`services.AddSingleton<ILearningsMemory, ThalosLearningsMemory>();` — `IMemoryService` is a Thalos singleton; if it turns out scoped, register the adapter scoped).

**Step 4:** unit tests green incl. Task 6's two pending assertions. Commit `feat(memory): ThalosLearningsMemory adapter (Ralph learnings under the shared owner)`.

---

## Task 9: MCP tool, tool status, middleware, knowledge tools, appsettings (G3)

**Files:** rewrite `src/Daedalus.Infrastructure/Agents/Tools/DaedalusLearningsTools.cs`, `src/Daedalus.Infrastructure/Services/KnowledgeBaseToolStatus.cs`, `src/Daedalus.Application/Abstractions/IKnowledgeBaseToolStatus.cs`, `src/Daedalus.Agents/Tools/DaedalusKnowledgeTools.cs`; modify `src/Daedalus.Application/Services/Middleware/LearningsEnrichmentMiddleware.cs`, `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs`, `src/Daedalus.Api/appsettings.json`; tests: rewrite `tests/Daedalus.Tests.Unit.Application/Agents/DaedalusKnowledgeToolsTests.cs`, create `tests/Daedalus.Tests.Unit.Infrastructure/Agents/DaedalusLearningsToolsTests.cs`, touch `LearningsEnrichmentMiddlewareTests.cs` if it asserts the summary text.

**Step 1: failing tests**

`DaedalusLearningsToolsTests.cs` (Unit.Infrastructure; add `Daedalus.Application` ref if missing — it has Infrastructure which references Application transitively):
```csharp
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Infrastructure.Agents;

public sealed class DaedalusLearningsToolsTests
{
    private readonly ILearningsMemory _memory = Substitute.For<ILearningsMemory>();

    private DaedalusLearningsTools Sut() => new(_memory, NullLogger<DaedalusLearningsTools>.Instance);

    [Fact]
    public async Task SearchLearnings_recalls_and_formats_json()
    {
        _memory.RecallAsync("npgsql timeout", 5, Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<RecalledLearning>>(
            [new RecalledLearning("m1", "Timeouts\nRaise CommandTimeout", ["errorpattern", "high"], 0.9, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero))]));

        var json = await Sut().SearchLearnings("npgsql timeout");

        json.Should().Contain("\"text\": \"Timeouts").And.Contain("\"score\": 0.9").And.Contain("errorpattern").And.Contain("2026-08-17");
    }

    [Fact]
    public async Task SearchLearnings_reports_no_matches_and_never_throws()
    {
        _memory.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<RecalledLearning>>([]));
        (await Sut().SearchLearnings("x", 3)).Should().Be("No matching learnings found.");

        _memory.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<IReadOnlyList<RecalledLearning>>("MemoryIndexUnavailable: down"));
        (await Sut().SearchLearnings("x", 3)).Should().StartWith("Learnings memory unavailable");
    }
}
```
`DaedalusKnowledgeToolsTests.cs` — rewrite: only `IFailurePatternDatabase` substitute; `CreateSut() => new(new DaedalusFailurePatternsTools(_failures, NullLogger<…>.Instance))`; keep `SearchFailurePatterns_delegates…`; the `LocalToolSource` test now asserts `tools.Value.Select(t => t.Name).Should().BeEquivalentTo(["search_failure_patterns"])`; delete the two `SearchLearnings` tests.

**Step 3: implement.**

`DaedalusLearningsTools.cs`
```csharp
#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Daedalus.Infrastructure.Agents.Tools;

/// <summary>MCP tool for the Ralph Loop: semantic recall of shared learnings from the agent memory (<see cref="ILearningsMemory"/>).</summary>
[McpServerToolType]
public sealed partial class DaedalusLearningsTools(ILearningsMemory memory, ILogger<DaedalusLearningsTools> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "search_learnings", ReadOnly = true, Idempotent = true)]
    [Description(
        "Search past learnings from previous task executions using semantic similarity. " +
        "Use this when you encounter errors, need context about the codebase, or want to " +
        "learn from previous approaches that worked or failed.")]
    public async Task<string> SearchLearnings(
        [Description("Natural language description of what you're looking for")] string query,
        [Description("Maximum number of results (default: 5)")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var recalled = await memory.RecallAsync(query, Math.Clamp(maxResults, 1, 20), cancellationToken).ConfigureAwait(false);
        if (recalled.IsFailure)
        {
            LogRecallFailed(logger, query, recalled.Error);
            return $"Learnings memory unavailable ({recalled.Error}). Proceed with available context.";
        }

        if (recalled.Value.Count == 0)
        {
            return "No matching learnings found.";
        }

        LogRecalled(logger, query, recalled.Value.Count);
        var results = recalled.Value.Select(l => new
        {
            id = l.Id,
            text = l.Text,
            tags = l.Tags,
            score = Math.Round(l.Score, 3),
            createdAt = l.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
        return JsonSerializer.Serialize(results, SerializerOptions);
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Debug, Message = "Recalled {Count} learnings for query '{Query}'")]
    private static partial void LogRecalled(ILogger logger, string query, int count);

    [LoggerMessage(EventId = 402, Level = LogLevel.Warning, Message = "Recalling learnings for query '{Query}' failed: {Error}")]
    private static partial void LogRecallFailed(ILogger logger, string query, string error);
}
```
`IKnowledgeBaseToolStatus.cs` — remove `LearningsCount` (doc: "learnings are recalled from the agent memory; only failure patterns are counted"). `KnowledgeBaseToolStatus.cs` — delete the `_learningsCount` field and `LearningsCount` property (ctor unchanged; `dbContext` still used for failure patterns).

`LearningsEnrichmentMiddleware.cs` — slim summary:
```csharp
                var summary = "=== KNOWLEDGE BASE ===\n" +
                    "You have access to a semantic memory of learnings from previous tasks " +
                    $"and {toolStatus.FailurePatternsCount} known failure patterns.\n" +
                    "Use the search_learnings tool to recall relevant past knowledge.\n" +
                    "Use the search_failure_patterns tool when you encounter errors.\n";
                context.PromptContext.AccumulatedLearnings = summary;
                LogSlimEnrichment(logger, context.Iteration);
```
and `LogSlimEnrichment` loses the count parameter (`Message = "Slim enrichment mode for iteration {Iteration}: knowledge base tools available"`).

`DaedalusKnowledgeTools.cs` — drop the `search_learnings` member and the `learnings` ctor parameter; class doc: "`daedalus__search_failure_patterns` only — learnings are recalled automatically and through `memory__*` tools". Composition root: remove `services.AddScoped<DaedalusLearningsTools>();` (Ralph's `McpToolBuilder` discovers `[McpServerToolType]` classes itself and needs `ILearningsMemory` — provided by `AddDaedalusMemory`/`AddDaedalusAgents`; keep `services.AddScoped<DaedalusFailurePatternsTools>();`).

`appsettings.json` agent:
```json
        "Instructions": "You are a senior .NET architect embedded in the Daedalus project. Use roslyn__* tools to inspect the solution, memory__recall/memory__list to look up remembered facts and past learnings, memory__remember to keep durable, non-obvious facts you learn (never secrets), and daedalus__search_failure_patterns for known error→fix pairs. Relevant memories are also injected automatically before each turn. Cite symbols and files. If a tool fails, say so plainly.",
        "Tools": [ "roslyn__*", "daedalus__*", "memory__*", "context7__*" ]
```
Also `README`'s copy of this block (Task 19).

**Step 4:** build (Ralph console still compiles: `DaedalusLearningsTools` needs `ILearningsMemory`, registered in Task 10 for the console; DI resolution is at runtime), unit tests green. Commit `refactor(learnings): MCP search_learnings and Thalos knowledge tools on the memory port; memory__* for agents`.

---

## Task 10: Console (Ralph worker) host + AppHost (G3)

**Files:** modify `src/Daedalus.Console/Daedalus.Console.csproj`, `src/Daedalus.Console/Program.cs`, `src/Daedalus.AppHost/Program.cs`.

- csproj: add `<ProjectReference Include="..\Daedalus.Agents\Daedalus.Agents.csproj" />` and `<PackageReference Include="OllamaSharp" />`.
- `Program.cs` inside `ConfigureServices`, after `AddAgentFrameworkServices`:
```csharp
            // Ollama embedding generator (memory index). Aspire provides ConnectionStrings:ollama; without it memories are stored
            // index_pending and the API host's reindex service embeds them once Ollama is up.
            var ollamaConnectionString = context.Configuration.GetConnectionString("ollama");
            if (!string.IsNullOrEmpty(ollamaConnectionString))
            {
                services.AddSingleton<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(
                    new OllamaSharp.OllamaApiClient(new Uri(ollamaConnectionString), "nomic-embed-text"));
            }

            // Agent memory for the Ralph learnings write/read paths (LearningsService, search_learnings MCP tool).
            services.AddDaedalusMemory(context.Configuration);
```
(`using Daedalus.Agents;`)
- AppHost: add `.WithReference(ollama)` to the `console` project (after `.WithReference(migrations)`).
- Update the comment on `AddDaedalusAgents` in Api `Program.cs` if not done.

Verify: `dotnet build`; `dotnet run --project src/Daedalus.AppHost` → console starts without DI errors (log shows "Starting Ralph Loop Console Application"). Commit `feat(memory): Ralph console host registers Daedalus memory and the Ollama embedding generator`.

---

## Task 11: Delete the slice, Pgvector and `UseVector()` (G5)

**Delete:** `src/Daedalus.Application/Abstractions/IEmbeddingService.cs`, `ILearningsRepository.cs`; `src/Daedalus.Infrastructure/Services/OllamaEmbeddingService.cs`, `Services/NoOp/NoOpEmbeddingService.cs`, `Persistence/LearningsRepository.cs`, `Persistence/Configurations/StructuredLearningEntryConfiguration.cs`; `src/Daedalus.Domain/Entities/StructuredLearningEntry.cs`; `tests/Daedalus.Tests.Unit.Domain/Entities/StructuredLearningEntryTests.cs`.

**Modify:**
- `ApplicationDbContext.cs`: remove `DbSet<StructuredLearningEntry> StructuredLearnings`.
- `InfrastructureServiceExtensions.cs`: remove `services.AddScoped<ILearningsRepository, LearningsRepository>();` (+ comment) and the whole `IEmbeddingService` factory block in `AddAgentFrameworkServices` (+ `using Daedalus.Infrastructure.Services.NoOp;`, `Microsoft.Extensions.AI`, `Microsoft.Extensions.Logging` if unused). Also delete the now-empty `Services/NoOp` folder if nothing else is in it (check `ls`).
- `Directory.Packages.props`: remove `Pgvector.EntityFrameworkCore`; `src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj` and `src/Daedalus.ServiceDefaults/Daedalus.ServiceDefaults.csproj`: remove the `PackageReference`.
- `UseVector()` sites → plain `UseNpgsql(connectionString)`: `ServiceDefaults/AspireExtensions.cs` (2, drop `using Pgvector.EntityFrameworkCore;`), `Infrastructure/Persistence/ApplicationDbContextFactory.cs` (1), `tests/Daedalus.Tests.Integration/Fixtures/PostgresFixture.cs` (2; rewrite the doc comment: "Every test context should still come from here so options stay in one place"), `tests/Daedalus.Tests.Integration/Agents/PostgresAgentSessionStoreTests.cs` (1 — simplest: use `fixture.CreateDbContext()` like the memory store test), `tests/Daedalus.Tests.Playwright.Api/Fixtures/E2EServerFixture.cs` (2), `tests/Daedalus.Tests.Playwright.Browser/Fixtures/E2EServerFixture.cs` (2). Keep every `CREATE EXTENSION IF NOT EXISTS vector;` line but change its comment to "Rag.NET's rag_chunks (Thalos memory index) needs the pgvector extension".
- Fixture workarounds: `tests/Daedalus.Tests.Integration/Fixtures/InMemoryDbContextFactory.cs` — drop the `Ignore(e => e.Embedding)` override; if nothing else needs the derived context, return `new ApplicationDbContext(options)` and delete the nested class + doc sentence. `tests/Daedalus.Tests.Unit.Infrastructure/Persistence/BrainstormRepositoryTests.cs` ~line 278 — remove the `StructuredLearningEntry` ignore (keep the `RowVersion` ignore).
- Historical migrations (the package that provided `Pgvector.Vector` is gone): in `20260302080051_AddSemanticEmbeddings.cs` replace `using Pgvector;` → nothing and `migrationBuilder.AddColumn<Vector>(` → `migrationBuilder.AddColumn<float[]>(` (the SQL is driven by `type: "vector(384)"`, so applied databases are unaffected); in `20260302080051_AddSemanticEmbeddings.Designer.cs`, `20260302120000_AddEmbeddingHnswIndex.Designer.cs`, `20260302134329_AddBrainstormSessions.Designer.cs`, `20260816174546_AddAgentSessions.Designer.cs` and `ApplicationDbContextModelSnapshot.cs`: delete `using Pgvector;` and change `b.Property<Vector>("Embedding")` → `b.Property<float[]>("Embedding")` (the snapshot's `StructuredLearningEntry` block disappears entirely in Task 12 when EF regenerates it; edit it now anyway so the build is green).
- Registration test `Sentinel_is_configured_from_options_and_gets_the_embedding_generator` etc. unaffected. `DaedalusKnowledgeToolsTests` already rewritten.
- README notes: handled in Task 19 (but remove nothing yet).

Verify: `dotnet build --nologo` 0 warnings; unit tests green; `dotnet test tests/Daedalus.Tests.Integration --filter "Category!=AuthenticationFlow"` green (fixtures use `EnsureCreated`, no `StructuredLearnings` in the model any more). Commit `refactor(learnings): remove the StructuredLearnings/embedding slice, Pgvector.EntityFrameworkCore and UseVector`.

---

## Task 12: Migration `AddAgentMemories` + migration test (G2)

**Files:** generate `src/Daedalus.Infrastructure/Migrations/2026MMDDhhmmss_AddAgentMemories.cs` (+ Designer, snapshot); test `tests/Daedalus.Tests.Integration/Migrations/AddAgentMemoriesMigrationTests.cs`.

**Step 1: failing test**
```csharp
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a fresh database (the fixture DB uses EnsureCreated): up to the migration before
///     <c>AddAgentMemories</c>, seed <c>StructuredLearnings</c>, migrate to latest, assert the copy and the drop.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddAgentMemoriesMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migration_copies_structured_learnings_into_agent_memories_and_drops_the_table()
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddAgentMemories", StringComparison.Ordinal));
            index.Should().BeGreaterThan(0);
            await db.Database.MigrateAsync(migrations[index - 1]);

            var taskId = Guid.NewGuid();
            var t0 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "StructuredLearnings" ("Id","Category","Pattern","Resolution","SourceTaskId","ProjectId","Severity","HitCount","CreatedAt","LastReferencedAt","Tags")
                VALUES ({Guid.NewGuid()}, 0, 'CS1061 missing member', 'Add using ZLinq', {taskId}, NULL, 2, 3, {t0}, {t0.AddDays(1)}, ARRAY['EF CORE','ZLINQ']),
                       ({Guid.NewGuid()}, 2, 'uses primary constructors', 'uses primary constructors', NULL, NULL, 0, 0, {t0.AddHours(1)}, NULL, ARRAY[]::text[])
                """);

            await db.Database.MigrateAsync();

            var rows = await db.AgentMemories.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
            rows.Should().HaveCount(2);

            var error = rows[0];
            error.OwnerId.Should().Be("daedalus");
            error.AgentId.Should().BeNull();
            error.Kind.Should().Be("learning");
            error.Text.Should().Be("CS1061 missing member\nAdd using ZLinq");
            error.Tags.Should().Equal("errorpattern", "high", "ef core", "zlinq");
            error.Source.Should().Be($"ralph:task/{taskId}");
            error.Importance.Should().Be(0.8);
            error.CreatedAt.Should().Be(t0);
            error.UpdatedAt.Should().Be(t0);
            error.LastRecalledAt.Should().Be(t0.AddDays(1));
            error.RecallCount.Should().Be(3);
            error.IsArchived.Should().BeFalse();
            error.IndexPending.Should().BeTrue();

            var convention = rows[1];
            convention.Text.Should().Be("uses primary constructors");
            convention.Tags.Should().Equal("codeconvention", "low");
            convention.Source.Should().Be("migration");
            convention.Importance.Should().Be(0.3);

            var tableExists = await db.Database.SqlQuery<bool>($"""SELECT to_regclass('"StructuredLearnings"') IS NOT NULL AS "Value" """).SingleAsync();
            tableExists.Should().BeFalse();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        }
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
```
**Step 2:** run → fails (no migration).

**Step 3: generate + edit**
```powershell
dotnet ef migrations add AddAgentMemories --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations
```
Open the generated `…_AddAgentMemories.cs`. `Up` must contain: `CreateTable("AgentMemories", …)` with the 14 columns, the four indexes, `DropIndex`s for `IX_StructuredLearning_*` (and possibly `IX_StructuredLearnings_Embedding` — EF does not know about that raw index; drop it via SQL below), and `DropTable("StructuredLearnings")`. Nothing touching other tables (else the snapshot was stale — investigate). Reorder so that the copy runs **after** `CreateTable` and **before** `DropTable`, and add the SQL:
```csharp
            // Copy the Ralph learnings into the memory table (owner "daedalus", kind "learning"); vectors are rebuilt by
            // ReindexPendingMemoriesHostedService (IndexPending = true). Mirrors LearningMemoryMapping (text, tags, importance, source).
```csharp
            migrationBuilder.Sql("""
                INSERT INTO "AgentMemories"
                    ("Id","OwnerId","AgentId","Kind","Text","Tags","Source","Importance","CreatedAt","UpdatedAt","LastRecalledAt","RecallCount","IsArchived","IndexPending")
                SELECT
                    s."Id",
                    'daedalus',
                    NULL,
                    'learning',
                    left(CASE WHEN s."Pattern" = s."Resolution" THEN s."Pattern" ELSE s."Pattern" || E'\n' || s."Resolution" END, 4000),
                    ARRAY[
                        CASE s."Category" WHEN 0 THEN 'errorpattern' WHEN 1 THEN 'successpattern' WHEN 2 THEN 'codeconvention' WHEN 3 THEN 'dependencyinfo' WHEN 4 THEN 'architecturedecision' ELSE 'general' END,
                        CASE s."Severity" WHEN 0 THEN 'low' WHEN 1 THEN 'medium' WHEN 2 THEN 'high' WHEN 3 THEN 'critical' ELSE 'medium' END
                    ] || COALESCE((
                        -- Trim, lower-case, truncate to MaxTagLength, drop blanks, de-duplicate keeping the first
                        -- occurrence, and only THEN take 8 — the LearningMemoryMapping.Tags pipeline. Capping on the raw
                        -- array position instead lets blanks, duplicates and over-long tags through, and the first edit of
                        -- such a memory is then rejected by AgentMemory.Update.
                        SELECT array_agg(d.tag ORDER BY d.ord)
                        FROM (
                            SELECT n.tag, min(n.ord) AS ord
                            FROM (
                                SELECT left(lower(btrim(u.t)), 32) AS tag, u.ord
                                FROM unnest(s."Tags") WITH ORDINALITY AS u(t, ord)
                            ) n
                            WHERE n.tag IS NOT NULL AND n.tag <> ''
                            GROUP BY n.tag
                            ORDER BY min(n.ord)
                            LIMIT 8
                        ) d
                    ), '{}'::text[]),
                    CASE WHEN s."SourceTaskId" IS NULL THEN 'migration' ELSE 'ralph:task/' || s."SourceTaskId"::text END,
                    CASE s."Severity" WHEN 3 THEN 1.0 WHEN 2 THEN 0.8 WHEN 1 THEN 0.5 ELSE 0.3 END,
                    s."CreatedAt",
                    s."CreatedAt",
                    s."LastReferencedAt",
                    s."HitCount",
                    false,
                    true
                FROM "StructuredLearnings" s;
                """);

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_StructuredLearnings_Embedding";""");
```
`Down`: EF scaffolds `CreateTable("StructuredLearnings", …)` + `DropTable("AgentMemories")`; keep it, and say in the comment that this destroys **every** memory (the migrated learnings and everything written since) and restores no learnings. Do **not** drop the `vector` extension. Add right after the `CreateTable`:
```csharp
            // AddSemanticEmbeddings.Down drops this column, and the scaffolded CreateTable above no longer creates it (the
            // model lost the vector(384) mapping with Pgvector), so without this the rollback dies with 42703 — after this
            // Down has already dropped AgentMemories. The extension is still installed because Rag.NET needs it.
            migrationBuilder.Sql("""ALTER TABLE "StructuredLearnings" ADD COLUMN IF NOT EXISTS "Embedding" vector(384);""");
```

**Step 4:** `dotnet test tests/Daedalus.Tests.Integration --filter "FullyQualifiedName~AddAgentMemoriesMigration"` → green; whole integration suite green; `dotnet run --project src/Daedalus.Migrations` against the local dev DB works (Aspire) — check `SELECT count(*) FROM "AgentMemories"` equals the former learnings count. Commit `feat(memory): AddAgentMemories migration — create table, copy StructuredLearnings, drop old table`.

---

## Task 13: `ReindexPendingMemoriesHostedService` (G4)

**Files:** create `src/Daedalus.Agents/Memory/ReindexPendingMemoriesHostedService.cs`; register in `AddDaedalusAgents` (see Task 6); test `tests/Daedalus.Tests.Unit.Application/Agents/ReindexPendingMemoriesHostedServiceTests.cs`.

**Step 1: failing tests**
```csharp
using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using Thalos.Memory;

namespace Daedalus.Tests.Unit.Application.Agents;

public sealed class ReindexPendingMemoriesHostedServiceTests
{
    private readonly IMemoryService _service = Substitute.For<IMemoryService>();
    private readonly IMemoryIndex _index = Substitute.For<IMemoryIndex>();
    private readonly MemoryConfig _config = new();

    private ReindexPendingMemoriesHostedService Sut() => new(_service, _index, _config, new FakeTimeProvider(), NullLogger<ReindexPendingMemoriesHostedService>.Instance);

    [Fact]
    public async Task Unavailable_index_waits_the_retry_interval_and_does_not_reindex()
    {
        _index.ProbeAsync(Arg.Any<CancellationToken>()).Returns(ZeroAlloc.Results.Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(false, null, "no embedding generator")));

        var next = await Sut().RunOnceAsync(CancellationToken.None);

        next.Should().Be(_config.Reindex.RetryInterval);
        await _service.DidNotReceiveWithAnyArgs().ReindexAsync(default!, default);
    }

    [Fact]
    public async Task Available_index_reindexes_pending_rows_and_sweeps_later_when_nothing_failed()
    {
        _index.ProbeAsync(Arg.Any<CancellationToken>()).Returns(ZeroAlloc.Results.Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(true, 768, null)));
        _service.ReindexAsync(Arg.Is<ReindexOptions>(o => o.PendingOnly), Arg.Any<CancellationToken>()).Returns(ZeroAlloc.Results.Result<ReindexReport, AgentError>.Success(new ReindexReport(3, 3, 0)));

        var next = await Sut().RunOnceAsync(CancellationToken.None);

        next.Should().Be(_config.Reindex.SweepInterval);
    }

    [Fact]
    public async Task Failed_rows_or_a_failed_reindex_retry_sooner()
    {
        _index.ProbeAsync(Arg.Any<CancellationToken>()).Returns(ZeroAlloc.Results.Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(true, 768, null)));
        _service.ReindexAsync(Arg.Any<ReindexOptions>(), Arg.Any<CancellationToken>()).Returns(ZeroAlloc.Results.Result<ReindexReport, AgentError>.Success(new ReindexReport(3, 1, 2)));
        (await Sut().RunOnceAsync(CancellationToken.None)).Should().Be(_config.Reindex.RetryInterval);

        _service.ReindexAsync(Arg.Any<ReindexOptions>(), Arg.Any<CancellationToken>()).Returns(ZeroAlloc.Results.Result<ReindexReport, AgentError>.Failure(AgentError.MemoryIndexUnavailable("down")));
        (await Sut().RunOnceAsync(CancellationToken.None)).Should().Be(_config.Reindex.RetryInterval);
    }

    [Fact]
    public async Task Exceptions_never_escape()
    {
        _index.ProbeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        (await Sut().RunOnceAsync(CancellationToken.None)).Should().Be(_config.Reindex.RetryInterval);
    }
}
```
(`AgentError.MemoryIndexUnavailable(string)` — use whichever factory exists; `Microsoft.Extensions.Time.Testing` is already referenced by the test project via Thalos.NET.Testing / integration tests; add `Microsoft.Extensions.TimeProvider.Testing` to CPM + csproj if not.)

**Step 3: implement**
```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos.Memory;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Agents.Memory;

/// <summary>
///     Embeds memories whose vector is missing (<c>IndexPending</c>): rows written while Ollama was down, rows migrated from
///     <c>StructuredLearnings</c>, rows written by the Ralph console host. Runs at startup (after a short delay so the Rag.NET
///     schema step is done) and then periodically — every <see cref="ReindexConfig.RetryInterval"/> while the index is
///     unavailable or rows failed, every <see cref="ReindexConfig.SweepInterval"/> otherwise. Never fails host start.
/// </summary>
internal sealed partial class ReindexPendingMemoriesHostedService(
    IMemoryService memory,
    IMemoryIndex index,
    MemoryConfig config,
    TimeProvider clock,
    ILogger<ReindexPendingMemoriesHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(config.Reindex.StartupDelay, clock, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(wait, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // host shutting down
        }
    }

    /// <summary>One probe + reindex pass; returns how long to wait before the next one.</summary>
    internal async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var health = await index.ProbeAsync(ct).ConfigureAwait(false);
            if (health.IsFailure || !health.Value.Available)
            {
                LogIndexUnavailable(logger, health.IsFailure ? health.Error.Code.ToString() : health.Value.Reason ?? "unknown");
                return config.Reindex.RetryInterval;
            }

            var report = await memory.ReindexAsync(new ReindexOptions { PendingOnly = true }, ct).ConfigureAwait(false);
            if (report.IsFailure)
            {
                LogReindexFailed(logger, report.Error.Code.ToString(), report.Error.Message);
                return config.Reindex.RetryInterval;
            }

            if (report.Value.Scanned > 0)
            {
                LogReindexed(logger, report.Value.Indexed, report.Value.Failed);
            }

            return report.Value.Failed > 0 ? config.Reindex.RetryInterval : config.Reindex.SweepInterval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReindexFailed(logger, ex.GetType().Name, "unexpected exception");
            return config.Reindex.RetryInterval;
        }
    }

    [LoggerMessage(EventId = 410, Level = LogLevel.Information, Message = "Memory index unavailable ({Reason}); pending memories stay index_pending until the next attempt")]
    private static partial void LogIndexUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 411, Level = LogLevel.Information, Message = "Reindexed pending memories: {Indexed} indexed, {Failed} failed")]
    private static partial void LogReindexed(ILogger logger, int indexed, int failed);

    [LoggerMessage(EventId = 412, Level = LogLevel.Warning, Message = "Reindexing pending memories failed: {Code} {Message}")]
    private static partial void LogReindexFailed(ILogger logger, string code, string message);
}
```
Registration in `AddDaedalusAgents` (not in `AddDaedalusMemory`): `if (options.Memory.Enabled && options.Memory.Reindex.Enabled) services.AddHostedService<ReindexPendingMemoriesHostedService>();`. Add a registration test asserting the hosted service is present when enabled and absent with `Thalos:Memory:Reindex:Enabled=false`.

**Step 4:** unit tests green; run the AppHost with Ollama up → after ~10 s the API log shows "Reindexed pending memories: N indexed, 0 failed" (N = migrated rows) and `SELECT count(*) FROM rag_chunks` = N. Commit `feat(memory): ReindexPendingMemoriesHostedService embeds index_pending memories at startup and periodically`.

---

## Task 14: `MemoryDto`, mapper, error mapping (G6)

**Files:** modify `AgentDtos.cs`, `AgentDtoMapper.cs`, `src/Daedalus.Api/Agents/AgentErrorResults.cs`; tests `AgentDtoMapperTests.cs`, `tests/Daedalus.Tests.Unit/Api/AgentErrorResultsTests.cs` (create if the file doesn't exist; `Tests.Unit` references Api).

`AgentDtos.cs`:
```csharp
/// <summary>A stored memory. <paramref name="IsShared"/> = owned by the shared owner (host-written project knowledge).</summary>
public sealed record MemoryDto(
    string Id, string OwnerId, string? AgentId, string Kind, string Text, IReadOnlyList<string> Tags, string Source, double Importance,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastRecalledAt, int RecallCount, bool IsArchived, bool IndexPending, bool IsShared);
```
`AgentDtoMapper`:
```csharp
    /// <summary>Maps a memory record; <paramref name="sharedOwnerId"/> marks host-written project knowledge.</summary>
    public static MemoryDto ToDto(MemoryRecord record, string? sharedOwnerId)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new MemoryDto(record.Id.ToString(), record.OwnerId, record.AgentId?.ToString(), record.Kind.Value, record.Text, record.Tags, record.Source,
            record.Importance, record.CreatedAt, record.UpdatedAt, record.LastRecalledAt, record.RecallCount, record.IsArchived, record.IndexPending,
            sharedOwnerId is not null && string.Equals(record.OwnerId, sharedOwnerId, StringComparison.Ordinal));
    }
```
`AgentErrorResults.ToStatusCode` — add before the default arm:
```csharp
        AgentErrorCode.MemoryValidationFailed => StatusCodes.Status400BadRequest,
        AgentErrorCode.MemoryForbidden => StatusCodes.Status403Forbidden,
        AgentErrorCode.MemoryNotFound => StatusCodes.Status404NotFound,
        AgentErrorCode.MemoryIndexUnavailable => StatusCodes.Status503ServiceUnavailable,
        AgentErrorCode.MemoryStoreFailed or AgentErrorCode.MemoryIndexFailed => StatusCodes.Status502BadGateway,
```
Tests: mapper `ToDto_maps_memory_record_and_flags_shared_owner` (owner "daedalus" with shared "daedalus" → IsShared true; "alice" → false); `AgentErrorResultsTests` theory over the five codes → statuses. Commit `feat(memory): MemoryDto, record mapping and HTTP status mapping for memory error codes`.

---

## Task 15: `AgentMemoriesController` (G6)

**Files:** create `src/Daedalus.Api/Controllers/AgentMemoriesController.cs`; modify `ApiJsonSerializerContext.cs` (`MemoryDto`, `PagedResultDto<MemoryDto>`); tests `tests/Daedalus.Tests.Integration/Controllers/AgentMemoriesControllerIntegrationTests.cs`, extend `tests/Daedalus.Tests.Integration/Api/AgentEndpointsSmokeTests.cs`.

**Controller**
```csharp
using Daedalus.Agents;
using Daedalus.Agents.Api;
using Daedalus.Agents.Security;
using Daedalus.Api.Agents;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Thalos;
using Thalos.Memory;
using ZeroAlloc.Authorization;

namespace Daedalus.Api.Controllers;

/// <summary>
///     Browse and forget agent memories. A caller sees their own memories plus the shared owner's; deleting is own-only,
///     shared-owner memories need the <c>developer</c> policy. Unknown/foreign ids answer 404 (no probing).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/agent-memories")]
[Authorize(Policy = "AgentUse")]
[Produces("application/json")]
public sealed partial class AgentMemoriesController(IMemoryService memory, IMemoryStore store, MemoryConfig config, ILogger<AgentMemoriesController> logger) : ControllerBase
{
    private static readonly DeveloperPolicy Developer = new();

    /// <summary>Caller's own + shared memories, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<MemoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? agentId = null, [FromQuery] string? kind = null, [FromQuery] string? tag = null,
        [FromQuery] bool includeArchived = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        AgentId? agent = null;
        if (!string.IsNullOrEmpty(agentId))
        {
            if (!AgentId.TryParse(agentId, null, out var parsed))
            {
                return AgentError.Validation("agentId is not a valid id.").ToActionResult(this);
            }

            agent = parsed;
        }

        var owners = new List<string> { caller.Id };
        if (!string.Equals(config.SharedOwnerId, caller.Id, StringComparison.Ordinal))
        {
            owners.Add(config.SharedOwnerId);
        }

        var query = new MemoryQuery
        {
            OwnerIds = owners,
            AgentId = agent,
            Kinds = string.IsNullOrWhiteSpace(kind) ? null : [new MemoryKind(kind.Trim().ToLowerInvariant())],
            Tags = string.IsNullOrWhiteSpace(tag) ? null : [tag.Trim()],
            IncludeArchived = includeArchived,
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, 100),
        };

        var result = await memory.ListAsync(query, ct);
        if (result.IsFailure)
        {
            return result.Error.ToActionResult(this);
        }

        var items = result.Value.Items.Select(r => AgentDtoMapper.ToDto(r, config.SharedOwnerId)).ToList();
        return Ok(new PagedResultDto<MemoryDto>(items, result.Value.Total, result.Value.Page, result.Value.PageSize));
    }

    /// <summary>One memory (own or shared).</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(MemoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        if (!MemoryId.TryParse(id, null, out var memoryId))
        {
            return AgentError.Validation("id is not a valid memory id.").ToActionResult(this);
        }

        var record = await store.GetAsync(memoryId, ct);
        if (record.IsFailure)
        {
            return record.Error.ToActionResult(this);
        }

        if (!IsVisible(record.Value, caller))
        {
            LogForeignAccess(logger, caller.Id, memoryId);
            return AgentError.MemoryNotFound(memoryId).ToActionResult(this);
        }

        return Ok(AgentDtoMapper.ToDto(record.Value, config.SharedOwnerId));
    }

    /// <summary>Forgets a memory: <c>hard=false</c> archives, <c>hard=true</c> deletes. Own memories only; shared ones need the developer policy.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Forget(string id, [FromQuery] bool hard = false, CancellationToken ct = default)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        if (!MemoryId.TryParse(id, null, out var memoryId))
        {
            return AgentError.Validation("id is not a valid memory id.").ToActionResult(this);
        }

        var record = await store.GetAsync(memoryId, ct);
        if (record.IsFailure)
        {
            return record.Error.ToActionResult(this);
        }

        string scopeOwner;
        if (string.Equals(record.Value.OwnerId, caller.Id, StringComparison.Ordinal))
        {
            scopeOwner = caller.Id;
        }
        else if (string.Equals(record.Value.OwnerId, config.SharedOwnerId, StringComparison.Ordinal))
        {
            var allowed = await Developer.EvaluateAsync(caller, ct);
            if (allowed.IsFailure)
            {
                return AgentError.MemoryForbidden("Shared memories can only be forgotten by developers or admins.").ToActionResult(this);
            }

            scopeOwner = config.SharedOwnerId;
        }
        else
        {
            LogForeignAccess(logger, caller.Id, memoryId);
            return AgentError.MemoryNotFound(memoryId).ToActionResult(this);
        }

        var result = await memory.ForgetAsync(memoryId, new MemoryScope(scopeOwner, null, null), hard, ct);
        if (result.IsFailure)
        {
            return result.Error.ToActionResult(this);
        }

        LogForgotten(logger, caller.Id, memoryId, hard);
        return NoContent();
    }

    private bool IsVisible(MemoryRecord record, ISecurityContext caller) =>
        string.Equals(record.OwnerId, caller.Id, StringComparison.Ordinal) || string.Equals(record.OwnerId, config.SharedOwnerId, StringComparison.Ordinal);

    [LoggerMessage(EventId = 320, Level = LogLevel.Warning, Message = "Caller {Caller} attempted to access memory {MemoryId} it cannot see; answered 404")]
    private static partial void LogForeignAccess(ILogger logger, string caller, MemoryId memoryId);

    [LoggerMessage(EventId = 321, Level = LogLevel.Information, Message = "Caller {Caller} forgot memory {MemoryId} (hard: {Hard})")]
    private static partial void LogForgotten(ILogger logger, string caller, MemoryId memoryId, bool hard);
}
```
`ApiJsonSerializerContext.cs`: `[JsonSerializable(typeof(MemoryDto))]`, `[JsonSerializable(typeof(PagedResultDto<MemoryDto>))]`.

**Tests** — `AgentMemoriesControllerIntegrationTests` (same helpers as the sessions controller test: `Controller(user, role?)`, `AssertProblem`; real `PostgresMemoryStore` over the fixture for `IMemoryStore`, `Substitute.For<IMemoryService>()` for list/forget; `MemoryConfig { SharedOwnerId = "daedalus" }`):
- `List_queries_own_and_shared_owners_and_returns_a_page` (service `ListAsync` captured query → OwnerIds equal `["alice","daedalus"]`, `PageSize` clamped to 100 when 500 given, kind lowercased).
- `Get_own_and_shared_are_visible_foreign_is_404` (seed via store: alice/daedalus/bob rows).
- `Forget_own_archives_under_the_callers_scope` (service `ForgetAsync` received `scope.OwnerId == "alice"`, `hard == false` → 204).
- `Forget_shared_requires_developer_or_admin` (alice → 403 `MemoryForbidden`; `Controller("carol","developer")` → 204 with scope owner `daedalus`; `Controller("dave","admin")` → 204).
- `Forget_foreign_answers_404_and_never_calls_the_service`.
- `Invalid_id_is_400`, `Anonymous_is_401`.

`AgentEndpointsSmokeTests` — add `Memories_endpoint_lists_the_callers_and_shared_memories`: seed two rows through `_factory.Services.GetRequiredService<IMemoryStore>()` (owner alice + owner daedalus) and one for bob; `GET /api/agent-memories` as alice → 200, two ids, `IsShared` true for the daedalus one; as anonymous → 401.

Run integration suite → green. Commit `feat(api): agent memories endpoints (list, get, forget) with shared-owner developer gate`.

---

## Task 16: `AgentApiClient` memory methods (G7)

**File:** modify `src/Daedalus.Web/Services/AgentApiClient.cs` (add `using Daedalus.Application.DTOs;`).
```csharp
    /// <summary>Own + shared memories, newest first.</summary>
    public Task<Result<PagedResultDto<MemoryDto>>> ListMemoriesAsync(string? kind = null, string? agentId = null, bool includeArchived = false, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}", $"includeArchived={(includeArchived ? "true" : "false")}" };
        if (!string.IsNullOrEmpty(kind)) query.Add($"kind={Uri.EscapeDataString(kind)}");
        if (!string.IsNullOrEmpty(agentId)) query.Add($"agentId={Uri.EscapeDataString(agentId)}");
        return GetAsync<PagedResultDto<MemoryDto>>($"/api/agent-memories?{string.Join('&', query)}", ct);
    }

    /// <summary>One memory (own or shared).</summary>
    public Task<Result<MemoryDto>> GetMemoryAsync(string id, CancellationToken ct = default) =>
        GetAsync<MemoryDto>($"/api/agent-memories/{Uri.EscapeDataString(id)}", ct);

    /// <summary>Forgets a memory (archive; <paramref name="hard"/> deletes).</summary>
    public async Task<Result> ForgetMemoryAsync(string id, bool hard = false, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync(Relative($"/api/agent-memories/{Uri.EscapeDataString(id)}?hard={(hard ? "true" : "false")}"), ct);
            return response.IsSuccessStatusCode ? Result.Success() : Result.Failure(await ReadProblemMessageAsync(response, ct));
        }
        catch (AccessTokenNotAvailableException) { return Result.Failure(LoginRequired); }
        catch (HttpRequestException ex) { return Result.Failure($"API error: {ex.Message}"); }
    }
```
Build Web. Commit `feat(web): AgentApiClient list/get/forget memories`.

---

## Task 17: Memories panel on `Agent.razor` (G7)

**Files:** modify `src/Daedalus.Web/Pages/Agent.razor`, `Agent.razor.cs`.

**Markup** — in the session header stack (next to "Close session") add:
```razor
                <RadzenButton Text="Memories" Icon="psychology" ButtonStyle="@(_memoriesOpen ? ButtonStyle.Primary : ButtonStyle.Light)" Size="ButtonSize.Small"
                              Click="@ToggleMemoriesAsync" data-testid="agent-memories-toggle"/>
```
Wrap the existing chat column and a new right column in a horizontal stack; the right column (rendered when `_memoriesOpen`):
```razor
        @if (_memoriesOpen)
        {
            <RadzenStack Gap="0.5rem" class="rz-p-2 rz-border-left" Style="width: 340px; flex-shrink: 0; overflow-y: auto;" data-testid="agent-memories-panel">
                <RadzenText TextStyle="TextStyle.Overline" Style="opacity: 0.6;">Recalled this turn</RadzenText>
                @if (_recallStatus is { } status)
                {
                    <RadzenText TextStyle="TextStyle.Caption" Style="opacity: 0.7;" data-testid="agent-recall-status">@status</RadzenText>
                }
                @if (_recalled.Count == 0)
                {
                    <RadzenText TextStyle="TextStyle.Caption" Style="opacity: 0.6;">Nothing recalled yet.</RadzenText>
                }
                @foreach (var m in _recalled)
                {
                    <RadzenCard class="rz-p-2" data-testid="agent-recalled-item" data-memory-id="@m.Id">
                        <RadzenText TextStyle="TextStyle.Caption" Style="margin: 0; opacity: 0.7;">@m.Kind &middot; @Age(m.CreatedAt)@(m.IsShared ? " · shared" : "")</RadzenText>
                        <RadzenText TextStyle="TextStyle.Body2" Style="margin: 0; white-space: pre-wrap;">@m.Text</RadzenText>
                    </RadzenCard>
                }

                <RadzenText TextStyle="TextStyle.Overline" class="rz-mt-2" Style="opacity: 0.6;">Browse</RadzenText>
                <RadzenStack Orientation="Orientation.Horizontal" Gap="0.5rem" AlignItems="AlignItems.Center">
                    <RadzenDropDown TValue="string" Data="@MemoryKinds" @bind-Value="@_kindFilter" Change="@(_ => LoadMemoriesAsync(1))"
                                    AllowClear="true" Placeholder="All kinds" Style="flex: 1;" data-testid="agent-memory-kind-filter"/>
                    <RadzenButton Icon="refresh" ButtonStyle="ButtonStyle.Light" Size="ButtonSize.Small" Click="@(() => LoadMemoriesAsync(_memoriesPage))" data-testid="agent-memories-refresh"/>
                </RadzenStack>
                @if (_memories.Count == 0)
                {
                    <RadzenText TextStyle="TextStyle.Caption" Style="opacity: 0.6;" data-testid="agent-memories-empty">No memories.</RadzenText>
                }
                @foreach (var m in _memories)
                {
                    <RadzenCard class="rz-p-2" data-testid="agent-memory-item" data-memory-id="@m.Id" data-memory-kind="@m.Kind">
                        <RadzenStack Orientation="Orientation.Horizontal" JustifyContent="JustifyContent.SpaceBetween" AlignItems="AlignItems.Start" Gap="0.25rem">
                            <div style="min-width: 0;">
                                <RadzenText TextStyle="TextStyle.Caption" Style="margin: 0; opacity: 0.7;">@m.Kind &middot; @Age(m.CreatedAt)@(m.IsShared ? " · shared" : "")@(m.IndexPending ? " · not indexed yet" : "")</RadzenText>
                                <RadzenText TextStyle="TextStyle.Body2" Style="margin: 0; white-space: pre-wrap;">@m.Text</RadzenText>
                                @if (m.Tags.Count > 0)
                                {
                                    <RadzenText TextStyle="TextStyle.Caption" Style="margin: 0; opacity: 0.6;">@string.Join(", ", m.Tags)</RadzenText>
                                }
                            </div>
                            <RadzenButton Icon="delete" ButtonStyle="ButtonStyle.Light" Size="ButtonSize.ExtraSmall" title="Forget"
                                          Click="@(() => ForgetAsync(m.Id))" data-testid="agent-memory-forget"/>
                        </RadzenStack>
                    </RadzenCard>
                }
                @if (_memoriesTotal > _memories.Count || _memoriesPage > 1)
                {
                    <RadzenStack Orientation="Orientation.Horizontal" JustifyContent="JustifyContent.SpaceBetween">
                        <RadzenButton Text="Prev" Size="ButtonSize.ExtraSmall" ButtonStyle="ButtonStyle.Light" Disabled="@(_memoriesPage <= 1)" Click="@(() => LoadMemoriesAsync(_memoriesPage - 1))"/>
                        <RadzenText TextStyle="TextStyle.Caption" Style="margin: 0;">@_memoriesPage / @Math.Max(1, (int)Math.Ceiling(_memoriesTotal / (double)MemoriesPageSize))</RadzenText>
                        <RadzenButton Text="Next" Size="ButtonSize.ExtraSmall" ButtonStyle="ButtonStyle.Light" Disabled="@(_memoriesPage * MemoriesPageSize >= _memoriesTotal)" Click="@(() => LoadMemoriesAsync(_memoriesPage + 1))"/>
                    </RadzenStack>
                }
            </RadzenStack>
        }
```
**Code (`@code` / code-behind)** — fields: `private const int MemoriesPageSize = 20; private static readonly string[] MemoryKinds = ["fact", "preference", "decision", "learning", "note"]; private bool _memoriesOpen; private string? _kindFilter; private readonly List<MemoryDto> _memories = []; private int _memoriesTotal; private int _memoriesPage = 1; private readonly List<MemoryDto> _recalled = []; private readonly List<string> _recalledIds = []; private string? _recallStatus;`. Methods:
```csharp
    private async Task ToggleMemoriesAsync()
    {
        _memoriesOpen = !_memoriesOpen;
        if (_memoriesOpen) await LoadMemoriesAsync(1);
    }

    private async Task LoadMemoriesAsync(int page)
    {
        var result = await Api.ListMemoriesAsync(kind: _kindFilter, page: page, pageSize: MemoriesPageSize, ct: _pageCts.Token);
        result.Match(p => { _memories.Clear(); _memories.AddRange(p.Items); _memoriesTotal = p.Total; _memoriesPage = p.Page; }, NotifyError);
    }

    private async Task ForgetAsync(string id)
    {
        var result = await Api.ForgetMemoryAsync(id, hard: false, _pageCts.Token);
        if (result.IsFailure) { NotifyError(result.Error); return; }
        _memories.RemoveAll(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        _recalled.RemoveAll(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        _memoriesTotal = Math.Max(0, _memoriesTotal - 1);
    }

    private async Task LoadRecalledAsync()
    {
        _recalled.Clear();
        foreach (var id in _recalledIds.Distinct(StringComparer.Ordinal))
        {
            var m = await Api.GetMemoryAsync(id, _pageCts.Token);
            if (m.IsSuccess) _recalled.Add(m.Value);
        }
    }

    private static string Age(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span.TotalHours < 1 ? "just now" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : span.TotalDays < 30 ? $"{(int)span.TotalDays} d ago" : at.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }
```
`SendAsync`: at start `_recalledIds.Clear(); _recallStatus = null;`; in `finally`, after `RefreshSessionsAsync()`: `if (SameSession()) { await LoadRecalledAsync(); if (_memoriesOpen) await LoadMemoriesAsync(_memoriesPage); }`. `Apply(...)` new cases:
```csharp
            case "memory-recalled" when evt.Memory is { } recalled:
                if (recalled.Ids is not null) _recalledIds.AddRange(recalled.Ids);
                _recallStatus = $"{recalled.Count ?? recalled.Ids?.Count ?? 0} memories recalled";
                break;
            case "memory-stored" when evt.Memory is { } stored:
                _recallStatus = stored.Deduped == true ? "Memory updated (duplicate merged)" : "New memory stored";
                break;
            case "memory-recall-failed" when evt.Memory is { } failed:
                _recallStatus = $"Memory recall unavailable ({failed.Code})";
                break;
            case "memory-index-pending":
                _recallStatus = "Memory stored; indexing pending (embedding service unavailable)";
                break;
```
Add `@using Daedalus.Application.DTOs` if needed. Manual check via Aspire: turn → "N memories recalled" + items; browse shows migrated learnings (kind `learning`, shared); Forget archives. Commit `feat(web): Memories panel on the Agent page (recalled this turn, browse, forget)`.

---

## Task 18: Playwright — stub memory events, index stub, seed, page object, scenario (G7)

**Files:** create `tests/Daedalus.Tests.Playwright.Browser/Fixtures/StubMemoryIndex.cs`; modify `Fixtures/StubAgentRuntime.cs`, `Fixtures/E2EServerFixture.cs`, `PageObjects/AgentPage.cs`, `Scenarios/AgentPageBrowserTests.cs`.

`StubMemoryIndex.cs` — every member returns success/empty; `ProbeAsync` → `new MemoryIndexHealth(true, 768, null)`; `SearchAsync` → empty list. Purpose: E2E has no Ollama, and the stub runtime emits the recall events itself.

`E2EServerFixture.cs` — after `AddDaedalusAgents(...)` and the runtime replacement:
```csharp
            foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IMemoryIndex)).ToList()) builder.Services.Remove(descriptor);
            builder.Services.AddSingleton<IMemoryIndex, StubMemoryIndex>();
```
`SeedTestDataAsync(dbContext)` — add (before the early return guard or after seeding projects; guard on `AnyAsync` of `AgentMemories`):
```csharp
            var now = DateTime.UtcNow;
            dbContext.AgentMemories.Add(AgentMemory.Create(Guid.NewGuid(), "e2e-test-user-id", null, "fact", "The E2E user prefers xUnit over NUnit.", ["testing"], "seed", 0.7, now, indexPending: false).Value);
            dbContext.AgentMemories.Add(AgentMemory.Create(Guid.NewGuid(), "daedalus", null, "learning", "Playwright locators on the PRD page use data-testid.", ["codeconvention", "low"], "migration", 0.3, now.AddDays(-3), indexPending: false).Value);
```
`StubAgentRuntime` — ctor gains `IMemoryStore memories`; constants `public const string SharedOwnerId = "daedalus";`; right after the claim succeeds (before the first text delta):
```csharp
            var recall = await memories.ListAsync(new MemoryQuery { OwnerIds = [request.Caller.Id, SharedOwnerId], PageSize = 5 }, ct).ConfigureAwait(false);
            if (recall.IsSuccess && recall.Value.Items.Count > 0)
            {
                var ids = recall.Value.Items.Select(m => m.Id).ToList();
                yield return new MemoryRecalledEvent(sessionId, turnId, ids.Count, ids, recall.Value.Items.Sum(m => m.Text.Length));
            }
```
`AgentPage.cs` — add locators: `MemoriesToggle` (`agent-memories-toggle`), `MemoriesPanel`, `RecalledItems` (`agent-recalled-item`), `RecallStatus`, `MemoryItems` (`agent-memory-item`), `MemoryKindFilter`, `MemoriesEmpty`; `MemoryItem(string text) => _page.Locator("[data-testid='agent-memory-item']", new() { HasText = text })`; `MemoryForgetButton(string text) => MemoryItem(text).Locator("[data-testid='agent-memory-forget']")`; `Task OpenMemoriesAsync()`.

Scenario:
```csharp
    [Test]
    [Description("Turn recalls seeded memories → Memories panel lists them; forgetting one removes it")]
    public async Task AgentPage_Memories_ShouldShowRecalled_AndForget()
    {
        await _agentPage.NavigateAsync().ConfigureAwait(false);
        await _agentPage.CreateSessionAsync().ConfigureAwait(false);
        await _agentPage.SendAsync("hi").ConfigureAwait(false);
        await Expect(_agentPage.AssistantTexts.Last).ToContainTextAsync(StubAgentRuntime.ReplyText).ConfigureAwait(false);
        await Expect(_agentPage.SendButton).ToBeVisibleAsync().ConfigureAwait(false);

        await _agentPage.OpenMemoriesAsync().ConfigureAwait(false);
        await Expect(_agentPage.MemoriesPanel).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.RecallStatus).ToContainTextAsync("recalled").ConfigureAwait(false);
        await Expect(_agentPage.RecalledItems).ToHaveCountAsync(2).ConfigureAwait(false);
        await Expect(_agentPage.MemoriesPanel).ToContainTextAsync("prefers xUnit").ConfigureAwait(false);
        await Expect(_agentPage.MemoryItem("prefers xUnit")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.MemoryItem("data-testid")).ToContainTextAsync("shared").ConfigureAwait(false);

        await _agentPage.MemoryForgetButton("prefers xUnit").ClickAsync().ConfigureAwait(false);
        await Expect(_agentPage.MemoryItem("prefers xUnit")).ToHaveCountAsync(0).ConfigureAwait(false);
        await Page.ScreenshotAsync(new() { Path = "docs/regression-screenshots/agent-memories.png", FullPage = true }).ConfigureAwait(false);
    }
```
(The E2E user is `admin`, so forgetting the shared item would also pass — the scenario forgets the own one. Seeded rows persist across tests in the fixture DB; run this test in isolation first, then the whole Agent category — the reopen-transcript test is unaffected.)

Run `dotnet test tests/Daedalus.Tests.Playwright.Browser --filter "FullyQualifiedName~AgentPage"` → green. Commit `test(e2e): Agent memories panel scenario with stubbed memory recall`.

---

## Task 19: ArchUnit, README, diagrams (G8)

- `CleanArchitectureTests.cs`: add `typeof(IMemoryService).Assembly` (Thalos.NET.Memory) and the RagNet assembly to `ThalosAssemblies`; new rule `OnlyAgentsProject_DependsOn_RagNet`: `Types().That().Are(DomainTypes/ApplicationTypes/InfrastructureTypes/ApiTypes/WebTypes)` (one rule per layer or a loop) `.Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching("^Rag(\\.|$)")` (check the actual Rag.NET root namespace in its XML: `rag.net.abstractions/<ver>/lib/net10.0/*.xml`). Run.
- README "Thalos agents" section: config block gets `Memory` (+ table rows `Thalos:Memory:*`), agent `Tools` includes `memory__*`; new subsection **Memory** (what: curated records, auto-recall + `memory__remember/recall/forget/list`, shared owner `daedalus` = Ralph learnings; where: `AgentMemories` table + Rag.NET `rag_chunks` in the same DB; Ollama `nomic-embed-text` 768 dims; without Ollama → `index_pending` + `ReindexPendingMemoriesHostedService`; migration `AddAgentMemories` copied `StructuredLearnings`; console host registers `AddDaedalusMemory`); endpoints table adds `GET /api/agent-memories`, `GET /api/agent-memories/{id}`, `DELETE /api/agent-memories/{id}?hard=`; SSE kinds add `memory-recalled | memory-stored | memory-recall-failed | memory-index-pending`; "Operational notes": rewrite the **PostgreSQL image** bullet (needed by Rag.NET's `rag_chunks`; `Pgvector.EntityFrameworkCore` is gone; `rag_chunks` dimension mismatch → `DROP TABLE rag_chunks;` then restart, reindex rebuilds), replace "Ralph unchanged … share learnings and embeddings" with "Ralph's learnings now live in the agent memory (shared owner) — parser unchanged, persistence/recall via `ILearningsMemory`". Fix line ~803 (`UseVector()` mention) and the Tech-stack/testing rows that mention `vector(384)`.
- `docs/architecture-diagrams.md` §14: add the memory recall step (`MemoryContextProvider → IMemoryService → RagNet index / PostgresMemoryStore`) and the `memory-*` SSE events.
Commit `docs: Thalos memory — README config, endpoints, operations; ArchUnit Rag.NET boundary`.

---

## Task 20: Regression, review, merge (G8)

1. `dotnet format`; `dotnet build --nologo` (0 warnings); unit + integration suites; Playwright Agent category.
2. Aspire smoke with a real `ANTHROPIC_API_KEY`: `/agent` → "Remember that I prefer xUnit" → `memory__remember` tool card + `memory-stored`; new turn "which test framework do I like?" → recalled memory in the panel and in the answer; DELETE via panel; `GET /api/agent-memories` in Scalar.
3. `regression-test` skill against the running app (Agent page with the panel + one control page) → report under `docs/`.
4. `pre-push-review` on `feature/thalos-memory`; fix findings.
5. Merge to `main` / open the PR (user decides); comment on #228 with links (design, both plans, Thalos tag).

---

## Task 21: nuget.org 0.2.0 switch, planning docs, `complete` (G8)

Precondition: Thalos.NET **0.2.0** published (eight packages).
1. `Directory.Packages.props`: all eight `Thalos.NET*` → `0.2.0` (comment "Thalos.NET (nuget.org)"); `nuget.config` back to nuget.org-only (delete the `thalos-local` source + mapping); `git rm -r packages-local`; `dotnet restore --force-evaluate`; build + unit + integration green. Commit `build: consume Thalos.NET 0.2.0 from nuget.org`.
2. `docs/planning/ROADMAP.md` + `MILESTONE.md`: 1.2 → `complete (<date>; Thalos.NET 0.2.0 on nuget.org, #228)` with plan links; `STATE.md`: last completed / open decisions / next step (1.3 skills). Commit `chore(state): phase 1.2 memory complete; Thalos.NET 0.2.0 consumed`.
3. Invoke the `project-orchestration` skill with `complete 1.2` (records the phase end); close #228 with a summary comment.

## Definition of done (= phase 1.2 done)
- `main` builds with 0 warnings; unit, integration and Playwright Agent suites green; regression report PASS; pre-push review PASS.
- A signed-in user's turn gets relevant memories injected (`memory-recalled` visible in the panel), `memory__*` tools work, `/api/agent-memories` lists/forgets with the shared-owner developer gate.
- `StructuredLearnings` is gone, rows live in `AgentMemories` and are indexed in `rag_chunks` after startup reindex; Ralph learnings write/read via memory; `Pgvector.EntityFrameworkCore` and every `UseVector()` removed; pgvector image kept.
- Thalos.NET 0.2.0 on nuget.org and consumed; #228 closed; roadmap 1.2 complete.

---

**Summary (10 lines)**
1. Plan: 21 tasks in execution order (G1 pins/wiring → G2 store → G1 mapper/wiring → G3 Ralph paths → G5 slice deletion → G2 migration → G4 reindex → G6 API → G7 Web/Playwright → G8 docs/review/nuget switch); TDD per task, conventional commits, exact commands.
2. Not written to disk: this session is read-only (no Write tool; shell writes prohibited) — the parent must save the document above to `docs/plans/2026-08-17-thalos-memory-plan-b.md`.
3. Key structural finding: Ralph's learnings run in `Daedalus.Console` (no `Daedalus.Agents` ref, no Ollama) → new `AddDaedalusMemory` + AppHost `WithReference(ollama)`; Application/Infrastructure must stay Thalos-free (ArchUnit) → new port `ILearningsMemory` + `ParsedLearning`, adapter `ThalosLearningsMemory` in Agents.
4. Sequencing constraint solved: the single `AddAgentMemories` migration is generated only after the slice is removed from the EF model (Task 11 → 12) so EF scaffolds create+drop; copy SQL inserted between; historical migrations lose `Pgvector.Vector` (→ `float[]`, SQL unchanged).
5. Deviations flagged (§0.3): `AgentMemories.Id` uuid (MemoryId Guid-backed), extra `IndexPending` index, extra `GET /api/agent-memories/{id}` for panel hydration, `LearningsCount` dropped.
6. Biggest risk: Plan A's exact member names (MemoryQuery/MemoryUpdate/RememberRequest/RecalledMemory/ReindexReport, `UseMemoryStore<T>`, event constructors) are assumptions — Task 1 step 4 reconciles against the package XML before any code.
7. Open question: memory events must be `AgentEvent` subclasses to reach SSE; if 0.2.0 ships them only as hub notifications, Tasks 5/17/18 need a notification→SSE bridge (stop and report).
8. Open question: `PostgresFixture` uses `EnsureCreated`, so the migration test creates its own database per run and migrates to the predecessor of `AddAgentMemories`; needs Docker + pgvector image (already the case).
9. Open question: Rag.NET `PgVectorStore` startup in test hosts without an embedding generator (probe unavailable is expected; fail-fast only on dimension mismatch) — E2E swaps `IMemoryIndex` for a stub to keep forget/recall deterministic.
10. Critical files: `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs`, `src/Daedalus.Agents/Memory/PostgresMemoryStore.cs` (new), `src/Daedalus.Application/Services/LearningsService.cs`, `src/Daedalus.Infrastructure/Migrations/<stamp>_AddAgentMemories.cs` (new), `src/Daedalus.Web/Pages/Agent.razor`.

### Critical Files for Implementation
- C:\Projects\Prive\daedalus\src\Daedalus.Agents\DaedalusAgentsServiceCollectionExtensions.cs
- C:\Projects\Prive\daedalus\src\Daedalus.Agents\Memory\PostgresMemoryStore.cs (new)
- C:\Projects\Prive\daedalus\src\Daedalus.Application\Services\LearningsService.cs
- C:\Projects\Prive\daedalus\src\Daedalus.Infrastructure\Persistence\ApplicationDbContext.cs (+ new `AddAgentMemories` migration)
- C:\Projects\Prive\daedalus\src\Daedalus.Web\Pages\Agent.razor
