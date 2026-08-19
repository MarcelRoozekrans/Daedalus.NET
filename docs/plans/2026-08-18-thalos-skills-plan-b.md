# Thalos.NET — Plan B (phase 1.3): Daedalus consumes `Thalos.NET.Skills`

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make Daedalus the first consumer of Thalos.NET 0.3.0 skills: a `Skills` table + `PostgresSkillStore` passing `SkillStoreContractTests` on Testcontainers, migration `AddSkills`, `UseSkills(...)` + `UseSkillStore<PostgresSkillStore>()` wired into `AddDaedalusAgents` **only** (the Ralph console host keeps memory-only registration), a real `skills/` folder at the repo root with two starter procedures (`daedalus-migrations`, `thalos-release`) that load at startup in production **and** in the test hosts, and the agent's `Skills` globs + `skills__*` tools in `appsettings.json`.

**Architecture:** `Daedalus.Domain` gets the Thalos-free `Skill` aggregate (invariants mirroring the library's rules, exactly as `AgentMemory` mirrors `MemoryRules`). `Daedalus.Infrastructure` gets `SkillConfiguration` + `DbSet<Skill> Skills` + the `AddSkills` migration. `Daedalus.Agents` — still the only project allowed to reference Thalos — gets `Skills/PostgresSkillStore.cs` and the wiring inside `AddDaedalusAgents`. The `skills/` folder is authored at the repo root and flows into every host's content root through a `Content` item on `Daedalus.Api.csproj` (the `.mcp.json` mechanism). **No API surface, no Blazor surface** (design §6): the repo is the UI.

**Tech Stack:** as phase 1.2 (.NET 10, EF Core 10 + Npgsql 10.0.3, Blazor WASM + Radzen, xUnit/NUnit/Playwright, Testcontainers `pgvector/pgvector:pg16`) + **`Thalos.NET.* 0.3.0`** — nine packages, the new one being `Thalos.NET.Skills` (net8.0 + net10.0, in-process cosine search, no Rag.NET dependency).

**Prerequisite:** Plan A complete in `C:\Projects\Prive\Thalos.NET` and **0.3.0 on nuget.org**. If it is not published yet, see §0.2 for the local-pack fallback and its removal gate.

**Design doc:** `docs/plans/2026-08-18-thalos-skills-design.md` (§6–§9 are this plan's scope; §3–§5 are the API this plan consumes) · **Tracking:** #229 · **Phase-1.2 plan (conventions, and the source of every "1.2 lesson" below):** `docs/plans/2026-08-17-thalos-memory-plan-b.md`

---

## 0. Read this first

### 0.1 Facts and conventions (verified in the Daedalus repo, HEAD `65278d8`, 2026-08-18)

**Layout / namespaces**

- `src/Daedalus.Agents` (`namespace Daedalus.Agents`, `.Api`, `.Memory`, `.Security`, `.Sessions`, `.Tools`) is the **only** project that may reference Thalos or Rag.NET (ArchUnit enforces it). It references Domain, Application, Infrastructure and the packages `Thalos.NET`, `.Mcp`, `.Anthropic`, `.Sentinel`, `.Memory`, `.Memory.RagNet`, `ZeroAlloc.Mapping`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.AI`, `Configuration.Binder`, `Hosting.Abstractions`, `Options`. `IsTrimmable=false`. It has `<InternalsVisibleTo Include="Daedalus.Tests.Integration" />` and `<InternalsVisibleTo Include="Daedalus.Tests.Unit.Application" />`.
- Thalos namespaces in use today: `Thalos` (abstractions: `AgentError`, `AgentErrorCode`, `AgentDefinition`, `AgentEvent` + all `Memory*Event`s, typed ids), `Thalos.Anthropic`, `Thalos.Mcp`, `Thalos.Memory`, `Thalos.Memory.RagNet`, `Thalos.Sentinel`, `Thalos.Tools`, `Thalos.Testing`. **The skills package is assumed to follow suit: `Thalos.Skills` for the model/ports/service, with `SkillCatalogueFailedEvent` + `AgentErrorCode.Skill*` in `Thalos` — verify in Task 2.**
- **Composition root:** `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs`.
  - `AddDaedalusAgents(services, configuration, environment, IEmbeddingGenerator<string,Embedding<float>>? embeddingGenerator = null)` — binds `DaedalusAgentsOptions` from section `Thalos`, calls `ThrowIfMemoryAlreadyRegistered`, registers `DaedalusFailurePatternsTools` (scoped), `AgentSessionCrashRecovery` (hosted), `ValidateMemoryConfig`, three `TryAddSingleton`s (`MemoryConfig`, `RalphRecallConfiguration`, `ILearningsMemory`), `TryAddSingleton(embeddingGenerator)`, then `services.AddThalos(thalos => { ConfigureMemory(..., ensureSchema: true); thalos.UseAnthropic(configuration).UseSessionStore<PostgresAgentSessionStore>().AddLocalTools("daedalus", typeof(DaedalusKnowledgeTools)).AddMcpServersFromFile(ResolveMcpConfigPath(...)).AddPolicy<DeveloperPolicy>(); …RequireToolPolicy…; …AddAgent(ToDefinition(a))…; if (Sentinel.Enabled) UseAISentinel(...); })`, then conditionally `AddHostedService<ReindexPendingMemoriesHostedService>()`.
  - `AddDaedalusMemory(services, configuration)` — memory only, for the Ralph console worker, `ensureSchema: false`.
  - **`AddDaedalusAgents` and `AddDaedalusMemory` are mutually exclusive and enforced** (`ThrowIfMemoryAlreadyRegistered` probes for a `MemoryConfig` service descriptor and throws `InvalidOperationException` "…mutually exclusive…"). **Skills are API-host only: do not touch `AddDaedalusMemory`.**
  - `ResolveMcpConfigPath(configured, environment) => Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured)` — **this is the pattern for resolving skill roots**.
  - `ValidateMemoryConfig` is the house style for Daedalus-only configuration validation: plain `InvalidOperationException` at *registration* time, message naming the offending key (`$"{MemoryConfig.SectionName}:VectorDimensions must be greater than 0, but was {…}."`). Mirror it exactly.
  - `ToDefinition(AgentConfig)` maps `Tools = agent.Tools.Count == 0 ? ["*"] : [.. agent.Tools]` and `Memory = agent.Memory is null ? null : new AgentMemorySettings { … }`.
- **Options:** `src/Daedalus.Agents/DaedalusAgentsOptions.cs` holds `DaedalusAgentsOptions` (`SectionName = "Thalos"`, `McpConfigPath`, `IList<AgentConfig> Agents`, `IList<ToolPolicyConfig> ToolPolicies`, `SentinelConfig Sentinel`, `MemoryConfig Memory`) plus `AgentConfig`, `AgentMemoryConfig`, `MemoryConfig` (`SectionName = "Thalos:Memory"`), `ReindexConfig`, `ToolPolicyConfig`, `SentinelConfig` as separate top-level classes in the same file and namespace. **Collections are `IList<T>` with `{ get; } = []` because the configuration binder appends to a pre-populated list** — this is why the `Tools` default (`["*"]`) lives in the mapping, not the class.
- **Store model:** `src/Daedalus.Agents/Memory/PostgresMemoryStore.cs` — singleton, `IDbContextFactory<ApplicationDbContext>` + fresh short-lived DbContext per call, ZeroAlloc `Result<T, AgentError>` / `UnitResult<AgentError>`, aggregate validates → `AgentError.*ValidationFailed`, `DbUpdateException` → `*StoreFailed` with `ex.GetType().Name` as detail (never raw exception text), `ExecuteUpdateAsync`/`ExecuteDeleteAsync` for set-based writes, `ConfigureAwait(false)` everywhere, `DateTime` UTC in the entity and `new DateTimeOffset(…, TimeSpan.Zero)` outward. Npgsql/connection exceptions **propagate**. Copy this shape.
- **Domain aggregate model:** `src/Daedalus.Domain/Entities/AgentMemory.cs` — `sealed class … : Entity<Guid>` (CSFE), private parameterless ctor for EF, `public const int Max*Length` constants that *mirror the Thalos rules* so a violation is a validation error and never a varchar failure, static `Create(...)` returning CSFE `Result<T>`, an `Update(...)` that validates everything before mutating anything, private `List<string> _tags` exposed as `IReadOnlyList<string>`, tag normalisation under a `#pragma warning disable CA1308`, `list.Exists(...)` not `Any(...)` (MA0020). **Domain must stay Thalos-free** (ArchUnit).
- **EF configuration:** `src/Daedalus.Infrastructure/Persistence/Configurations/AgentMemoryConfiguration.cs` — `internal sealed class … : IEntityTypeConfiguration<T>`, `ToTable`, `HasKey`, explicit `HasMaxLength` using the aggregate's constants, backing-field mapping (`builder.Property("_tags").HasColumnName("Tags").HasColumnType("text[]").IsRequired(); builder.Ignore(m => m.Tags);`), named indexes (`IX_AgentMemory_*`). Registered by `ApplyConfigurationsFromAssembly`; `ApplicationDbContext` exposes `public DbSet<AgentMemory> AgentMemories => Set<AgentMemory>();` (line 30, add the new one after it).
- **Migrations:** `src/Daedalus.Infrastructure/Migrations/`, timestamped ids, latest `20260817194349_AddAgentMemories`. That file is the convention reference: `#pragma warning disable CA1861` header (composite-index `new[]` arrays), no BOM (`dotnet format` CHARSET), XML doc comments on the class **and on `Down`** describing what a rollback destroys. **1.2 lesson (do not re-learn it): the scaffolder diffs *both* directions**, so an entity mapping that no longer exists can crash `migrations add` with a `NullReferenceException` in `MigrationsModelDiffer`, and a scaffolded `Down` must leave a schema the *predecessor's* `Down` can still run against — `AddAgentMemories.Down` hand-adds `ALTER TABLE "StructuredLearnings" ADD COLUMN IF NOT EXISTS "Embedding" vector(384);` for exactly that reason, and `AddAgentMemoriesMigrationTests.Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain` pins it permanently.
- **Migration command** (Api is the startup project; README documents the same):
  `dotnet ef migrations add AddSkills --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations`
  Applying: `dotnet run --project src/Daedalus.Migrations` (Aspire runs it, and the AppHost now uses `.WaitForCompletion(migrations)`). EF 10 throws on `Migrate()` when the model has pending changes not in the snapshot — always regenerate through `migrations add`, never hand-write the snapshot.
- **Test projects:** `Daedalus.Tests.Unit` (ArchUnit + controllers + `Configuration/ApiThalosConfigurationTests`; links `src/Daedalus.Api/appsettings.json` as `Daedalus.Api.appsettings.json` and the Console's as `Daedalus.Console.appsettings.json`; references Api/Web/App/Domain/Infra/Console), `Daedalus.Tests.Unit.Application` (references `Daedalus.Agents` + `Thalos.NET`; holds `Agents/DaedalusAgentsRegistrationTests.cs`, `Agents/AgentDtoMapperTests.cs`), `Daedalus.Tests.Unit.Domain` (`Entities/AgentMemoryTests.cs`), `Daedalus.Tests.Integration` (references `Thalos.NET.Testing`, `Daedalus.Agents`, `Daedalus.Api`, `Daedalus.Console`; `Agents/PostgresMemoryStoreTests.cs`, `Migrations/AddAgentMemoriesMigrationTests.cs`), `Daedalus.Tests.Playwright.Browser`.
- **Fixtures:** `tests/Daedalus.Tests.Integration/Fixtures/PostgresFixture.cs` — Testcontainers `pgvector/pgvector:pg16`, `CREATE EXTENSION IF NOT EXISTS vector`, **`EnsureCreatedAsync()` (not migrations)**, Respawn reset, `fixture.ConnectionString`, `fixture.CreateDbContext()`, `static PostgresFixture.CreateDbContextOptions(connectionString)`, `[Collection(DatabaseCollection.Name)]` for sequential DB tests. A new `DbSet` is therefore in the fixture database automatically, **before** the migration exists.
- **`.mcp.json` copy mechanism (the model for the `skills/` folder):** `Daedalus.Api.csproj` has `<Content Update=".mcp.json" CopyToOutputDirectory="PreserveNewest" />`, and because `Daedalus.Tests.Unit`, `Daedalus.Tests.Unit.Application` (transitively), `Daedalus.Tests.Integration` and `Daedalus.Tests.Playwright.Browser` all reference `Daedalus.Api`, the file lands in each test's output directory — `ApiThalosConfigurationTests.Mcp_config_is_copied_next_to_the_api_and_parses` asserts it. `Content` items flow transitively; `None` items do not.
- **Content roots:** API = the publish/bin directory; `ApiThalosConfigurationTests` fakes `IHostEnvironment.ContentRootPath` = `AppContext.BaseDirectory`; `DaedalusAgentsRegistrationTests` fakes it as **`Path.GetTempPath()`** (nothing is copied there — this is why the skills roots default to *empty*, §0.6); the Playwright `E2EServerFixture` sets `ContentRootPath = testAssemblyDir` and pushes `ConnectionStrings:daedalus` in with an in-memory source right before `AddDaedalusAgents`.
- **Error mapping:** `src/Daedalus.Api/Agents/AgentErrorResults.cs` (`ToStatusCode` + `ToActionResult`, `code` extension = the `AgentErrorCode` name). `tests/Daedalus.Tests.Unit/Controllers/AgentErrorResultsTests.cs` has an **exhaustiveness guard** — `Every_AgentErrorCode_value_has_an_explicit_mapping_test` asserts `Enum.GetValues<AgentErrorCode>().Should().HaveCount(18)`. **Adding four `Skill*` codes in 0.3.0 breaks that test the moment the pin is bumped**, exactly as the six `Memory*` codes did in 1.2 — so the status arms are pulled forward into Task 3, the first task after the pin, and the count goes 18 → **22**.
- **SSE / DTO mapping:** `src/Daedalus.Agents/Api/AgentDtoMapper.cs` `ToDto(AgentEvent)` — since 1.2 it **passes unknown kinds through** (`_ => new AgentEventDto(agentEvent.Kind)`) instead of throwing, and already carries the five `memory-*` kinds via `AgentEventDto.Memory`. See §0.6 for the `skill-catalogue-failed` decision.
- **ArchUnit:** `tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs` — `ThalosAssemblies[]` (7 entries today), `RagNetAssemblies[]`, `ThalosNamespacePattern = "^Thalos(\\.|$)"`. **1.2 lesson: ArchUnitNET does not synthesise types for assemblies it has not loaded**, so a rule over a namespace whose assembly is absent passes *vacuously*. `ThalosAssemblies` must gain `Thalos.NET.Skills` or the `^Thalos(\.|$)` rules will never see a skills type, and the addition needs a load-bearing proof fact.
- **Docker:** `src/Daedalus.Api/Dockerfile` restores from explicit `.csproj` COPYs then `COPY . .`. `.dockerignore` excludes `docs/` and `*.md` **but Docker's `*` does not cross `/`, so `*.md` only excludes root-level markdown** — `skills/<name>/SKILL.md` survives the context. Task 10 verifies this rather than assuming it, and adds an explicit re-include with a comment.
- **CI** (`.github/workflows/ci.yml`): `dotnet build Daedalus.sln --configuration Release`; unit `--filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"`; integration `--filter "FullyQualifiedName~Integration&FullyQualifiedName!~Playwright&Category!=AuthenticationFlow"`. CI **cannot see** `C:\Projects\Prive\.nuget-local`.
- **Conventions:** primary constructors, `[LoggerMessage]` partial methods with per-class EventId ranges, `ConfigureAwait(false)` in library code, `TreatWarningsAsErrors` (0 warnings), `dotnet format` before the final review, CSFE `Result` in Domain/Ralph code and ZeroAlloc `Result` at the Thalos boundary. Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Commit messages (read this twice — a 1.2 commit failed CI on it):** conventional commits, commitlint on PRs, `.commitlintrc.yml` extends `@commitlint/config-conventional` and sets `body-max-line-length: 0` but **leaves `footer-max-line-length` at 100**. commitlint parses **every body paragraph after the first as a footer**, so a long line in the second paragraph fails the job even though `body-max-line-length` is off. **Header ≤ 100 chars and hard-wrap every body line at 100.**

### 0.2 Pinning Thalos.NET 0.3.0

**Preferred path — 0.3.0 is on nuget.org (what 1.2 ended up doing):** `Directory.Packages.props` bumps the eight existing `Thalos.NET*` pins `0.2.0 → 0.3.0` and adds a ninth, `Thalos.NET.Skills`, at `0.3.0`. `nuget.config` stays nuget.org-only. Nothing else changes. Confirm availability first:

```powershell
dotnet package search Thalos.NET.Skills --exact-match --format json | ConvertFrom-Json | % { $_.searchResult.packages.version }
```

**Fallback — Plan A has not published yet.** Do *not* invent a version; produce a local pack from the Thalos.NET working tree and pin that exact string:

```powershell
pwsh C:\Projects\Prive\Thalos.NET\scripts\pack-local.ps1        # writes 0.3.0-local.<yyyyMMddHHmmss> into C:\Projects\Prive\.nuget-local
Get-ChildItem C:\Projects\Prive\.nuget-local\Thalos.NET.Skills.0.3.0-local.*.nupkg | Select-Object -Last 1
```

(The script's header still says "eight packages since 0.2.0"; after Plan A it packs **nine**. If it produces eight, `Thalos.NET.Skills` is not in `Thalos.NET.slnx` yet — stop and report: Plan A is incomplete.)

Then add the source to `nuget.config` and pin `0.3.0-local.<stamp>` on all nine ids:

```xml
<add key="thalos-local" value="C:\Projects\Prive\.nuget-local" />
…
<packageSource key="thalos-local"><package pattern="Thalos.NET*" /></packageSource>
```

**Rules for the fallback, non-negotiable:**
1. CI cannot see that absolute path. A branch pinned to a local pack **must not be pushed** — or, if you need CI early, copy the nine `.nupkg` files into a committed `packages-local/` folder and point the source at the relative path (the phase-1.1 pattern).
2. The local pin, the `thalos-local` source and any `packages-local/` folder are **removed before the PR** — Task 20 will not pass its own checklist otherwise, and Task 21 re-pins to `0.3.0` from nuget.org.
3. Record which path you took in §0.8 (1.2's very first amendment did exactly this).

**Transitive pins:** CPM has `CentralPackageTransitivePinningEnabled`. `Thalos.NET.Skills` needs no vector store, so the `Npgsql 10.0.3` floor from `Rag.NET.VectorStores.PgVector` is the only one in play and is already pinned. If `NU1109`/`NU1010` names a package, `dotnet nuget why Thalos.NET.Skills <id>` shows the source of the floor; add the `PackageVersion` and note it.

### 0.3 Commands

```powershell
dotnet build --nologo                                                                              # 0 warnings
dotnet test --nologo --filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"      # unit (CI filter)
dotnet test tests/Daedalus.Tests.Integration --nologo --filter "Category!=AuthenticationFlow"        # integration (Docker)
dotnet test tests/Daedalus.Tests.Playwright.Browser --nologo --filter "FullyQualifiedName~AgentPage" # browser
dotnet ef migrations add AddSkills --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations
dotnet format
```

Single test while iterating: `dotnet test tests/<project> --nologo --filter "FullyQualifiedName~<TestClass>"`.

### 0.4 Branching

`git switch -c feature/thalos-skills` off `main` (HEAD `65278d8`). One small commit per task. `pre-push-review` before the PR.

### 0.5 Assumed `Thalos.NET.Skills` 0.3.0 API (design §3–§5; **reconcile in Task 2**)

The design fixes the interfaces; member names below are this plan's assumptions where the design left them implicit. Task 2 dumps the real surface from the package XML docs and every difference is search-replaced through the code in this plan **before** Task 4 uses it. The contract suite is the truth for store semantics.

| Item | Assumed shape | Used in |
|---|---|---|
| `SkillName` | typed identifier in `Thalos.Skills`: `new SkillName(string)`, `.Value`, `TryParse(string, IFormatProvider?, out SkillName)`, rule `^[a-z][a-z0-9_-]{0,63}$` | store, aggregate mirror |
| `SkillDocument` | `sealed record` with `required SkillName Name`, `required string Description` (≤300), `required string Body` (≤64 KB), `IReadOnlyList<string> Tags = []`, `required string SourcePath`, `required string ContentHash`, `bool IsActive = true`, `required DateTimeOffset UpdatedAt` | store, tests |
| `ISkillStore` | `UpsertAsync(SkillDocument, ct) → Result<SkillDocument, AgentError>`, `GetAsync(SkillName, ct) → Result<SkillDocument, AgentError>`, `ListAsync(SkillQuery, ct) → Result<IReadOnlyList<SkillDocument>, AgentError>`, `DeactivateMissingAsync(IReadOnlyList<SkillName>, ct) → UnitResult<AgentError>` — all `ValueTask` | `PostgresSkillStore` |
| `SkillQuery` | `{ IReadOnlyList<SkillName>? Names; IReadOnlyList<string>? Tags; bool IncludeInactive }` — null/empty = no filter; `Tags` **AND** (mirror `MemoryQuery`, confirm) | store |
| `SkillOptions` | `{ bool Enabled = true; IList<string> Roots; CatalogueOptions Catalogue { int MaxChars = 2000 }; SkillSearchOptions Search { int TopK; double MinScore } }` | wiring |
| `ThalosBuilder.UseSkills(Action<SkillOptions>?)` | registers `ISkillStore`/`ISkillIndex` defaults, `SkillSyncService` (`IHostedLifecycleService.StartingAsync`), `SkillContextProvider`, the `skills` tool source | wiring |
| `ThalosBuilder.UseSkillStore<T>()` | replaces the store (singleton + telemetry proxy, like `UseMemoryStore<T>`) | wiring |
| `AgentDefinition.Skills` | `IReadOnlyList<string>` glob allow-list, sibling of `Tools` | `ToDefinition` |
| `SkillCatalogueFailedEvent` | `AgentEvent` subclass, kind `skill-catalogue-failed`, carries at least `AgentErrorCode Code` | mapper fact |
| `AgentErrorCode` additions | `SkillNotFound`, `SkillStoreFailed`, `SkillValidationFailed`, `SkillSearchUnavailable`; factories `AgentError.SkillNotFound(SkillName)`, `AgentError.SkillStoreFailed(string message, string? detail = null)`, `AgentError.SkillValidationFailed(string)` | store, `AgentErrorResults` |
| `Thalos.Testing.SkillStoreContractTests` | `protected abstract ValueTask<ISkillStore> CreateStoreAsync(TimeProvider clock)` (mirrors `MemoryStoreContractTests`) | Task 6 |

> **Stop-and-report conditions.** (1) `UseSkillStore<T>()` does not exist → fall back to `services.AddSingleton<ISkillStore, PostgresSkillStore>()` *after* `AddThalos` only if Thalos used `TryAdd`; otherwise report before Task 9. (2) `SkillSyncService` is not an `IHostedLifecycleService` but a manual call → Task 11's startup test needs a different trigger. (3) `SkillStoreContractTests` does not exist in `Thalos.NET.Testing` 0.3.0 → Task 6 cannot be written as specified; report.

### 0.5b Reconciled against Plan A (2026-08-18, before execution)

Plan A (`docs/plans/2026-08-18-thalos-skills-plan-a.md`) now fixes the real 0.3.0 surface, so §0.5's
assumptions were checked against it up front rather than waiting for Task 2. **All three stop-and-report
conditions are resolved as non-issues**: `UseSkillStore<TStore>()` exists, `SkillSyncService` is an
`IHostedLifecycleService` (`StartingAsync`), and `Thalos.Testing.SkillStoreContractTests` exists with
exactly the assumed `protected abstract ValueTask<ISkillStore> CreateStoreAsync(TimeProvider clock)`.

Confirmed unchanged: namespace `Thalos.Skills`; `ISkillStore`'s four members and their exact
`ValueTask<Result<…, AgentError>>` shapes; `SkillQuery { Names?, Tags?, IncludeInactive }`;
`SkillDocument`'s member list; `AgentDefinition.Skills` as `IReadOnlyList<string> = []`;
`SkillCatalogueFailedEvent` carrying an `AgentErrorCode`; `SkillOptions.Roots` defaulting to empty
(so §0.6 deviation 2 stands as written).

Six deltas to apply while executing. Task 2 still runs — it verifies these against the **published**
package rather than against a sibling plan — but it should now be a confirmation, not a discovery.

| # | §0.5 assumed | Plan A actually specifies | Affects |
|---|---|---|---|
| 1 | `new SkillName("x")` | **The constructor is private.** `SkillName` is a hand-written `readonly record struct` (not `[TypedId]`, which mints Guid-backed ULIDs); construct with `SkillName.Parse(s)` or `SkillName.TryParse(s, out var n)`, both of which **trim and lower-case**. Note `Parse` takes **one argument** — the `IFormatProvider` overload is an explicit `IParsable` implementation, so `Parse(s, provider)` will not compile from Daedalus (confirmed in Plan A Task 4). `Value` is `""` for `default`. | Task 4 aggregate, Task 6 store |
| 2 | `CatalogueOptions Catalogue` | Named **`SkillCatalogueOptions`** (`MaxChars = 2000`) | Task 8 options |
| 3 | `SkillOptions { Enabled, Roots, Catalogue, Search }` | Also carries `SectionName = "Thalos:Skills"`, **`ExposeTools`** and **`SyncOnStartup`** | Task 8, Task 9 |
| 4 | `AgentError.SkillNotFound(SkillName)` | Takes a **`string`** — `SkillName` lives in `Thalos.Skills` and Abstractions does not reference it | Task 3, Task 6 |
| 5 | three `AgentError` factories | A fourth exists: **`AgentError.SkillSearchUnavailable(string message, string? detail = null)`**, so `AgentErrorResults` needs a fourth arm in Task 3 | Task 3 |
| 6 | `UseSkills(Action<SkillOptions>?)` | An **`UseSkills(IConfiguration)`** overload and a **`UseSkillIndex<TIndex>()`** also exist | Task 9 |

Delta 6 does not change Task 9's approach: §0.6 deviation 4 resolves roots to absolute paths against
`ContentRootPath` before they reach `SkillOptions`, which the `Action<SkillOptions>` overload supports and
the `IConfiguration` one does not. Keep the delegate overload.

**Additional constraint discovered while executing Plan A Task 8.** `SkillStoreContractTests` gained
`List_orders_ordinally_not_by_culture_collation`, which lists skills named `a-b`, `a0b`, `a_b` and `aab`
and requires exactly that order. Ordinal ranks them by code point (`-` U+002D < `0` U+0030 < `_` U+005F
< `a` U+0061); a typical Postgres collation such as `en_US.UTF-8` treats punctuation as variable-weight
and returns a different order. **`PostgresSkillStore.ListAsync` must therefore order with a binary
collation** — `ORDER BY name COLLATE "C"` — **or sort client-side after materialising.** Task 6 will
fail the contract otherwise, and the failure will look like an unrelated ordering bug.

Delta 1 is the one with teeth. `SkillName.Parse` lower-cases, so a `Skill` aggregate that stores the raw
string and a `SkillDocument` that round-trips through `SkillName` can disagree on case. Mirror the
library: normalise through `SkillName.TryParse` in `Skill.Create` and fail with `SkillValidationFailed`
when it returns false, rather than re-implementing the `^[a-z][a-z0-9_-]{0,63}$` rule in the aggregate.

### 0.6 Deviations from the design (deliberate, documented)

1. **`Skills.Name` is the primary key, typed `character varying(64)`** — not a surrogate id. `SkillDocument` has no id, files are the source of truth, and the name is globally unique by construction (duplicate names across roots are a load error, design §4). Consequence: `DeactivateMissingAsync` is one `ExecuteUpdateAsync` keyed on the name set, and history stays resolvable because rows are deactivated, never deleted.
2. **The skills root default is empty, not `["skills"]`.** The binder appends to pre-populated `IList<T>`s (§0.1), and `DaedalusAgentsRegistrationTests` builds hosts with `ContentRootPath = Path.GetTempPath()` where no skills folder exists. An empty default means "no roots configured → nothing to sync, no error", while the shipped `appsettings.json` configures `["skills"]` explicitly. A *configured* root that does not exist **throws at registration** (item 3).
3. **A configured-but-missing skills root fails the host at registration**, `InvalidOperationException` naming `Thalos:Skills:Roots[0]`. The design only says the *store* being unreachable must fail start; this extends the same reasoning to the files, because the failure it protects against is silent and total (an agent that quietly lost every procedure looks identical to a healthy one), and because it is the only thing that catches a broken `Content` copy in the Docker image or a test output directory. It is gated on `Thalos:Skills:Enabled` so a host can opt out.
4. **Roots are resolved to absolute paths in Daedalus, against `IHostEnvironment.ContentRootPath`**, before they reach `SkillOptions.Roots` — the `ResolveMcpConfigPath` pattern. This makes the same configuration work in the API, in `ApiThalosConfigurationTests`, in the integration startup test and in the Playwright fixture without any of them knowing Thalos' own path-resolution rule.
5. **`Skill` bodies are stored in an unbounded `text` column**; the aggregate enforces the 64 K limit, so an over-size body is a `SkillValidationFailed`, never a varchar truncation error. Same call as `AgentMemory.Text` in 1.2.
6. **No `AgentDtoMapper` arm for `skill-catalogue-failed`.** The mapper has passed unknown kinds through by name since 1.2, the design ships no UI for skills (§6: "the repo is the UI"), and the event carries only an error code that no client renders. A DTO arm would mean a new payload record, a `MemoryEventDto`-style field on `AgentEventDto`, a `[JsonSerializable]` entry and a Blazor `Apply` arm — four files of ceremony for a diagnostic that belongs in the log. What Task 13 *does* add is a **regression fact** pinning that the event reaches the client as kind-only and does not kill the stream, so the pass-through is a tested decision rather than an accident. Revisit if and when a skills panel exists.
7. **Tag limits mirror `AgentMemory`** (≤10 tags, ≤32 chars each, trimmed/lower-cased/de-duplicated). The design does not specify skill tag limits. If `SkillStoreContractTests` round-trips a longer tag or an eleventh tag, **relax the constants to match the contract** and record it — the contract is the truth, exactly as it was for `AgentMemory.Text` round-tripping untrimmed in 1.2.
8. **`ContentHash` is validated for length only (≤128), not format.** SHA-256 is 64 hex chars or 44 base64 chars; the store must not reject an encoding the library chose.
9. **No API, no Blazor, no `AgentMemoriesController` equivalent** (design §6). Skills are not per-user data and there is nothing to authorize per row.

### 0.7 Task map (execution order; groups G1–G8 as in the design brief)

| # | Group | Task |
|---|---|---|
| 1 | G1 | Branch + pin Thalos.NET 0.3.0 (nine packages) + `Daedalus.Agents` package reference |
| 2 | G1 | API reconciliation against the real package XML docs; apply corrections to this plan |
| 3 | G1 | `AgentErrorResults` `Skill*` status arms + theory rows + count guard 18 → 22 |
| 4 | G2 | Domain `Skill` aggregate + tests |
| 5 | G2 | EF `SkillConfiguration` + `DbSet<Skill>` (no migration yet) |
| 6 | G2 | `PostgresSkillStore` + `SkillStoreContractTests` on Testcontainers |
| 7 | G3 | Migration `AddSkills` + migration test (incl. the `Down` chain) |
| 8 | G4 | `SkillsConfig`/`AgentConfig.Skills` options + `ValidateSkillsConfig` + validation tests |
| 9 | G4 | Wiring in `AddDaedalusAgents` only (`UseSkills` + `UseSkillStore<PostgresSkillStore>()`) + registration tests |
| 10 | G5 | `skills/` folder, the two starter skills, the `Content` copy, `.dockerignore` verification |
| 11 | G5 | Startup test: both skills load into Postgres through a real host start |
| 12 | G6 | `appsettings.json`: `Thalos:Skills`, agent `Skills` globs, `skills__*` tools, instructions |
| 13 | G6 | `skill-catalogue-failed` pass-through fact (SSE decision) |
| 14 | G7 | ArchUnit: `ThalosAssemblies` gains Skills + load-bearing proof |
| 15 | G7 | README |
| 16 | G7 | `docs/architecture-diagrams.md` §14 |
| 17 | G7 | Planning docs (ROADMAP / MILESTONE / STATE) |
| 18 | G8 | `dotnet format` + full regression run (unit, integration, browser) |
| 19 | G8 | AppHost smoke run: skills sync writes rows, catalogue reaches a real turn |
| 20 | G8 | Pre-push review, PR, CI, merge |
| 21 | G8 | Close #229, `complete 1.3`, STATE handoff to 1.4 |

### 0.8 Amendments (append here during execution, like 1.2's §0.7)

*(Executor: append one bullet per task, in this style: what deviated from the plan, why, and the resulting fact counts. Future tasks read this section, so record anything the plan's later code depends on — renamed API members above all. Do not silently "fix" the plan text; record the deviation.)*

- **Task 1 (2026-08-19) — the preferred pin path, no fallback.** Thalos.NET 0.3.0 went to nuget.org
  earlier the same day (Plan A Task 25), so §0.2's preferred path applied verbatim: eight pins bumped
  `0.2.0 → 0.3.0`, `Thalos.NET.Skills` added as the ninth, `nuget.config` untouched and still
  nuget.org-only. **No local pack, no `thalos-local` source, no `packages-local/` folder ever existed
  this phase**, so §0.2's removal gate is moot from the start — exactly as it turned out in 1.2.
  `dotnet restore --force-evaluate` raised no `NU1109`/`NU1010`, so the `Npgsql 10.0.3` floor was the
  only transitive pin in play and it was already set. Build: **0 warnings**. The predicted single
  breakage is the only breakage: `Every_AgentErrorCode_value_has_an_explicit_mapping_test` fails
  `Expected … 18 item(s), but found 22`. Suite totals unchanged otherwise — Domain 255, Application 368,
  Infrastructure 130, Unit 114/115.

- **Task 2 (2026-08-19) — confirmation, no substitutions.** Reconciled against the **published** package
  (`~/.nuget/packages/thalos.net.*/0.3.0/lib/net10.0/*.xml`) and cross-checked against Thalos.NET HEAD
  `2e23e4c`. **All six §0.5b deltas hold and no seventh was found, so no substitutions were applied to
  Tasks 4, 6, 8, 9 or 13.** The seven Step 3 confirmations, so later tasks need not re-derive them:

  1. Namespace `Thalos.Skills` for `SkillName`/`SkillDocument`/`ISkillStore`/`SkillQuery`/`SkillOptions`;
     `AgentErrorCode.Skill*`, the four `AgentError.Skill*` factories and `SkillCatalogueFailedEvent`
     in `Thalos`. `AgentEventKinds.SkillCatalogueFailed = "skill-catalogue-failed"`.
  2. `UseSkills(Action<SkillOptions>)`, `UseSkills(IConfiguration)`, `UseSkillStore<T>()` and
     `UseSkillIndex<T>()` all live on `Thalos.Skills.SkillThalosBuilderExtensions`, extending
     `Thalos.ThalosBuilder`. Task 9 keeps the delegate overload (§0.5b delta 6 reasoning stands).
  3. `SkillOptions` carries `SectionName`, `Enabled`, `Roots`, `Catalogue`, `Search`, `ExposeTools`,
     `SyncOnStartup`. `Catalogue` is `SkillCatalogueOptions { MaxChars }`; `Search` is
     `SkillSearchOptions { TopK, MinScore }`.
  4. `SkillSyncService : IHostedLifecycleService`, work in `StartingAsync` — before any other hosted
     service, so Task 11's `host.StartAsync()` does trigger it.
  5. `AgentDefinition.Skills` is `IReadOnlyList<string>` defaulting to **`[]`**, not `["*"]` — the
     opposite of `Tools`, deliberately, because a catalogue costs tokens every turn. Task 8's mapping
     must not copy `Tools`' `["*"]` fallback.
  6. `SkillStoreContractTests` exposes `protected abstract ValueTask<ISkillStore> CreateStoreAsync(TimeProvider clock)`
     and carries **12** `[Fact]`/`[Theory]` attributes.
  7. Limits (all `public const` on `SkillDocument`, plus `SkillName.MaxLength`): description **300**,
     body **65536** (64 KiB), tags **10** × **32** chars, source path **512**, name **64**.

  **Two facts the plan did not state, recorded because later tasks depend on them.** First,
  `SkillDocument.ContentHash` is `[NotEmpty]` with **no `MaxLength`** in the library — the doc comment
  says lower-case hex SHA-256, i.e. 64 chars — so §0.6-8's Daedalus-side ≤128 cap constrains nothing the
  library would have allowed through at a realistic length, and stays as written. Second,
  `SkillName.CompareTo` is `string.CompareOrdinal`, which independently corroborates the §0.5b ordering
  constraint: **Task 6's `ListAsync` must not order under a culture collation.** Deviation §0.6-7 also
  needs no relaxation after all — the library's tag limits are exactly `AgentMemory`'s 10 × 32.

  `SkillQuery.Tags` is confirmed **AND** semantics (every requested tag must be present, each normalised
  through `SkillRules.NormalizeTag` before an ordinal `Contains`), which is what Task 6's Daedalus-side
  facts assert.

- **Task 4 (2026-08-19) — §0.5b delta 1 is not implementable in Domain, and the task body already knew it.**
  Delta 1 says "normalise through `SkillName.TryParse` in `Skill.Create`". **That cannot compile.**
  `SkillName` lives in `Thalos.Skills`; `Daedalus.Domain.csproj` references only `CSharpFunctionalExtensions`,
  and `CleanArchitectureTests.DomainLayer_ShouldNotDependOn_Thalos` fails the build if it ever did. Task 4's
  own spec is the correct one and was followed verbatim: `IsValidName` **mirrors** the rule
  (`^[a-z][a-z0-9_-]{0,63}$`) in the aggregate, exactly as `AgentMemory` mirrors `MemoryRules`, and the
  aggregate **rejects** a non-normalised name rather than normalising it — which is why
  `Create_rejects_invalid_names` lists `"Daedalus"` as invalid instead of expecting `"daedalus"` back.

  **Consequence Task 6 must honour:** normalisation happens *upstream*, in the library, before Daedalus ever
  sees the value. `PostgresSkillStore` must therefore pass `document.Name.Value` (already trimmed and
  lower-cased by `SkillName.TryParse`) straight into `Skill.Create`. Passing a raw model- or file-supplied
  string instead would hit the aggregate's rejection and surface as `SkillValidationFailed` on a name the
  library considered perfectly valid. Delta 1's *worry* was real — aggregate and document must not disagree
  on case — but the fix belongs at the boundary, not in Domain.

- **Task 4 (cont.) — `MaxSourcePathLength` is deliberately looser than the library's.** The aggregate caps
  source paths at **1024** while `SkillDocument.MaxSourcePathLength` is **512**, so Daedalus can never reject
  a path the library already accepted. Same shape as §0.6-8's `ContentHash` ≤ 128 against a 64-char hex hash:
  where the library validates first, the Daedalus-side cap exists only to keep a `varchar` from truncating,
  so erring wide is correct. The plan's test asserts the `"1024"` message, which pins it.

  Suite: Domain 255 → **275** (+20 facts), build 0 warnings, whole unit filter green at 892.

- **Task 6 (2026-08-19) — three deviations, one of them a plan error the contract caught.**
  14/14 green (12 contract facts + the 2 Daedalus ones).

  1. **`DeactivateMissingAsync` must stamp the clock; the plan said it must not.** The plan's snippet carried
     the comment "UpdatedAt is not bumped, because nothing about the document changed", and the test file's
     `CreateStoreAsync` said "the store needs no clock". Both are wrong:
     `DeactivateMissing_only_touches_active_unseen_skills_and_stamps_the_clock` asserts the deactivated row's
     `UpdatedAt` equals the time of the sweep that deactivated it. **The contract wins** (§0.6-7's principle).
     `PostgresSkillStore` now takes a `TimeProvider` — matching `PostgresMemoryStore`'s shape after all — and
     the `ExecuteUpdateAsync` sets `UpdatedAt` alongside `IsActive`. The existing `WHERE IsActive` filter is
     what satisfies the second half of that fact, "an already-inactive skill is not stamped again": a later
     sweep cannot match a row it already deactivated. **Task 9 must therefore have a `TimeProvider` resolvable
     from DI** for `UseSkillStore<PostgresSkillStore>()` to construct.
  2. **Ordering is client-side and deliberately not SQL.** §0.5b's warning was correct and
     `List_orders_ordinally_not_by_culture_collation` is the fact that would have caught it. Rather than
     `ORDER BY name COLLATE "C"`, `ListAsync` materialises and sorts with `string.CompareOrdinal`: filtering
     stays in SQL, the table is repo-sized, and the result cannot depend on the server's collation at all.
     Verified green against the real `pgvector/pgvector:pg16` container, not reasoned about.
  3. **Two §0.5b substitutions the plan's own snippets had not absorbed**: `new SkillName(x)` → `SkillName.Parse(x)`
     (delta 1 — the constructor is private) in `ToDocument`, and `AgentError.SkillNotFound(name)` →
     `SkillNotFound(key)` (delta 4 — it takes a `string`). The test file also drops its local `NewDocument`
     helper in favour of the base class's `NewClock()`/`NewSkill(...)`, which the plan's own note prefers and
     which sidesteps the private constructor entirely.

- **Task 6 (cont.) — the blocker the plan never anticipated: AwesomeAssertions 7 → 9.5.0.**
  Every one of the 12 inherited contract facts failed with
  `FileNotFoundException: Could not load file or assembly 'AwesomeAssertions, Version=9.5.0.0'` while the two
  Daedalus-authored facts passed — the tell that the fault was in the *inherited* assembly, not in the store.
  **`Thalos.NET.Testing` 0.3.0 depends on AwesomeAssertions 9.5.0** (Plan A Task 1's major bump), and Daedalus
  pinned **7.0.0**; under CPM the explicit pin wins and downgrades the transitive dependency, so the contract
  base class could not load at runtime. This is a consequence of consuming 0.3.0 that **Plan B's §0.2 does not
  mention** — it only anticipated `Npgsql` floors.

  The migration mirrors Plan A Task 1 exactly: bump the pin, then rename the namespace `FluentAssertions` →
  `AwesomeAssertions` in the seven `tests/*/GlobalUsings.cs` files and one stray `using` in
  `GitChangeApplierTests`. **Exactly one real API break across ~900 tests**: `BeLessOrEqualTo` is
  `BeLessThanOrEqualTo` in v9 (`WorkspaceOrchestratorTests.cs:345`). Full unit filter green afterwards at
  **899**, 0 warnings — so Plan A's "only the namespace rename, no behavioural change" holds for Daedalus too,
  with that one rename on top.

- **Task 8 (2026-08-19) — two analyzer traps, no pragmas.** `S3267` rejected the missing-root `foreach`
  (rewritten as `FirstOrDefault`), and `S4144` caught the new theory being byte-identical to
  `Out_of_range_memory_settings_fail_fast_naming_the_key`. The skills theory now asserts the **section name**
  as well as the member, which is a genuinely stronger assertion: the weaker form would have passed on a
  message naming `Thalos:Memory`. Application 368 → **375**.

  **Known-red window, by design:** with `DbSet<Skill>` in the model and no migration yet, the three
  `AddAgentMemoriesMigrationTests` fail with EF 10's `PendingModelChangesWarning` (§0.1 documents exactly this).
  Integration therefore sits at **354/357** until Task 7 scaffolds `AddSkills`. The 14 skill-store facts are green.

- **Task 7 (2026-08-19) — the scaffolder produced exactly the predicted shape; nothing to reconcile.**
  `20260819152830_AddSkills`. The plan's "Expected `Up`" block matches the generated file column for column,
  including `Tags` as `List<string>`/`text[]` and `Name` as the `character varying(64)` primary key. **No
  `CA1861` pragma was needed** — that rule fires on a `new[]` column array and a single-column index does not
  emit one, exactly as the plan predicted. The scaffolder *did* write a UTF-8 BOM (`AddAgentMemories` has
  none), so it was stripped to match the repo convention. No `MigrationsModelDiffer` `NullReferenceException`:
  1.3 removes no mapped type, so the both-directions diff had nothing historical to re-create.

  Both facts green first run, including the rollback past `AddSkills` to `_AddAgentSessions` and forward again.
  **The red window from Task 5 is closed**: integration goes 354/357 → **359/359** (343 at the 1.2 baseline,
  +14 skill store, +2 migration), build 0 warnings.

- **Task 9 (2026-08-19) — the `TimeProvider` worry from Task 6 was a non-issue, and one plan test asserted the
  wrong thing.**

  **`TimeProvider` needs no Daedalus registration.** Task 6's amendment flagged that
  `UseSkillStore<PostgresSkillStore>()` now needs one resolvable from DI. It already is:
  `SkillThalosBuilderExtensions.cs:93` calls `services.TryAddSingleton(TimeProvider.System)` inside `UseSkills`,
  and `ThalosServiceCollectionExtensions.cs:19` does the same for the core. Nothing was added — checked rather
  than assumed, because the failure would only have appeared at first resolve.

  **`SkillOptions.Roots` is `{ get; set; }`**, so per the plan's own note the delegate assigns
  `o.Roots = [.. resolvedRoots]` instead of clear-and-fill. The assignment happens *after* `section.Bind(o)`
  precisely because the binder appends to a pre-populated list.

  **The plan's test had a silent assertion bug.**
  `options.Roots.Should().Equal(root, "roots reach Thalos already resolved to absolute paths")` reads like an
  assertion with a reason, but `Equal` is a `params` overload — the reason string is consumed as a **second
  expected element**, so it asserted two roots and failed against the correct single-root result. Corrected to
  `Equal([root], because)`, with a comment, since the wrong form fails in a way that looks like a product bug.

  **Doc-comment placement trap worth knowing:** inserting the `ConfigureSkills` helper immediately above the
  line `private static void ConfigureMemory(` puts it *between* `ConfigureMemory`'s XML doc and its signature,
  so that doc silently re-attaches to `ConfigureSkills` and `CS1734` fires on the now-orphaned
  `<paramref name="ensureSchema"/>`. Insert above the doc block, not above the signature.

  Application 375 → **381** (+6 facts), unit total **905**, build 0 warnings.

---

## Task 1: Branch and pin Thalos.NET 0.3.0 (G1)

**Files:** modify `Directory.Packages.props`, `src/Daedalus.Agents/Daedalus.Agents.csproj`; possibly `nuget.config` (fallback only).

**Step 1:** `git switch -c feature/thalos-skills`

**Step 2:** Decide the pin per §0.2. Preferred (0.3.0 published):

```powershell
dotnet package search Thalos.NET.Skills --exact-match --format json | ConvertFrom-Json | % { $_.searchResult.packages.version }
```

`Directory.Packages.props` — replace the Thalos block (nine ids, all `0.3.0`):

```xml
    <!-- Thalos.NET (nuget.org) -->
    <PackageVersion Include="Thalos.NET" Version="0.3.0" />
    <PackageVersion Include="Thalos.NET.Abstractions" Version="0.3.0" />
    <PackageVersion Include="Thalos.NET.Testing" Version="0.3.0" />
    <PackageVersion Include="Thalos.NET.Mcp" Version="0.3.0" />
    <PackageVersion Include="Thalos.NET.Anthropic" Version="0.3.0" />
    <PackageVersion Include="Thalos.NET.Sentinel" Version="0.3.0" />
    <PackageVersion Include="Thalos.NET.Memory" Version="0.3.0" />
    <PackageVersion Include="Thalos.NET.Memory.RagNet" Version="0.3.0" />
    <PackageVersion Include="Thalos.NET.Skills" Version="0.3.0" />
```

**Step 3:** `src/Daedalus.Agents/Daedalus.Agents.csproj`, after `Thalos.NET.Memory.RagNet`:

```xml
        <PackageReference Include="Thalos.NET.Skills" />
```

**Step 4:**

```powershell
dotnet restore --force-evaluate
dotnet build --nologo
```

Expect **one** failing test class, not a build error: the pin bump adds the four `Skill*` `AgentErrorCode` values and `AgentErrorResultsTests` pins the count at 18. Confirm that is the only breakage:

```powershell
dotnet test --nologo --filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"
```

Expected: `Every_AgentErrorCode_value_has_an_explicit_mapping_test` fails with `Expected collection to contain 18 item(s), but found 22`. If anything *else* fails, 0.3.0 changed an API this repo already uses — record it in §0.8 and reconcile in Task 2 before continuing.

**Step 5:** Commit (the suite is red on that one guard until Task 3 — that is intentional and matches 1.2):

```
build: pin Thalos.NET 0.3.0 and reference Thalos.NET.Skills from Daedalus.Agents

Nine packages now; Thalos.NET.Skills joins the eight of 0.2.0. The AgentErrorCode
guard test in AgentErrorResultsTests goes red on the four new Skill* codes and is
fixed in the next commit, which adds their status arms.
```

---

## Task 2: API reconciliation against the real package (G1)

**Files:** none in `src/`; this task updates **§0.5 and §0.8 of this plan**.

**Step 1 — dump the real surface** (adjust `$v` if you took the local-pack path):

```powershell
$v = "0.3.0"
$root = "$env:USERPROFILE\.nuget\packages"
Select-String -Path "$root\thalos.net.skills\$v\lib\net10.0\Thalos.NET.Skills.xml" -Pattern 'name="[TMPF]:' |
    ForEach-Object { $_.Matches[0].Value } | Sort-Object -Unique
Select-String -Path "$root\thalos.net.abstractions\$v\lib\net10.0\Thalos.NET.Abstractions.xml" -Pattern 'Skill'
Select-String -Path "$root\thalos.net.testing\$v\lib\net10.0\Thalos.NET.Testing.xml" -Pattern 'SkillStoreContractTests'
```

**Step 2 — cross-check the sources** at `C:\Projects\Prive\Thalos.NET` (note the HEAD sha in the amendment) for anything the XML docs leave implicit: default values, `SkillQuery` tag semantics (AND vs ANY), whether `SkillName` normalises case, `SkillDocument` member optionality, `SkillOptions` nesting (`Catalogue:MaxChars`, `Search:TopK/MinScore`), and the `SkillCatalogueFailedEvent` constructor.

**Step 3 — confirm these seven, explicitly**, because later tasks branch on them:

1. `Thalos.Skills` is the namespace for `SkillName`/`SkillDocument`/`ISkillStore`/`SkillQuery`/`SkillOptions`; `AgentErrorCode.Skill*`, the `AgentError.Skill*` factories and `SkillCatalogueFailedEvent` live in `Thalos`.
2. `ThalosBuilder.UseSkills(Action<SkillOptions>?)` and `UseSkillStore<T>()` exist and are chainable (which extension class they live in).
3. `SkillOptions.Roots` is settable (`IList<string>`/`string[]`) — Task 9 assigns resolved absolute paths onto it.
4. `SkillSyncService` runs as `IHostedLifecycleService.StartingAsync` (Task 11's startup test depends on `host.StartAsync()` triggering it) and **a bad file is logged and skipped, never fatal**.
5. `AgentDefinition.Skills` exists, its type, and its default when unset (`[]` or `["*"]`) — Task 8's mapping mirrors it.
6. `SkillStoreContractTests`' factory signature and its fact count.
7. **The limits the contract actually round-trips**: description length, body length, tag count/length, source-path length, hash length. Deviation §0.6-7 says the contract wins.

**Step 4:** Apply every difference to §0.5 of this plan *and* to the code snippets in Tasks 4, 6, 8, 9, 13 **before** those tasks run. Keep a substitution list in the commit body.

**Step 5:** No test run (nothing changed in `src/`). Commit:

```
docs(skills): reconcile the plan against the real Thalos.NET.Skills 0.3.0 surface

Dumped from the package XML docs and cross-checked against Thalos.NET HEAD <sha>.
Substitutions applied to the plan: <one per line, or "none">.
```

---

## Task 3: `AgentErrorResults` Skill status arms + guard test (G1)

**Files:** modify `src/Daedalus.Api/Agents/AgentErrorResults.cs`, `tests/Daedalus.Tests.Unit/Controllers/AgentErrorResultsTests.cs`.

Mirrors the memory arms one-for-one: validation 400, not-found 404, store-failed 502, search-unavailable 503.

**Step 1: the failing test is already failing** (Task 1). Extend it first — add four `InlineData` rows and bump the count:

```csharp
    [InlineData(AgentErrorCode.SkillValidationFailed, StatusCodes.Status400BadRequest)]
    [InlineData(AgentErrorCode.SkillNotFound, StatusCodes.Status404NotFound)]
    [InlineData(AgentErrorCode.SkillStoreFailed, StatusCodes.Status502BadGateway)]
    [InlineData(AgentErrorCode.SkillSearchUnavailable, StatusCodes.Status503ServiceUnavailable)]
    public void ToStatusCode_maps_every_code(AgentErrorCode code, int expected)
```

```csharp
    [Fact]
    public void Every_AgentErrorCode_value_has_an_explicit_mapping_test()
    {
        // Guards the InlineData list above against Thalos adding codes: new values fall through to 502 silently otherwise.
        // 0.2.0 added the six Memory* codes; 0.3.0 added the four Skill* codes.
        Enum.GetValues<AgentErrorCode>().Should().HaveCount(22);
    }
```

Run: `dotnet test tests/Daedalus.Tests.Unit --nologo --filter "FullyQualifiedName~AgentErrorResultsTests"` → the count fact passes, three of the four new rows fail (everything falls through to 502 today; `SkillStoreFailed` passes by accident).

**Step 2: implement.** In `ToStatusCode`, after the `Memory*` arms and before the `_ =>` catch-all:

```csharp
        AgentErrorCode.SkillValidationFailed => StatusCodes.Status400BadRequest,
        AgentErrorCode.SkillNotFound => StatusCodes.Status404NotFound,
        AgentErrorCode.SkillSearchUnavailable => StatusCodes.Status503ServiceUnavailable,
        AgentErrorCode.SkillStoreFailed => StatusCodes.Status502BadGateway,
```

Update the type's summary comment to mention skills alongside memory if it enumerates them.

**Step 3:** `dotnet test --nologo --filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"` → all green, the suite is back to zero failures.

**Step 4:** Commit:

```
feat(skills): map the four Skill* agent error codes to HTTP statuses

Mirrors the Memory* arms: validation 400, not found 404, store failed 502, search
unavailable 503. The exhaustiveness guard in AgentErrorResultsTests goes 18 to 22,
which is what caught the new codes when the Thalos.NET pin moved to 0.3.0.
```

---

## Task 4: Domain — the `Skill` aggregate (G2)

**Files:** create `src/Daedalus.Domain/Entities/Skill.cs`; create `tests/Daedalus.Tests.Unit.Domain/Entities/SkillTests.cs`.

**Step 1: failing tests.**

```csharp
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

public sealed class SkillTests
{
    private static readonly DateTime _now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private static Result<Skill> Create(
        string name = "daedalus-migrations",
        string description = "How to add and apply an EF Core migration in this repo.",
        string body = "# Adding a migration\n1. ...",
        IEnumerable<string>? tags = null,
        string sourcePath = "skills/daedalus-migrations/SKILL.md",
        string contentHash = "0123456789abcdef",
        bool isActive = true) =>
        Skill.Create(name, description, body, tags ?? ["dotnet", "EF"], sourcePath, contentHash, isActive, _now);

    [Fact]
    public void Create_keeps_the_document_verbatim_and_normalises_tags()
    {
        var skill = Create(tags: ["  DotNet ", "ef", "EF", "", "   "]).Value;

        skill.Id.Should().Be("daedalus-migrations");
        skill.Description.Should().Be("How to add and apply an EF Core migration in this repo.");
        skill.Body.Should().Be("# Adding a migration\n1. ...");
        skill.Tags.Should().Equal("dotnet", "ef");
        skill.SourcePath.Should().Be("skills/daedalus-migrations/SKILL.md");
        skill.ContentHash.Should().Be("0123456789abcdef");
        skill.IsActive.Should().BeTrue();
        skill.UpdatedAt.Should().Be(_now);
    }

    [Theory]
    [InlineData("daedalus-migrations")]
    [InlineData("a")]
    [InlineData("a_b-c9")]
    public void Create_accepts_valid_names(string name) => Create(name).IsSuccess.Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Daedalus")]        // upper case
    [InlineData("9lives")]          // leading digit
    [InlineData("-leading")]        // leading dash
    [InlineData("has space")]
    [InlineData("has.dot")]
    public void Create_rejects_invalid_names(string name) =>
        Create(name).Error.Should().Contain("Name must match");

    [Fact]
    public void Name_may_be_64_chars_but_not_65()
    {
        Create("a" + new string('b', 63)).IsSuccess.Should().BeTrue();
        Create("a" + new string('b', 64)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Description_is_required_and_capped()
    {
        Create(description: "  ").Error.Should().Contain("Description is required");
        Create(description: new string('d', Skill.MaxDescriptionLength)).IsSuccess.Should().BeTrue();
        Create(description: new string('d', Skill.MaxDescriptionLength + 1)).Error.Should().Contain("300");
    }

    [Fact]
    public void Body_is_required_and_capped_at_64_kb()
    {
        Create(body: "   ").Error.Should().Contain("Body is required");
        Create(body: new string('x', Skill.MaxBodyLength)).IsSuccess.Should().BeTrue();
        Create(body: new string('x', Skill.MaxBodyLength + 1)).Error.Should().Contain("65536");
    }

    [Fact]
    public void Body_keeps_leading_and_trailing_whitespace()
    {
        // What the model reads is byte-for-byte what is in git (design section 3), so nothing is trimmed.
        Create(body: "\n# Title\n\n").Value.Body.Should().Be("\n# Title\n\n");
    }

    [Fact]
    public void Tags_are_capped_in_count_and_length()
    {
        Create(tags: Enumerable.Range(0, Skill.MaxTags + 1).Select(i => $"t{i}")).Error.Should().Contain("At most");
        Create(tags: [new string('t', Skill.MaxTagLength + 1)]).Error.Should().Contain("32");
    }

    [Fact]
    public void SourcePath_and_content_hash_are_required_and_capped()
    {
        Create(sourcePath: " ").Error.Should().Contain("Source path is required");
        Create(sourcePath: new string('p', Skill.MaxSourcePathLength + 1)).Error.Should().Contain("1024");
        Create(contentHash: " ").Error.Should().Contain("Content hash is required");
        Create(contentHash: new string('h', Skill.MaxContentHashLength + 1)).Error.Should().Contain("128");
    }

    [Fact]
    public void Update_replaces_every_field_and_bumps_the_timestamp()
    {
        var skill = Create().Value;
        var later = _now.AddHours(1);

        var result = skill.Update("New description.", "new body", ["x"], "skills/other/SKILL.md", "deadbeef", isActive: true, later);

        result.IsSuccess.Should().BeTrue();
        skill.Description.Should().Be("New description.");
        skill.Body.Should().Be("new body");
        skill.Tags.Should().Equal("x");
        skill.SourcePath.Should().Be("skills/other/SKILL.md");
        skill.ContentHash.Should().Be("deadbeef");
        skill.UpdatedAt.Should().Be(later);
    }

    [Fact]
    public void Update_validates_before_mutating_anything()
    {
        var skill = Create().Value;

        var result = skill.Update("ok", new string('x', Skill.MaxBodyLength + 1), null, "p", "h", true, _now.AddHours(1));

        result.IsFailure.Should().BeTrue();
        skill.Body.Should().Be("# Adding a migration\n1. ...");
        skill.Description.Should().Be("How to add and apply an EF Core migration in this repo.");
        skill.UpdatedAt.Should().Be(_now);
    }

    [Fact]
    public void An_inactive_skill_round_trips()
    {
        Create(isActive: false).Value.IsActive.Should().BeFalse();
    }
}
```

Run: `dotnet test tests/Daedalus.Tests.Unit.Domain --nologo --filter "FullyQualifiedName~SkillTests"` → does not compile (the type does not exist). That is the red step.

**Step 2: implement.**

```csharp
using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     One agent skill: a named procedure document authored in git and synced into the database (Thalos
///     <c>SkillDocument</c> persisted by Daedalus). Domain stays framework-free: the id is the skill name, times are UTC.
///     Embeddings are not stored here — the skill index is a rebuildable, in-process cache.
/// </summary>
/// <remarks>
///     Limits mirror the Thalos skill rules (name <c>^[a-z][a-z0-9_-]{0,63}$</c>, description ≤ 300, body ≤ 64 KB,
///     ≤ 10 tags of ≤ 32 chars), so a violation is a validation error, never a database constraint failure. The body is
///     stored <b>verbatim</b> — what the model reads is byte-for-byte what is in git — and only tags are normalised
///     (trimmed, lower-cased, blanks dropped, de-duplicated), like <see cref="AgentMemory"/>.
/// </remarks>
public sealed class Skill : Entity<string>
{
    /// <summary>Maximum length of the name (the primary key): a lowercase identifier <c>^[a-z][a-z0-9_-]{0,63}$</c>.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Maximum length of <see cref="Description"/> — it appears in every catalogue, so it stays short.</summary>
    public const int MaxDescriptionLength = 300;

    /// <summary>Maximum length of <see cref="Body"/>: 64 K UTF-16 units, so one runaway file cannot blow a context window.</summary>
    public const int MaxBodyLength = 64 * 1024;

    /// <summary>Maximum number of <see cref="Tags"/>.</summary>
    public const int MaxTags = 10;

    /// <summary>Maximum length of one tag.</summary>
    public const int MaxTagLength = 32;

    /// <summary>Maximum length of <see cref="SourcePath"/> (repo-relative, used in error messages).</summary>
    public const int MaxSourcePathLength = 1024;

    /// <summary>Maximum length of <see cref="ContentHash"/>; the encoding is the library's business (hex or base64).</summary>
    public const int MaxContentHashLength = 128;

    private readonly List<string> _tags = [];

    /// <summary>Gets the one-line description shown in every catalogue.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Gets the procedure body, verbatim (everything after the file's frontmatter).</summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>Gets the normalised tags (lower-case, distinct, insertion order).</summary>
    public IReadOnlyList<string> Tags => _tags.AsReadOnly();

    /// <summary>Gets the repo-relative path the skill was loaded from.</summary>
    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>Gets the hash of the raw file; an unchanged hash means the sync skips the file entirely.</summary>
    public string ContentHash { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the file still exists on disk. Inactive skills leave the catalogues but keep their row.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>Gets when the skill was last synced from a changed file (UTC).</summary>
    public DateTime UpdatedAt { get; private set; }

    private Skill() { } // EF Core

    /// <summary>Creates a skill from a synced document. Timestamps are supplied by the caller (UTC).</summary>
    /// <returns>A Result containing the new skill or the first validation error.</returns>
    public static Result<Skill> Create(
        string name, string description, string body, IEnumerable<string>? tags,
        string sourcePath, string contentHash, bool isActive, DateTime updatedAt)
    {
        if (!IsValidName(name))
            return Result.Failure<Skill>($"Name must match ^[a-z][a-z0-9_-]{{0,{MaxNameLength - 1}}}$.");

        var fields = ValidateFields(description, body, tags, sourcePath, contentHash);
        if (fields.IsFailure)
            return Result.Failure<Skill>(fields.Error);

        var skill = new Skill
        {
            Id = name,
            Description = description,
            Body = body,
            SourcePath = sourcePath,
            ContentHash = contentHash,
            IsActive = isActive,
            UpdatedAt = updatedAt,
        };
        skill._tags.AddRange(fields.Value);
        return Result.Success(skill);
    }

    /// <summary>
    ///     Replaces the whole document (files are the source of truth, so an upsert is a full replace, not a patch).
    ///     Validation runs before anything is applied, so a failed update leaves the aggregate unchanged.
    /// </summary>
    public Result Update(
        string description, string body, IReadOnlyList<string>? tags,
        string sourcePath, string contentHash, bool isActive, DateTime updatedAt)
    {
        var fields = ValidateFields(description, body, tags, sourcePath, contentHash);
        if (fields.IsFailure)
            return Result.Failure(fields.Error);

        Description = description;
        Body = body;
        _tags.Clear();
        _tags.AddRange(fields.Value);
        SourcePath = sourcePath;
        ContentHash = contentHash;
        IsActive = isActive;
        UpdatedAt = updatedAt;
        return Result.Success();
    }

    /// <summary>Same rule as the Thalos skill name: <c>^[a-z][a-z0-9_-]{0,63}$</c>.</summary>
    private static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    private static Result<List<string>> ValidateFields(
        string description, string body, IEnumerable<string>? tags, string sourcePath, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(description))
            return Result.Failure<List<string>>("Description is required.");

        if (description.Length > MaxDescriptionLength)
            return Result.Failure<List<string>>($"Description must be at most {MaxDescriptionLength} characters.");

        if (string.IsNullOrWhiteSpace(body))
            return Result.Failure<List<string>>("Body is required.");

        if (body.Length > MaxBodyLength)
            return Result.Failure<List<string>>($"Body must be at most {MaxBodyLength} characters.");

        if (string.IsNullOrWhiteSpace(sourcePath))
            return Result.Failure<List<string>>("Source path is required.");

        if (sourcePath.Length > MaxSourcePathLength)
            return Result.Failure<List<string>>($"Source path must be at most {MaxSourcePathLength} characters.");

        if (string.IsNullOrWhiteSpace(contentHash))
            return Result.Failure<List<string>>("Content hash is required.");

        return contentHash.Length > MaxContentHashLength
            ? Result.Failure<List<string>>($"Content hash must be at most {MaxContentHashLength} characters.")
            : NormaliseTags(tags);
    }

    private static Result<List<string>> NormaliseTags(IEnumerable<string>? tags)
    {
#pragma warning disable CA1308 // tags are lowercase identifiers by definition, not user-facing text
        var list = (tags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
#pragma warning restore CA1308

        if (list.Count > MaxTags)
            return Result.Failure<List<string>>($"At most {MaxTags} tags are allowed.");

        return list.Exists(t => t.Length > MaxTagLength)
            ? Result.Failure<List<string>>($"Tags must be at most {MaxTagLength} characters.")
            : Result.Success(list);
    }
}
```

**Step 3:** `dotnet test tests/Daedalus.Tests.Unit.Domain --nologo --filter "FullyQualifiedName~SkillTests"` → green. Then the whole unit filter → green, 0 warnings.

**Step 4:** Commit:

```
feat(skills): Skill domain aggregate mirroring the Thalos skill rules

Thalos-free, like AgentMemory: the name is the id and the invariants (name pattern,
description 300, body 64 KB, 10 tags of 32) mirror the library so a bad document is a
validation error rather than a varchar failure. The body is stored verbatim - what the
model reads must be byte-for-byte what is in git.
```

---

## Task 5: EF configuration + `DbSet` (G2)

**Files:** create `src/Daedalus.Infrastructure/Persistence/Configurations/SkillConfiguration.cs`; modify `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs`.

No migration yet — the fixture DB uses `EnsureCreatedAsync`, so Task 6's contract suite runs against this configuration alone. That ordering is deliberate (1.2 did the same): it keeps the store green before the migration is scaffolded, and the migration then has a *known-good* model to diff against.

**Step 1:** `SkillConfiguration.cs`:

```csharp
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the <see cref="Skill"/> aggregate (table <c>Skills</c>, the Thalos skill store).
///     Documents only — embeddings live in the in-process skill index, which is rebuilt from these rows at startup.
/// </summary>
internal sealed class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.ToTable("Skills");

        // The name is the primary key: skill documents carry no surrogate id and names are unique by construction
        // (a duplicate across roots is a load error, never a silent rename).
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasColumnName("Name")
            .IsRequired()
            .HasMaxLength(Skill.MaxNameLength);

        builder.Property(s => s.Description)
            .IsRequired()
            .HasMaxLength(Skill.MaxDescriptionLength);

        // Unbounded text column; the aggregate enforces MaxBodyLength so violations are validation errors.
        builder.Property(s => s.Body)
            .IsRequired()
            .HasColumnType("text");

        // Backing-field mapping: the private List<string> becomes a text[] column named Tags.
        builder.Property("_tags")
            .HasColumnName("Tags")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(s => s.Tags);

        builder.Property(s => s.SourcePath)
            .IsRequired()
            .HasMaxLength(Skill.MaxSourcePathLength);

        builder.Property(s => s.ContentHash)
            .IsRequired()
            .HasMaxLength(Skill.MaxContentHashLength);

        builder.Property(s => s.IsActive).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        // Every catalogue and list query filters on IsActive; the table is repo-sized, so this is the only index it needs.
        builder.HasIndex(s => s.IsActive)
            .HasDatabaseName("IX_Skill_IsActive");
    }
}
```

**Step 2:** `ApplicationDbContext`, after line 30:

```csharp
    public DbSet<Skill> Skills => Set<Skill>();
```

**Step 3:** `dotnet build --nologo` → 0 warnings; unit filter → green (nothing consumes it yet).

**Step 4:** Commit:

```
feat(skills): map the Skill aggregate to the Skills table

Name is the primary key, the body is an unbounded text column (the aggregate enforces
64 KB) and tags map through the backing field to text[], like AgentMemories. No
migration yet: the integration fixture uses EnsureCreated, so the store contract suite
can go green against the model before the migration is scaffolded.
```

---

## Task 6: `PostgresSkillStore` + contract tests (G2)

**Files:** create `src/Daedalus.Agents/Skills/PostgresSkillStore.cs`; create `tests/Daedalus.Tests.Integration/Agents/PostgresSkillStoreTests.cs`.

**Step 1: failing test** — run the library's contract suite against Postgres, plus two Daedalus-specific facts (the same shape as `PostgresMemoryStoreTests`):

```csharp
using Daedalus.Agents.Skills;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Thalos.Skills;
using Thalos.Testing;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Runs Thalos.NET's <see cref="ISkillStore"/> contract suite against the Postgres-backed store. Tests in the
///     database collection run sequentially; the database is reset before each test and the base class creates one
///     store per test through <see cref="CreateStoreAsync"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresSkillStoreTests(PostgresFixture fixture) : SkillStoreContractTests, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // Skill documents carry their own UpdatedAt (they are synced from files), so the store needs no clock.
    protected override ValueTask<ISkillStore> CreateStoreAsync(TimeProvider clock) =>
        new(new PostgresSkillStore(new FixtureDbContextFactory(fixture)));

    /// <summary>Daedalus-specific: the tag filter is AND over normalised tags, translated to per-tag <c>= ANY("Tags")</c>.</summary>
    [Fact]
    public async Task List_tag_filter_requires_every_tag()
    {
        var store = await CreateStoreAsync(TimeProvider.System);
        await store.UpsertAsync(NewDocument("xy", tags: ["x", "y"]), CancellationToken.None);
        await store.UpsertAsync(NewDocument("x-only", tags: ["x"]), CancellationToken.None);
        await store.UpsertAsync(NewDocument("none", tags: []), CancellationToken.None);

        (await store.ListAsync(new SkillQuery { Tags = ["x"] }, CancellationToken.None)).Value
            .Select(s => s.Name.Value).Should().BeEquivalentTo(["xy", "x-only"]);
        (await store.ListAsync(new SkillQuery { Tags = ["x", "y"] }, CancellationToken.None)).Value
            .Select(s => s.Name.Value).Should().BeEquivalentTo(["xy"]);
        (await store.ListAsync(new SkillQuery { Tags = [" "] }, CancellationToken.None)).Value
            .Should().BeEmpty("a blank query tag matches nothing");
    }

    /// <summary>
    ///     Daedalus-specific: a document that violates the aggregate's rules comes back as SkillValidationFailed, not as
    ///     a database constraint failure — the whole point of mirroring the Thalos limits in the Domain layer.
    /// </summary>
    [Fact]
    public async Task Upsert_of_an_over_size_body_is_a_validation_error()
    {
        var store = await CreateStoreAsync(TimeProvider.System);

        var result = await store.UpsertAsync(NewDocument("huge", body: new string('x', (64 * 1024) + 1)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(Thalos.AgentErrorCode.SkillValidationFailed);
    }

    private static SkillDocument NewDocument(string name, string? body = null, IReadOnlyList<string>? tags = null) => new()
    {
        Name = new SkillName(name),
        Description = $"Description of {name}.",
        Body = body ?? $"# {name}\nstep one",
        Tags = tags ?? [],
        SourcePath = $"skills/{name}/SKILL.md",
        ContentHash = name.GetHashCode(StringComparison.Ordinal).ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
        UpdatedAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
    };

    /// <summary>The store disposes every context it creates, so the factory needs no tracking.</summary>
    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
```

> If the contract's factory is `CreateStoreAsync()` without a clock, or `NewDocument`-style helpers already exist on the base class, use the base class' helpers — mirror `PostgresMemoryStoreTests`, which reuses `NewClock()`/`NewRecord()` from `MemoryStoreContractTests`.

Run: `dotnet test tests/Daedalus.Tests.Integration --nologo --filter "FullyQualifiedName~PostgresSkillStoreTests"` → does not compile. Red.

**Step 2: implement.**

```csharp
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Skills;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Skills;

/// <summary>
///     Thalos skill store over <see cref="ApplicationDbContext"/> (table <c>Skills</c>). Documents only — the skill
///     index is an in-process, rebuildable cache. Fresh short-lived DbContext per call (the store is a singleton), same
///     patterns as <see cref="Memory.PostgresMemoryStore"/>.
/// </summary>
/// <remarks>
///     Files are the source of truth, so <see cref="UpsertAsync"/> is a <b>full replace</b> keyed on the name rather
///     than a patch, and <see cref="DeactivateMissingAsync"/> flips rows whose file disappeared instead of deleting
///     them (history and references stay resolvable). Timestamps come from the document, not from a clock: the sync
///     decides when a skill changed. Validation failures come back as <see cref="AgentError"/>s; Npgsql/connection
///     exceptions propagate, the session-store policy.
/// </remarks>
public sealed class PostgresSkillStore(IDbContextFactory<ApplicationDbContext> contextFactory) : ISkillStore
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async ValueTask<Result<SkillDocument, AgentError>> UpsertAsync(SkillDocument skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var name = skill.Name.Value;
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Skills.FirstOrDefaultAsync(s => s.Id == name, ct).ConfigureAwait(false);

        if (row is null)
        {
            var created = Skill.Create(
                name, skill.Description, skill.Body, skill.Tags, skill.SourcePath, skill.ContentHash,
                skill.IsActive, skill.UpdatedAt.UtcDateTime);
            if (created.IsFailure)
            {
                return Result<SkillDocument, AgentError>.Failure(AgentError.SkillValidationFailed(created.Error));
            }

            db.Skills.Add(created.Value);
            row = created.Value;
        }
        else
        {
            var applied = row.Update(
                skill.Description, skill.Body, skill.Tags, skill.SourcePath, skill.ContentHash,
                skill.IsActive, skill.UpdatedAt.UtcDateTime);
            if (applied.IsFailure)
            {
                return Result<SkillDocument, AgentError>.Failure(AgentError.SkillValidationFailed(applied.Error));
            }
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            return Result<SkillDocument, AgentError>.Failure(
                AgentError.SkillStoreFailed("Could not store the skill.", ex.GetType().Name));
        }

        return Result<SkillDocument, AgentError>.Success(ToDocument(row));
    }

    /// <inheritdoc />
    public async ValueTask<Result<SkillDocument, AgentError>> GetAsync(SkillName name, CancellationToken ct)
    {
        var key = name.Value;
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Id == key, ct).ConfigureAwait(false);

        return row is null
            ? Result<SkillDocument, AgentError>.Failure(AgentError.SkillNotFound(name))
            : Result<SkillDocument, AgentError>.Success(ToDocument(row));
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<SkillDocument>, AgentError>> ListAsync(SkillQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Skills.AsNoTracking().AsQueryable();

        if (query.Names is { Count: > 0 })
        {
            var names = query.Names.Select(n => n.Value).ToList();
            q = q.Where(s => names.Contains(s.Id));
        }

        if (query.Tags is { Count: > 0 })
        {
            // Every listed tag must be present (AND), normalised like stored tags. A blank query tag matches nothing.
            foreach (var raw in query.Tags)
            {
                var tag = NormaliseTag(raw);
                if (string.IsNullOrEmpty(tag))
                {
                    q = q.Where(s => false);
                    break;
                }

                q = q.Where(s => EF.Property<List<string>>(s, "_tags").Contains(tag)); // Npgsql: @tag = ANY("Tags")
            }
        }

        if (!query.IncludeInactive)
        {
            q = q.Where(s => s.IsActive);
        }

        // Sorted by name: the catalogue is rendered in this order (design section 5).
        var rows = await q.OrderBy(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<SkillDocument>, AgentError>.Success(rows.ConvertAll(ToDocument));
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<SkillName> seen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seen);

        // Rows survive so history and references stay resolvable; UpdatedAt is not bumped, because nothing about the
        // document changed - the file simply stopped existing.
        var names = seen.Select(n => n.Value).Distinct(StringComparer.Ordinal).ToList();
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Skills
            .Where(s => s.IsActive && !names.Contains(s.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.IsActive, false), ct)
            .ConfigureAwait(false);

        return UnitResult<AgentError>.Success();
    }

#pragma warning disable CA1308 // tags are lowercase identifiers by definition, not user-facing text
    private static string? NormaliseTag(string? tag) => tag?.Trim().ToLowerInvariant();
#pragma warning restore CA1308

    private static SkillDocument ToDocument(Skill s) => new()
    {
        Name = new SkillName(s.Id),
        Description = s.Description,
        Body = s.Body,
        Tags = s.Tags.ToList(),
        SourcePath = s.SourcePath,
        ContentHash = s.ContentHash,
        IsActive = s.IsActive,
        UpdatedAt = new DateTimeOffset(s.UpdatedAt, TimeSpan.Zero),
    };
}
```

**Step 3:** `dotnet test tests/Daedalus.Tests.Integration --nologo --filter "FullyQualifiedName~PostgresSkillStoreTests"` → green (record the contract fact count in §0.8). Then the full integration filter → green.

**Watch for** (these are the 1.2 failure modes, in order of likelihood):
- **`ExecuteUpdateAsync` inside `DeactivateMissingAsync` when `seen` is empty** — that deactivates *everything*, which is correct (no files on disk) but worth a contract check.
- **`SkillQuery.Tags` semantics** — if the contract expects ANY rather than AND, change the loop to a single `Where` with `Overlaps`/`Any` and record the deviation.
- **Ordering** — if the contract pins insertion order rather than name order, drop the `OrderBy` and let the catalogue sort. The contract wins.

**Step 4:** Commit:

```
feat(skills): PostgresSkillStore passing the Thalos skill store contract

Upsert is a full replace keyed on the name (files are the source of truth, not a patch
stream) and DeactivateMissing flips a flag rather than deleting, so a removed procedure
leaves the catalogues but keeps its row. Runs the library contract suite plus two
Daedalus facts on Testcontainers: AND tag filtering and over-size bodies surfacing as
SkillValidationFailed rather than a database error.
```

---

## Task 7: Migration `AddSkills` + migration test (G3)

**Files:** create `src/Daedalus.Infrastructure/Migrations/<timestamp>_AddSkills.cs` (+ `.Designer.cs`, + snapshot changes); create `tests/Daedalus.Tests.Integration/Migrations/AddSkillsMigrationTests.cs`.

**Step 1: scaffold.**

```powershell
dotnet ef migrations add AddSkills --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations
```

**1.2 lessons that apply here:**
- The scaffolder **diffs both directions**. This migration only *adds* a table, so the down direction is a plain `DropTable` and no historical mapping is re-created — a `NullReferenceException` in `MigrationsModelDiffer.Initialize` would mean the model still references a store type that no longer exists. Nothing in 1.3 removes a type, so this should not happen; if it does, stop and report.
- **`Down` must leave a runnable chain.** `AddSkills.Down` drops only `Skills`, which its predecessor (`AddAgentMemories`) knows nothing about, so the chain is intact by construction — but the test below still proves it, because that failure mode is silent and destructive.
- Add the `#pragma warning disable CA1861` header only if the scaffolder emitted a `new[]` column array (a single-column index does not). Strip any BOM (`dotnet format` CHARSET).

**Step 2: annotate the generated file.** Add the class and `Down` doc comments — every migration in this repo carries them:

```csharp
    /// <summary>
    ///     Creates <c>Skills</c>, the Thalos skill store: one row per procedure document synced from the repo's
    ///     <c>skills/</c> folder. Documents only — the skill index is in-process and rebuilt from these rows at startup,
    ///     so nothing here needs the <c>vector</c> extension.
    /// </summary>
    public partial class AddSkills : Migration
```

```csharp
        /// <summary>
        ///     Drops <c>Skills</c>. Not destructive in practice: files are the source of truth, so the next startup
        ///     re-syncs every skill from disk. What is lost is the deactivation history of skills whose files are gone.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
```

Expected `Up` (verify the scaffolder produced exactly this shape):

```csharp
            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Skill_IsActive",
                table: "Skills",
                column: "IsActive");
```

**Step 3: the test.**

```csharp
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Runs the real migration chain on a throwaway database: up to the migration before <c>AddSkills</c>, then to
///     latest, then asserts the table exists and round-trips a document — and that rolling back past it leaves a chain
///     the predecessors' <c>Down</c> methods can still run (the lesson AddAgentMemories learned the hard way).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddSkillsMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migration_creates_the_skills_table_and_round_trips_a_document()
    {
        await RunMigrationAsync(async db =>
        {
            var skill = Skill.Create(
                "daedalus-migrations", "How to add and apply an EF Core migration in this repo.",
                "# Adding a migration\n1. ...", ["dotnet", "ef"],
                "skills/daedalus-migrations/SKILL.md", "abc123", isActive: true,
                new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc)).Value;

            db.Skills.Add(skill);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var loaded = await db.Skills.AsNoTracking().SingleAsync();
            loaded.Id.Should().Be("daedalus-migrations");
            loaded.Body.Should().Be("# Adding a migration\n1. ...");
            loaded.Tags.Should().Equal("dotnet", "ef");
            loaded.IsActive.Should().BeTrue();

            var indexExists = await db.Database.SqlQuery<bool>(
                $"""SELECT to_regclass('"IX_Skill_IsActive"') IS NOT NULL AS "Value" """).SingleAsync();
            indexExists.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain()
    {
        await RunMigrationAsync(async db =>
        {
            var target = db.Database.GetMigrations().Single(m => m.EndsWith("_AddAgentSessions", StringComparison.Ordinal));

            await db.Database.MigrateAsync(target);

            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().NotContain(m => m.EndsWith("_AddSkills", StringComparison.Ordinal));
            applied.Should().NotContain(m => m.EndsWith("_AddAgentMemories", StringComparison.Ordinal));

            // And forward again, so the chain is runnable in both directions.
            await db.Database.MigrateAsync();
            (await db.Skills.CountAsync()).Should().Be(0);
        });
    }

    /// <summary>Creates a throwaway database, migrates to the predecessor of <c>AddSkills</c>, then to latest, then asserts.</summary>
    private async Task RunMigrationAsync(Func<ApplicationDbContext, Task> assert)
    {
        var dbName = $"migrate_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddSkills", StringComparison.Ordinal));
            index.Should().BeGreaterThan(0);

            // AddAgentMemories' Down needs the vector extension for its ALTER TABLE; Rag.NET installs it in the
            // fixture database, so install it here too before the chain runs.
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector");
            await db.Database.MigrateAsync(migrations[index - 1]);
            await db.Database.MigrateAsync();

            await assert(db);
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

**Step 4:** `dotnet test tests/Daedalus.Tests.Integration --nologo --filter "FullyQualifiedName~AddSkillsMigrationTests"` → green. Then the full integration filter.

**Step 5:** Commit:

```
feat(skills): AddSkills migration creating the Skills table

Name is the primary key, body is text, tags are text[] and one index covers IsActive.
The migration test round-trips a document and rolls the chain back past this migration
and forward again - the failure mode AddAgentMemories hit is silent and destructive, so
it stays pinned rather than checked once by hand.
```

---

## Task 8: Skills options + validation (G4)

**Files:** modify `src/Daedalus.Agents/DaedalusAgentsOptions.cs`, `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs` (validation helper only — wiring is Task 9); modify `tests/Daedalus.Tests.Unit.Application/Agents/DaedalusAgentsRegistrationTests.cs`.

**Step 1: failing tests** — append to `DaedalusAgentsRegistrationTests`:

```csharp
    [Theory]
    [InlineData("Thalos:Skills:Catalogue:MaxChars", "0", "MaxChars")]
    [InlineData("Thalos:Skills:Search:TopK", "0", "TopK")]
    [InlineData("Thalos:Skills:Search:MinScore", "1.5", "MinScore")]
    [InlineData("Thalos:Skills:Roots:0", "   ", "Roots")]
    public void Out_of_range_skill_settings_fail_fast_naming_the_key(string key, string value, string expectedInMessage)
    {
        var act = () => Build(Config((key, value)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expectedInMessage}*");
    }

    [Fact]
    public void A_configured_skills_root_that_does_not_exist_fails_fast()
    {
        // An agent that silently lost every procedure looks exactly like a healthy one, so a broken Content copy or a
        // bad path must take the host down at registration rather than at the first turn.
        var missing = Path.Combine(Path.GetTempPath(), $"no-skills-{Guid.NewGuid():N}");

        var act = () => Build(Config(("Thalos:Skills:Roots:0", missing)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
    }

    [Fact]
    public void Skills_are_off_when_no_root_is_configured()
    {
        // The binder appends to pre-populated lists, so the default must be empty: the registration tests run with a
        // content root that has no skills folder, and "nothing configured" is not an error.
        using var sp = Build(Config());

        sp.GetRequiredService<SkillsConfig>().Roots.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_skills_skip_root_validation_entirely()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-skills-{Guid.NewGuid():N}");

        var act = () => Build(Config(("Thalos:Skills:Enabled", "false"), ("Thalos:Skills:Roots:0", missing)));

        act.Should().NotThrow();
    }
```

Run → does not compile (`SkillsConfig` missing). Red.

**Step 2: options.** Append to `DaedalusAgentsOptions.cs` and add the property on `DaedalusAgentsOptions`:

```csharp
    /// <summary>Skill settings (<c>Thalos:Skills</c>): Thalos <c>SkillOptions</c> keys plus the Daedalus root resolution.</summary>
    public SkillsConfig Skills { get; } = new();
```

```csharp
/// <summary>
///     <c>Thalos:Skills</c>. The Thalos <c>SkillOptions</c> members are bound onto Thalos directly from the same
///     section; this class carries the values Daedalus validates and the roots it resolves against the content root.
/// </summary>
public sealed class SkillsConfig
{
    /// <summary>Configuration section name: <c>Thalos:Skills</c>.</summary>
    public const string SectionName = "Thalos:Skills";

    /// <summary>Whether skills (the catalogue and the <c>skills__*</c> tools) are on at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Folders holding <c><name>/SKILL.md</c> documents. Relative paths resolve against the host content root
    ///     (like <c>McpConfigPath</c>). <b>Empty by default</b> — the binder appends to a pre-populated list, so a
    ///     default here could not be overridden, and "no roots configured" must mean "no skills", not an error.
    /// </summary>
    public IList<string> Roots { get; } = [];

    /// <summary>Catalogue settings (<c>Thalos:Skills:Catalogue</c>).</summary>
    public SkillCatalogueConfig Catalogue { get; } = new();

    /// <summary>Search settings (<c>Thalos:Skills:Search</c>).</summary>
    public SkillSearchConfig Search { get; } = new();
}

/// <summary>The always-present catalogue block appended to an agent's instructions.</summary>
public sealed class SkillCatalogueConfig
{
    /// <summary>Character budget for the catalogue; overflow is reported with an explicit "and N more" line, never silently.</summary>
    public int MaxChars { get; set; } = 2000;
}

/// <summary><c>skills__search</c> settings; without an embedding generator search reports unavailable and the catalogue stays authoritative.</summary>
public sealed class SkillSearchConfig
{
    /// <summary>Default number of results.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Minimum cosine score for a result.</summary>
    public double MinScore { get; set; } = 0.6;
}
```

And on `AgentConfig`, next to `Tools`:

```csharp
    /// <summary>
    ///     Glob allow-list over skill names (<c>*</c> for every skill, <c>daedalus-*</c> for a family). Empty → the agent
    ///     gets no skills: procedures are granted explicitly, never by default.
    /// </summary>
    public IList<string> Skills { get; } = [];
```

**Step 3: validation** in `DaedalusAgentsServiceCollectionExtensions.cs`, beside `ValidateMemoryConfig`:

```csharp
    /// <summary>
    ///     Fails fast on the Daedalus-only <c>Thalos:Skills</c> keys (Thalos validates its own <c>SkillOptions</c> on
    ///     start). A configured root that does not exist is fatal on purpose: an agent that silently lost every
    ///     procedure is indistinguishable from a healthy one, and this is what catches a broken content copy.
    /// </summary>
    private static void ValidateSkillsConfig(SkillsConfig config, IReadOnlyList<string> resolvedRoots)
    {
        if (!config.Enabled)
        {
            return;
        }

        for (var i = 0; i < config.Roots.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(config.Roots[i]))
            {
                throw new InvalidOperationException($"{SkillsConfig.SectionName}:Roots[{i}] must not be blank.");
            }
        }

        foreach (var root in resolvedRoots)
        {
            if (!Directory.Exists(root))
            {
                throw new InvalidOperationException(
                    $"{SkillsConfig.SectionName}:Roots contains '{root}', which is not an existing directory. " +
                    "Relative roots resolve against the host content root; check that the skills folder is copied next to the host.");
            }
        }

        if (config.Catalogue.MaxChars <= 0)
        {
            throw new InvalidOperationException(
                $"{SkillsConfig.SectionName}:Catalogue:MaxChars must be greater than 0, but was {config.Catalogue.MaxChars}.");
        }

        if (config.Search.TopK < 1)
        {
            throw new InvalidOperationException(
                $"{SkillsConfig.SectionName}:Search:TopK must be at least 1, but was {config.Search.TopK}.");
        }

        if (config.Search.MinScore is < 0 or > 1 || double.IsNaN(config.Search.MinScore))
        {
            throw new InvalidOperationException(
                $"{SkillsConfig.SectionName}:Search:MinScore must be in [0, 1], but was {config.Search.MinScore.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    /// <summary>Resolves configured skill roots against the content root, like <c>ResolveMcpConfigPath</c>.</summary>
    private static IReadOnlyList<string> ResolveSkillRoots(SkillsConfig config, IHostEnvironment environment) =>
        [.. config.Roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.IsPathRooted(r) ? r : Path.Combine(environment.ContentRootPath, r))];
```

Wire the two calls into `AddDaedalusAgents` next to `ValidateMemoryConfig` (the `UseSkills` call itself is Task 9):

```csharp
        var skillRoots = ResolveSkillRoots(options.Skills, environment);
        ValidateSkillsConfig(options.Skills, skillRoots);
        services.TryAddSingleton(options.Skills);
```

**Step 4:** unit filter → green. **Step 5:** Commit:

```
feat(skills): Thalos:Skills options with fail-fast validation

Roots default to empty (the binder appends to pre-populated lists) and resolve against
the host content root like McpConfigPath. A configured root that does not exist throws
at registration naming the path: an agent that silently lost every procedure looks
exactly like a healthy one, and this is what catches a broken content copy.
```

---

## Task 9: Wire skills into `AddDaedalusAgents` (G4)

**Files:** modify `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs`; modify `tests/Daedalus.Tests.Unit.Application/Agents/DaedalusAgentsRegistrationTests.cs`.

**Step 1: failing tests.**

```csharp
    [Fact]
    public void Skills_are_wired_with_the_postgres_store_when_a_root_is_configured()
    {
        var root = Directory.CreateTempSubdirectory("daedalus-skills-").FullName;
        try
        {
            using var sp = Build(Config(
                ("Thalos:Skills:Roots:0", root),
                ("Thalos:Skills:Catalogue:MaxChars", "1234"),
                ("Thalos:Skills:Search:TopK", "9")));

            sp.GetRequiredService<ISkillStore>().GetType().Name.Should().BeOneOf("PostgresSkillStore", "SkillStoreInstrumented");
            sp.GetRequiredService<PostgresSkillStore>().Should().NotBeNull();

            var options = sp.GetRequiredService<IOptions<SkillOptions>>().Value;
            options.Enabled.Should().BeTrue();
            options.Roots.Should().Equal(root, "roots reach Thalos already resolved to absolute paths");
            options.Catalogue.MaxChars.Should().Be(1234);
            options.Search.TopK.Should().Be(9);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Relative_skill_roots_resolve_against_the_content_root()
    {
        var contentRoot = Directory.CreateTempSubdirectory("daedalus-content-").FullName;
        Directory.CreateDirectory(Path.Combine(contentRoot, "skills"));
        try
        {
            var env = Substitute.For<IHostEnvironment>();
            env.ContentRootPath.Returns(contentRoot);
            env.EnvironmentName.Returns("Development");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
            services.AddDaedalusAgents(Config(("Thalos:Skills:Roots:0", "skills")), env);
            using var sp = services.BuildServiceProvider();

            sp.GetRequiredService<IOptions<SkillOptions>>().Value.Roots
                .Should().Equal(Path.Combine(contentRoot, "skills"));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void Skills_can_be_disabled_from_configuration()
    {
        using var sp = Build(Config(("Thalos:Skills:Enabled", "false")));

        sp.GetRequiredService<SkillsConfig>().Enabled.Should().BeFalse();
        sp.GetRequiredService<IOptions<SkillOptions>>().Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Agent_skill_globs_bind_onto_the_definition()
    {
        using var sp = Build(Config(("Thalos:Agents:0:Skills:0", "daedalus-*"), ("Thalos:Agents:0:Skills:1", "thalos-release")));

        sp.GetRequiredService<IAgentCatalog>().Agents.Single().Skills.Should().Equal("daedalus-*", "thalos-release");
    }

    [Fact]
    public void An_agent_without_skill_globs_gets_none()
    {
        // Procedures are granted explicitly. Defaulting to "*" would hand every agent every procedure the moment one
        // is added to the repo, which is the opposite of the per-agent gate the design asks for.
        using var sp = Build(Config());

        sp.GetRequiredService<IAgentCatalog>().Agents.Single().Skills.Should().BeEmpty();
    }

    [Fact]
    public void AddDaedalusMemory_does_not_register_skills()
    {
        // Skills are API-host only: the Ralph console runs no Thalos agents, so it has nothing to hand a catalogue to.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusMemory(Config());
        using var sp = services.BuildServiceProvider();

        sp.GetService<ISkillStore>().Should().BeNull();
        sp.GetService<PostgresSkillStore>().Should().BeNull();
        sp.GetService<SkillsConfig>().Should().BeNull();
        sp.GetServices<IHostedService>().Should().NotContain(h => h.GetType().Name.Contains("Skill", StringComparison.Ordinal));
    }
```

Run → red.

**Step 2: implement.** Inside `AddDaedalusAgents`' `services.AddThalos(thalos => { … })`, right after the `ConfigureMemory(...)` call:

```csharp
            // Skills are API-host only: the Ralph console runs no Thalos agents (see AddDaedalusMemory).
            ConfigureSkills(thalos, configuration.GetSection(SkillsConfig.SectionName), options.Skills, skillRoots);
```

and the helper beside `ConfigureMemory`:

```csharp
    /// <summary>
    ///     Registers skills on the Thalos builder: <c>Thalos:Skills</c> → <c>SkillOptions</c> with the roots already
    ///     resolved to absolute paths, plus the Postgres-backed store. The sync runs once at host start; a malformed
    ///     document is logged and skipped, but an unreachable store fails start (an agent missing its procedures is
    ///     worse than a host that does not come up).
    /// </summary>
    private static void ConfigureSkills(
        ThalosBuilder thalos,
        IConfigurationSection section,
        SkillsConfig config,
        IReadOnlyList<string> resolvedRoots)
    {
        thalos.UseSkills(o =>
            {
                section.Bind(o); // Enabled, Catalogue:MaxChars, Search:TopK/MinScore straight from Thalos:Skills
                o.Enabled = config.Enabled;
                o.Roots.Clear();
                foreach (var root in resolvedRoots)
                {
                    o.Roots.Add(root); // absolute, resolved against the content root: no CWD surprises in tests or containers
                }
            })
            .UseSkillStore<PostgresSkillStore>();
    }
```

> If `SkillOptions.Roots` is a settable `IList<string>`/`string[]` rather than a read-only collection, assign it (`o.Roots = [.. resolvedRoots];`) — Task 2 settled this.

And in `ToDefinition`:

```csharp
        Skills = [.. agent.Skills],
```

Update the `AddDaedalusAgents` XML doc: mention `Thalos:Skills` in the `configuration` param, add "skills (the catalogue and `skills__*` tools over `PostgresSkillStore`)" to the summary, and add "a `Thalos:Skills` root does not exist" to the `<exception>` list. Add a sentence to `AddDaedalusMemory`'s remarks: *"No skills either: the Ralph worker runs no Thalos agents, so there is no catalogue to build."*

**Step 3:** unit filter → green (record the fact delta). **Step 4:** Commit:

```
feat(skills): wire UseSkills and the Postgres store into AddDaedalusAgents

API host only - the Ralph console runs no Thalos agents, so AddDaedalusMemory registers
no skills and a test pins that. Roots reach Thalos already resolved against the content
root, and an agent's Skills globs map onto AgentDefinition next to Tools, defaulting to
none rather than to everything.
```

---

## Task 10: The `skills/` folder and the two starter skills (G5)

**Files:** create `skills/daedalus-migrations/SKILL.md`, `skills/thalos-release/SKILL.md`; modify `src/Daedalus.Api/Daedalus.Api.csproj`, `.dockerignore`.

Both procedures were executed by hand during this milestone, which is the point: the feature ships used, not theoretical.

**Step 1:** `skills/daedalus-migrations/SKILL.md`:

```markdown
---
name: daedalus-migrations
description: How to add, verify and apply an EF Core migration in the Daedalus repo.
tags: [dotnet, ef, database, daedalus]
---

# Adding an EF Core migration

The model lives in `src/Daedalus.Infrastructure`; the startup project is `src/Daedalus.Api`
(it supplies the connection string and the DI graph the design-time factory needs).

## 1. Change the model first

- Aggregate: `src/Daedalus.Domain/Entities/<Name>.cs`. Domain stays framework-free — no Thalos, no EF
  attributes, invariants enforced in `Create`/`Update` returning `CSharpFunctionalExtensions.Result`.
- Mapping: `src/Daedalus.Infrastructure/Persistence/Configurations/<Name>Configuration.cs`,
  `internal sealed class … : IEntityTypeConfiguration<T>`. It is picked up automatically by
  `ApplyConfigurationsFromAssembly`. Use the aggregate's `Max*Length` constants for `HasMaxLength`, so a
  violation is a validation error and never a varchar failure.
- `DbSet`: add `public DbSet<T> Xs => Set<T>();` to `ApplicationDbContext`.

## 2. Scaffold

```powershell
dotnet ef migrations add <Name> --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations
```

This writes three things: the migration, its `.Designer.cs` and an updated
`ApplicationDbContextModelSnapshot.cs`. Never hand-edit the snapshot to "fix" a diff — EF 10 throws on
`Migrate()` when the model has pending changes that are not in the snapshot, so a hand-patched snapshot
fails at runtime, not at build time.

## 3. Read the generated file before you trust it

- **The scaffolder diffs both directions.** `Down` is generated from the snapshot, so if a mapping was
  removed in the same change (a type, a column type, a provider), the down direction can crash the
  scaffolder with a `NullReferenceException` in `MigrationsModelDiffer` — or, worse, generate a `Down`
  that leaves a schema the *previous* migration's `Down` cannot run against. Walk the chain mentally:
  after this `Down`, can the predecessor's `Down` still run? `AddAgentMemories` had to hand-add an
  `ALTER TABLE … ADD COLUMN IF NOT EXISTS "Embedding" vector(384);` for exactly this reason.
- **Order the operations yourself** when data moves. EF emits `DropTable` first; a copy has to run
  after the `CreateTable` and its indexes, and before the drop.
- Add the `#pragma warning disable CA1861` header if the file contains `new[]` column arrays
  (composite indexes), give the class and `Down` XML doc comments saying what a rollback destroys, and
  strip the BOM (`dotnet format` enforces CHARSET).

## 4. Test it

Migrations get an integration test under `tests/Daedalus.Tests.Integration/Migrations/`. The pattern:
create a throwaway database, `MigrateAsync(<predecessor>)`, seed, `MigrateAsync()`, assert — and a
second fact that rolls the chain back past this migration and forward again. Rollback failures are
silent and destructive, so they are pinned permanently rather than checked once by hand.

```powershell
dotnet test tests/Daedalus.Tests.Integration --nologo --filter "FullyQualifiedName~<Name>MigrationTests"
```

Note the integration fixture itself uses `EnsureCreatedAsync()`, not migrations: a new `DbSet` is
available to store/contract tests before its migration exists.

## 5. Apply

```powershell
dotnet run --project src/Daedalus.Migrations          # what Aspire runs, with .WaitForCompletion(migrations)
dotnet ef database update --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api
```

If the local Postgres volume is stale (collation-version mismatch warnings, tables missing),
`docker volume rm daedalus_postgres_data` and let Aspire recreate it.
```

**Step 2:** `skills/thalos-release/SKILL.md`:

```markdown
---
name: thalos-release
description: How to cut and publish a Thalos.NET release, and how to consume it from Daedalus.
tags: [release, nuget, thalos, ci]
---

# Cutting a Thalos.NET release

Repo: `C:\Projects\Prive\Thalos.NET` (GitHub `MarcelRoozekrans/Thalos.NET`). Full runbook:
`docs/release.md` in that repo. Rules that are easy to get wrong are repeated here.

- **No prereleases on nuget.org.** Only stable `X.Y.Z`, and only from the commit release-please tagged
  `vX.Y.Z`. The `publish-nuget` job refuses everything else.
- **GitVersion** derives the version from git history; `pack-validate` packs on every push and
  rehearses the nuget.org push against a local feed.
- **release-please** proposes releases from conventional commits (manifest mode, manual dispatch only).
- **Pre-1.0 bump rules:** a `feat:` bumps the **patch** (0.1.0 → 0.1.1); only `feat!:`/`BREAKING CHANGE`
  bumps the minor. A deliberate minor therefore needs an empty commit with a `Release-As:` footer.

## Steps

```bash
# 1. Deliberate version (e.g. a minor for a new package). Skip if the commits already imply it.
git commit --allow-empty -m "chore: set the release version" -m "Release-As: 0.3.0"
git push origin main

# 2. Open the release PR: release-please reads the conventional commits since the last release.
gh workflow run release-please.yml --ref main

# 3. Review and merge the "chore(main): release X.Y.Z" PR like any other PR.

# 4. Dispatch again: release-please now creates the GitHub release and the vX.Y.Z tag.
gh workflow run release-please.yml --ref main

# 5. Publish that exact commit. build-test (both OS) and pack-validate gate the push, and
#    publish-nuget refuses unless the checked-out commit is tagged vX.Y.Z.
gh workflow run ci.yml --ref vX.Y.Z -f publish_to_nuget=true
```

`pack-validate` checks the **package list and each package's TFMs**, so a new package must be added to
it in the same release: 0.2.0 shipped eight, 0.3.0 ships nine (`Thalos.NET.Skills` joined). Everything
except `Thalos.NET.Memory.RagNet` (net10.0-only) ships `net8.0` + `net10.0`.

## Consuming it from Daedalus before it is published

```powershell
pwsh C:\Projects\Prive\Thalos.NET\scripts\pack-local.ps1   # packs X.Y.Z-local.<timestamp> into C:\Projects\Prive\.nuget-local
```

Pin that exact version on every `Thalos.NET*` id in `Directory.Packages.props` and add the folder as a
`nuget.config` source with package-source mapping on `Thalos.NET*`. Two rules: **CI cannot see that
folder**, so either do not push, or commit the `.nupkg` files under `packages-local/` and use a relative
path; and the local pin, the source and the folder all go away before the PR is merged.
```

**Step 3:** the copy. `src/Daedalus.Api/Daedalus.Api.csproj`, next to the `.mcp.json` item:

```xml
    <ItemGroup>
        <!-- Agent skills (Thalos:Skills:Roots) are authored at the repo root so git is the source of truth. Copied next
             to the API and, through the project reference, into every test host that calls AddDaedalusAgents - the same
             mechanism as .mcp.json. Content items flow transitively to referencing projects; None items do not. -->
        <Content Include="..\..\skills\**\SKILL.md" Link="skills\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    </ItemGroup>
```

**Step 4:** `.dockerignore`. `*.md` does not cross `/` in Docker's matcher, so `skills/<name>/SKILL.md` already survives — but the image is where this failing silently would hurt most, so make it explicit. Append:

```
# Agent skills are content, not documentation: the API image serves them from its content root.
!skills/
```

**Step 5: verify the copy in all three places** (this is the real deliverable of the task):

```powershell
dotnet build --nologo
Get-ChildItem -Recurse -Filter SKILL.md `
    src\Daedalus.Api\bin\Debug\net10.0\skills, `
    tests\Daedalus.Tests.Unit\bin\Debug\net10.0\skills, `
    tests\Daedalus.Tests.Integration\bin\Debug\net10.0\skills, `
    tests\Daedalus.Tests.Playwright.Browser\bin\Debug\net10.0\skills | Select-Object FullName
```

Expect **two files per directory**. If a test project's output is empty, the transitive content copy did not happen — switch the item to `<None>` + an explicit `<Content Include>` in each test csproj rather than guessing, and record it in §0.8.

Docker (optional but cheap, and it is the one host nothing else covers):

```powershell
docker build -f src/Daedalus.Api/Dockerfile -t daedalus-api:skills-check .
docker run --rm --entrypoint ls daedalus-api:skills-check -R /app/skills
```

**Step 6:** Commit:

```
feat(skills): ship the daedalus-migrations and thalos-release procedures

Two procedures this milestone actually executed by hand: adding an EF migration in this
repo (including the both-directions scaffolder trap and the Down-chain rule) and cutting
a Thalos.NET release. They live at the repo root so git is the source of truth, and a
Content item on Daedalus.Api copies them next to every host - including the three test
projects that boot AddDaedalusAgents, through the project reference, like .mcp.json.
```

---

## Task 11: Startup test — both skills load (G5)

**Files:** create `tests/Daedalus.Tests.Integration/Agents/SkillsStartupTests.cs`; modify `tests/Daedalus.Tests.Unit/Configuration/ApiThalosConfigurationTests.cs`.

**Step 1: the cheap unit fact first** — append to `ApiThalosConfigurationTests` (no Docker, runs in the CI unit job, and is what will actually catch a broken copy):

```csharp
    /// <summary>
    ///     The starter skills flow into this test's output through the Daedalus.Api project reference (a Content item,
    ///     like .mcp.json), which is exactly how they reach the API's content root at runtime. A broken copy would
    ///     otherwise only show up as an agent that quietly has no procedures.
    /// </summary>
    [Fact]
    public void Starter_skills_are_copied_next_to_the_api()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "skills");

        Directory.Exists(root).Should().BeTrue("skills/**/SKILL.md must be a Content item in Daedalus.Api.csproj");
        foreach (var name in new[] { "daedalus-migrations", "thalos-release" })
        {
            var path = Path.Combine(root, name, "SKILL.md");
            File.Exists(path).Should().BeTrue();
            var text = File.ReadAllText(path);
            text.Should().StartWith("---").And.Contain($"name: {name}").And.Contain("description:");
        }
    }

    [Fact]
    public void Appsettings_points_the_skill_roots_at_the_shipped_folder()
    {
        LoadApiConfiguration().GetSection("Thalos:Skills:Roots").Get<string[]>().Should().Equal("skills");
    }
```

**Step 2: the startup test.**

```csharp
using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos.Skills;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Boots a real host with the shipped API configuration and asserts the skill sync ran: both starter procedures are
///     in the database, active, with their bodies verbatim. This is the only test that exercises the whole path —
///     Content copy → content root → resolved root → SkillSyncService → PostgresSkillStore — and it is the path that
///     fails silently if any link breaks.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SkillsStartupTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Host_start_syncs_both_starter_skills_into_postgres()
    {
        using var host = BuildHost();
        await host.StartAsync();
        try
        {
            var store = host.Services.GetRequiredService<ISkillStore>();

            var all = await store.ListAsync(new SkillQuery(), CancellationToken.None);
            all.IsSuccess.Should().BeTrue();
            all.Value.Select(s => s.Name.Value).Should().Equal("daedalus-migrations", "thalos-release");
            all.Value.Should().OnlyContain(s => s.IsActive && s.Description.Length > 0 && s.ContentHash.Length > 0);

            var migrations = await store.GetAsync(new SkillName("daedalus-migrations"), CancellationToken.None);
            migrations.Value.Description.Should().Be("How to add, verify and apply an EF Core migration in the Daedalus repo.");
            migrations.Value.Tags.Should().Contain("ef");
            migrations.Value.Body.Should().Contain("dotnet ef migrations add")
                .And.NotContain("---", "the frontmatter is parsed off; the body is everything after it");
            migrations.Value.SourcePath.Should().EndWith("SKILL.md");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>A second start with unchanged files is a no-op: the sync compares content hashes and skips.</summary>
    [Fact]
    public async Task A_second_start_leaves_the_rows_untouched()
    {
        using (var first = BuildHost())
        {
            await first.StartAsync();
            await first.StopAsync();
        }

        DateTimeOffset firstUpdatedAt;
        using (var db = fixture.CreateDbContext())
        {
            var row = await db.Skills.AsNoTracking().SingleAsync(s => s.Id == "daedalus-migrations");
            firstUpdatedAt = new DateTimeOffset(row.UpdatedAt, TimeSpan.Zero);
        }

        using var second = BuildHost();
        await second.StartAsync();
        try
        {
            using var db = fixture.CreateDbContext();
            var rows = await db.Skills.AsNoTracking().ToListAsync();
            rows.Should().HaveCount(2);
            new DateTimeOffset(rows.Single(r => r.Id == "daedalus-migrations").UpdatedAt, TimeSpan.Zero)
                .Should().Be(firstUpdatedAt, "an unchanged content hash means the file is skipped entirely");
        }
        finally
        {
            await second.StopAsync();
        }
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.ContentRootPath = AppContext.BaseDirectory;
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.appsettings.json"), optional: false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:daedalus"] = fixture.ConnectionString,
        });

        builder.Services.AddPooledDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        builder.Services.AddDaedalusAgents(builder.Configuration, builder.Environment);
        return builder.Build();
    }
}
```

**Notes for the executor.** The host also starts `AgentSessionCrashRecovery` (needs the factory — registered), the Rag.NET schema initializer (`EnsureSchemaOnStartup = true`; the fixture installs the `vector` extension, so `rag_chunks` is created in the container — the Playwright fixture already does the same) and the reindex sweeper (its `StartupDelay` is 10 s, so it does nothing inside the test). MCP servers are built lazily per turn, so a missing `dnx`/`roslyn` is harmless. `Daedalus.Tests.Integration` must link `src/Daedalus.Api/appsettings.json` as `Daedalus.Api.appsettings.json` the same way `Daedalus.Tests.Unit` does — add the `<None Include … Link=… CopyToOutputDirectory="PreserveNewest"/>` item if it is not there.

**Step 3:** run both suites. **Step 4:** Commit:

```
test(skills): prove both starter skills load at host start

Boots a real host on the shipped API configuration against Testcontainers and asserts
the sync wrote both procedures with their bodies verbatim, plus a second start that
changes nothing because the content hashes match. A unit fact pins the Content copy
itself, so a broken copy fails the no-Docker CI job too.
```

---

## Task 12: `appsettings.json` — skills configuration, globs and tools (G6)

**Files:** modify `src/Daedalus.Api/appsettings.json`; modify `tests/Daedalus.Tests.Unit/Configuration/ApiThalosConfigurationTests.cs`.

**Step 1: failing test** — update the pinned agent fact (it pins the `Tools` list and the instruction text; 1.2's Task 9 had to do the same for `memory__*`):

```csharp
        agent.Tools.Should().Equal("roslyn__*", "daedalus__*", "memory__*", "skills__*", "context7__*");
        agent.Skills.Should().Equal("*");
        agent.Instructions.Should().Contain("roslyn__").And.Contain("daedalus__").And.Contain("memory__").And.Contain("skills__");
```

Run → red.

**Step 2:** `src/Daedalus.Api/appsettings.json`, inside `Thalos`, after the `Memory` block:

```json
        "Skills": {
            "Enabled": true,
            "Roots": [ "skills" ],
            "Catalogue": { "MaxChars": 2000 },
            "Search": { "TopK": 5, "MinScore": 0.6 }
        },
```

and on the agent:

```json
            "Tools": [ "roslyn__*", "daedalus__*", "memory__*", "skills__*", "context7__*" ],
            "Skills": [ "*" ]
```

Instructions — append one sentence to the existing text (keep it one line, it is a JSON string):

```
… A catalogue of the procedures you may load is appended to these instructions; use skills__load to read one in full before you follow it, and skills__search when the catalogue is truncated. A procedure tells you how this project does something — it does not do it for you.
```

**Do not** add a `Thalos:Skills` block to `src/Daedalus.Console/appsettings.json`: skills are API-host only, and `Console_and_api_agree_on_the_shared_memory_settings` compares memory keys only, so nothing forces the console file to grow. (If you find yourself wanting to, re-read §0.6.)

**Step 3:** `dotnet test tests/Daedalus.Tests.Unit --nologo --filter "FullyQualifiedName~ApiThalosConfigurationTests"` → green, then the full unit filter, then `SkillsStartupTests` (it reads this file).

**Step 4:** Commit:

```
feat(skills): give the Daedalus Architect its skills globs and tools

Thalos:Skills points at the shipped skills folder with a 2000-char catalogue budget; the
agent gets skills__* beside memory__* and a "*" glob, so both starter procedures are in
its catalogue. The instruction line says what a procedure is - guidance to follow, not a
tool that acts.
```

---

## Task 13: `skill-catalogue-failed` on the SSE stream (G6)

**Files:** modify `tests/Daedalus.Tests.Unit.Application/Agents/AgentDtoMapperTests.cs`; modify `src/Daedalus.Agents/Api/AgentDtoMapper.cs` (comment only).

**The decision (argued in §0.6-6): no DTO arm — pass-through, pinned by a test.** Since 1.2, `ToDto(AgentEvent)`'s default arm is `_ => new AgentEventDto(agentEvent.Kind)`, so the event already reaches the client by name and cannot kill the stream. Adding a real arm would cost a payload record, a field on `AgentEventDto`, an `ApiJsonSerializerContext` entry and a Blazor `Apply` arm — for a diagnostic with no UI (design §6 ships none) that the catalogue provider already logs and that never fails a turn (design §7). What is missing is not a mapping but *evidence that the pass-through is deliberate*, so this task adds exactly that.

**Step 1: failing test.**

```csharp
    /// <summary>
    ///     Skills have no UI (design section 6), and the catalogue provider never fails a turn — it logs, raises this
    ///     event and proceeds without a catalogue. So the event is deliberately <b>not</b> given a DTO arm: it reaches
    ///     the client by kind through the forward-compatible default, which is also what protects the stream against
    ///     event types a newer Thalos adds. This fact is the decision; without it the pass-through is an accident.
    /// </summary>
    [Fact]
    public void Skill_catalogue_failed_reaches_the_client_as_kind_only()
    {
        var evt = new SkillCatalogueFailedEvent(SessionId.New(), TurnId.New(), AgentErrorCode.SkillStoreFailed);

        var dto = AgentDtoMapper.ToDto(evt);

        dto.Kind.Should().Be("skill-catalogue-failed");
        dto.Text.Should().BeNull();
        dto.Memory.Should().BeNull();
        dto.ErrorCode.Should().BeNull("a skills diagnostic is not a turn failure and must not render as one");
    }
```

> The constructor shape comes from Task 2. If the event has no `AgentErrorCode` member, use whatever it does have; the assertions do not depend on it.

Run → likely **green already**. That is fine and expected: this is a characterisation test protecting a decision. To prove it is load-bearing, temporarily add an arm that maps it into `AgentEventDto.ErrorCode`, watch the fact fail, and revert.

**Step 2:** extend the `ToDto(AgentEvent)` summary comment:

```
    ///     …An event type this adapter does not know yet is passed through by kind only, so a newer Thalos never kills
    ///     the SSE stream. <c>skill-catalogue-failed</c> (Thalos.NET.Skills) rides that path on purpose: skills have no
    ///     UI, the catalogue provider logs the failure and the turn proceeds without a catalogue, so there is nothing
    ///     for a client to render beyond the kind.
```

**Step 3:** unit filter → green. **Step 4:** Commit:

```
test(skills): pin skill-catalogue-failed as a kind-only SSE passthrough

Skills have no UI and the catalogue provider never fails a turn, so the event
deliberately gets no DTO arm and rides the forward-compatible default the mapper has had
since 1.2. The fact turns that from an accident into a decision, and the mapper comment
says why.
```

---

## Task 14: ArchUnit — load the Skills assembly (G7)

**Files:** modify `tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs`.

**1.2 lesson:** ArchUnitNET does not synthesise types for assemblies it has not loaded, so the `^Thalos(\.|$)` rules pass *vacuously* for any Thalos assembly missing from `ThalosAssemblies`. Adding `Thalos.NET.Skills` is what makes "Domain/Application/Infrastructure/Web must not depend on Thalos" actually cover skills.

**Step 1: failing test** — add the proof fact first, before touching the array:

```csharp
    /// <summary>Anchored on the skills package's own namespace, so the fact fails if the assembly is not loaded.</summary>
    private const string SkillsNamespacePattern = "^Thalos\\.Skills(\\.|$)";

    [Fact]
    public void SkillsAssembly_IsLoaded_SoTheThalosRulesCoverIt()
    {
        // Known positive. ArchUnitNET does not synthesise types for assemblies it has not loaded, so without
        // Thalos.NET.Skills in ThalosAssemblies every ^Thalos(\.|$) rule above would pass vacuously for skills types —
        // the same trap the Rag.NET rules hit in phase 1.2.
        var rule = Types().That().ResideInNamespaceMatching(SkillsNamespacePattern)
            .Should().Exist()
            .Because("Thalos.NET.Skills must be loaded into the architecture or the Thalos boundary rules never see a skills type");

        rule.Check(Architecture);
    }
```

Run: `dotnet test tests/Daedalus.Tests.Unit --nologo --filter "FullyQualifiedName~CleanArchitectureTests"` → this fact fails with *"There are no objects matching the criteria"*. **That failure is the proof the change matters — note the exact message in §0.8.**

**Step 2: implement.** Add `using Thalos.Skills;` and extend the array:

```csharp
        typeof(IMemoryService).Assembly, // Thalos.NET.Memory
        MemoryRagNetAssembly, // Thalos.NET.Memory.RagNet
        typeof(ISkillStore).Assembly, // Thalos.NET.Skills
    ];
```

**Step 3:** re-run → green (ArchUnit facts 19 → 20). Full unit filter → green.

**Step 4 (worth 30 seconds):** confirm the *negative* rules now bite by temporarily adding a `using Thalos.Skills;` and a field of a skills type to a `Daedalus.Application` file — `ApplicationLayer_ShouldNotDependOn_Thalos` must fail naming it. Revert.

**Step 5:** Commit:

```
test(arch): load Thalos.NET.Skills so the Thalos boundary rules cover it

ArchUnitNET does not synthesise types for assemblies it has not loaded, so the
^Thalos(\.|$) rules were blind to skills types. The new known-positive fact failed with
"There are no objects matching the criteria" before the assembly was added and passes
after, the same guard the Rag.NET rules got in phase 1.2.
```

---

## Task 15: README (G7)

**Files:** modify `README.md`.

1. **Intro paragraph of "## Thalos agents":** add a sentence after the 1.2 one —
   *"Phase 1.3 adds **skills** on top: `Thalos.NET.Skills` (Thalos.NET 0.3.0), a library of markdown procedures authored in git that agents can see the titles of every turn and pull into context on demand. Design: [docs/plans/2026-08-18-thalos-skills-design.md](docs/plans/2026-08-18-thalos-skills-design.md); see [Skills](#skills) below."*
2. **"What you get":** add `skills__load`/`skills__search` to the tool list and "a catalogue of available procedures is appended to the agent's instructions" to the per-turn description; add `Skills` to the persisted-tables sentence.
3. **"### Configuration (`Thalos:*` …)":** add the `Skills` block to the JSON sample and a row per key to the key table — `Enabled`, `Roots` (relative to the content root; **a configured root that does not exist fails host start, on purpose**), `Catalogue:MaxChars`, `Search:TopK`, `Search:MinScore`, plus the per-agent `Skills` glob list.
4. **New "### Skills" section after "### Memory"**, mirroring its bullet structure:
   - *What they are* — a procedure document the agent loads, not an executable workflow and not a prompt template.
   - *Two-stage loading* — names + descriptions every turn (capped by `Catalogue:MaxChars`, overflow reported with an explicit "and N more" line, never silently), bodies on demand via `skills__load`.
   - *Files are the source of truth* — `skills/<name>/SKILL.md` at the repo root, YAML frontmatter (`name` required and must match the folder, `description` required, `tags` optional), body verbatim, synced one-way at startup. **Editing a skill while the host runs does nothing until restart** — deliberate.
   - *Assignment* — per agent by glob on `Skills`, like `Tools`. A name outside an agent's globs answers "unknown skill", identical to a name that does not exist: no probing.
   - *Where it lives* — the `Skills` table (`PostgresSkillStore`, `Daedalus.Agents`), name as primary key; deleted files are deactivated, not deleted.
   - *Search* — in-process cosine over the same Ollama generator; without it `skills__search` reports unavailable and the catalogue stays authoritative. **Skills never depend on Ollama being up.**
   - *Trust boundary* — skill bodies come from git, not from model output, so they are **not** passed through `IUntrustedContentScanner`, unlike recalled memories. Whoever can merge a `SKILL.md` can steer the agent, which is the same trust boundary as merging code. Say this explicitly.
   - *Shipped procedures* — `daedalus-migrations`, `thalos-release`.
   - *No API and no UI* — the repo is the UI.
5. **"### Operational notes":** two bullets — (a) a host whose content root has no `skills/` folder fails to start with an `InvalidOperationException` naming the path; the fix is the `Content` item in `Daedalus.Api.csproj`, not disabling validation; (b) a malformed `SKILL.md` is logged at warning and skipped, and the skipped count is logged — check the startup log after adding one.
6. **Tech Stack table:** add a `Thalos.NET.Skills 0.3.0` row; bump the existing Thalos rows 0.2.0 → 0.3.0 (including `Thalos.NET.Testing` in the testing-libraries table).
7. **Project Structure:** add the repo-root `skills/` folder with a one-line description.

**Verify** every claim against the code as you write it (1.2's README review found four drifted statements). Commit:

```
docs(skills): README section, configuration keys and operational notes
```

---

## Task 16: Architecture diagrams (G7)

**Files:** modify `docs/architecture-diagrams.md`.

In **§14 "Agent turn (Thalos)"**: add the catalogue step to the sequence (context provider appends `<skills note="…">` to the instructions before the model call, from a cache keyed by glob-set — a turn costs a dictionary lookup, not a query), the `skills__load` tool call returning the body wrapped in `<skill name="…">…</skill>` with the same `</skill` neutralisation `MemoryRecallBlock` applies, and a note that `skill-catalogue-failed` is logged and streamed by kind while the turn proceeds without a catalogue.

Add a short **startup** block beside the Rag.NET schema initializer: `SkillSyncService` (`StartingAsync`) → enumerate roots → parse/validate (bad file logged and skipped) → compare content hashes → upsert changed → `DeactivateMissingAsync`.

Update the **strangler layout** graph: `Skills` table beside `AgentMemories`, `PostgresSkillStore` beside `PostgresMemoryStore`, the repo-root `skills/` folder as the input.

Add a note that the skill index is **in-process** — no pgvector, no `rag_chunks` involvement — so a corpus outgrowing it swaps `ISkillIndex` for a pgvector implementation with no change above it (design §10).

Commit:

```
docs(skills): diagram the catalogue, the load tool and the startup sync
```

---

## Task 17: Planning docs (G7)

**Files:** modify `docs/planning/ROADMAP.md`, `docs/planning/MILESTONE.md`, `docs/planning/STATE.md`.

- **ROADMAP** row 1.3 → `complete (2026-08-18; Thalos.NET 0.3.0 on nuget.org, #229)` with all three links (design, plan A, plan B).
- **MILESTONE** phase table row 1.3 → the same status string.
- **STATE.md** — rewrite as a **1.3 → 1.4 handoff**, in the shape 1.2's did: what shipped (the `Skills` table + store, the migration, API-host-only wiring, the two starter procedures and how they reach every host, the agent's globs and tools, the ArchUnit addition), the release context (Thalos.NET 0.3.0, nine packages), open decisions (none blocking, or list them), deferred follow-ups from §0.8, the carried-over items still open from 1.1/1.2 (Keycloak `developer` role; `AgentSession.RowVersion`; the stale local pgvector volume; the sample smoke), and phase **1.4 — Channels (#230)** as the next step. Note explicitly that the design's §10 follow-ups (usage counters, pgvector `ISkillIndex`, and the task runner + mid-run clarification of 1.4–1.6) are recorded and not started.

Commit:

```
chore(state): phase 1.3 skills complete; handoff to 1.4 channels
```

---

## Task 18: Format and full regression run (G8)

**Step 1:** `dotnet format` — then `git diff` and read it. It strips BOMs and can reflow the migration; check nothing semantic moved.

**Step 2:** the four gates, in this order:

```powershell
dotnet build --nologo                                                                            # 0 warnings
dotnet test --nologo --filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"
dotnet test tests/Daedalus.Tests.Integration --nologo --filter "Category!=AuthenticationFlow"
dotnet test tests/Daedalus.Tests.Playwright.Browser --nologo
```

**Step 3: read the browser output for `Skipped`, not just the exit code.** 1.2 shipped a whole phase with the Agent category silently inconclusive because `Assert.Inconclusive` prints as "Skipped" and exits 0. The fixture now fails loudly, but the browser host **calls `AddDaedalusAgents`**, which from Task 9 validates skill roots — so if the `Content` copy did not reach `tests/Daedalus.Tests.Playwright.Browser`, host start throws and the whole suite fails. That is the intended behaviour; if it happens, fix the copy (Task 10 step 5), not the validation.

**Step 4:** record every suite's count in §0.8 against 1.2's baseline (unit 868, integration 343, browser 99, Agent category 5, `Skipped: 0`).

**Step 5:** Commit only if `dotnet format` changed files:

```
style: dotnet format after the skills work
```

---

## Task 19: AppHost smoke run (G8)

1.2 skipped this twice and only caught the schema race on the third attempt. Do it.

**Step 1:**

```powershell
$env:PARAMETERS__DB_USERNAME = "postgres"; $env:PARAMETERS__DB_PASSWORD = "postgres"; $env:ANTHROPIC_API_KEY = "sk-dummy"
dotnet run --project src/Daedalus.AppHost
```

(A dummy Anthropic key is fine — the provider reads it lazily. If the local Postgres volume is stale, `docker volume rm` it first; that finding is in STATE from 1.2.)

**Step 2:** wait for `migrations` to complete (the AppHost uses `.WaitForCompletion(migrations)`), then check the database:

```sql
SELECT "Name", "IsActive", left("Description", 60), "SourcePath" FROM "Skills" ORDER BY "Name";
```

Expect **two active rows** with absolute `SourcePath`s under the API's content root. A DI or `ValidateSkillsConfig` failure kills the host in seconds, so an API that is still up after a minute is itself a signal.

**Step 3:** open `/agent` in the Blazor app, start a session and ask *"what procedures do you have?"* — the answer should name both skills from the catalogue without a tool call, and *"how do I add a migration here?"* should produce a `skills__load` tool call in the SSE stream. Screenshot both into `docs/regression-screenshots/2026-08-18-skills/`.

**Step 4:** `docker compose`-free teardown (Ctrl-C). Record the outcome in §0.8 — including anything environmental you had to work around, as 1.2 did.

**Step 5:** No commit unless the screenshots are added:

```
docs(skills): AppHost smoke run screenshots for the catalogue and skills__load
```

---

## Task 20: Pre-push review, PR, CI, merge (G8)

**Step 1:** If you took the local-pack path (§0.2), undo it **now**: pins → `0.3.0` from nuget.org, `thalos-local` source removed from `nuget.config`, `packages-local/` deleted, `dotnet restore --force-evaluate && dotnet build` clean. A branch that reaches CI with a local pin cannot pass.

**Step 2:** `pre-push-review` skill against `main`. Its checklist for this branch: plan adherence, commit hygiene (**header ≤ 100 and every body line wrapped at 100** — commitlint parses post-first paragraphs as footers, §0.1), no leftover local feed, no `TODO`, XML docs on every public member, `ConfigureAwait(false)` in library code, no raw exception text in an `AgentError.Detail`.

**Step 3:**

```powershell
git push -u origin feature/thalos-skills
gh pr create --fill --base main
```

PR body: what shipped, the §0.6 deviations (especially the fail-fast root validation and the no-DTO-arm decision), the suite counts, and links to the design + both plans.

**Step 4:** CI green — build+test (both jobs), all three images, commitlint. **The three Docker image builds are the only place the `.dockerignore` change is exercised**; if the API image build fails on the skills copy, fix it here, not after merge.

**Step 5:** Merge, delete the branch.

---

## Task 21: Close #229 and complete phase 1.3 (G8)

1. `gh issue close 229 --comment "Phase 1.3 shipped in #<pr>: Thalos.NET 0.3.0 skills consumed — Skills table + PostgresSkillStore, AddSkills migration, API-host-only wiring, two starter procedures, agent globs and skills__* tools."`
2. Verify the ROADMAP/MILESTONE rows from Task 17 landed on `main`, and that STATE reads as a 1.3 → 1.4 handoff.
3. Run `complete 1.3` (project-orchestration) — it audits the milestone rows against the definition of done.
4. Confirm the Thalos.NET side is closed out too: 0.3.0 tagged and on nuget.org, nine packages, `pack-validate` updated for the ninth (Plan A's job, but this is the last chance to notice it was missed).

---

## Definition of done (= phase 1.3 done)

- [ ] Thalos.NET **0.3.0** consumed from nuget.org; nine `PackageVersion` pins; no local feed anywhere in the tree.
- [ ] Four `AgentErrorCode.Skill*` codes mapped to HTTP; the exhaustiveness guard pins 22.
- [ ] `Skill` aggregate (Thalos-free) + EF configuration + `DbSet` + migration `AddSkills` with a migration test that also rolls the chain back and forward.
- [ ] `PostgresSkillStore` green against `Thalos.Testing.SkillStoreContractTests` on Testcontainers, plus Daedalus facts for AND tag filtering and validation-vs-store errors.
- [ ] `AddDaedalusAgents` registers skills; `AddDaedalusMemory` provably does not; a configured-but-missing root fails registration naming the path.
- [ ] `skills/daedalus-migrations` and `skills/thalos-release` exist, are copied into the API and all three test hosts, and **load into Postgres on a real host start** (integration test) — with a unit fact pinning the copy in the no-Docker CI job.
- [ ] `appsettings.json` carries `Thalos:Skills`, the agent's `Skills: ["*"]` and `skills__*` in `Tools`; the pinned configuration test is updated.
- [ ] `skill-catalogue-failed` passes through as kind-only, pinned by a test that says why.
- [ ] ArchUnit loads `Thalos.NET.Skills` with a proof fact that failed before the assembly was added.
- [ ] README, `docs/architecture-diagrams.md` §14 and the planning docs updated.
- [ ] `dotnet build` 0 warnings; unit, integration and browser suites green with **`Skipped: 0`** in the browser run; AppHost smoke run done and recorded.
- [ ] Pre-push review PASS, PR merged, CI green (including the three image builds), #229 closed, `complete 1.3` run.

---

## 10-line summary

1. **Deliverable constraint:** I run read-only with no `Write`/`Edit` tool, so the plan is returned above as text rather than written to `docs/plans/2026-08-18-thalos-skills-plan-b.md` — copy it verbatim to that path.
2. **21 tasks**, TDD throughout (failing test → run → implement → run → commit), each with exact commands and a conventional-commit message wrapped at 100 for both header and body.
3. **Groups:** G1 = tasks 1–3 (0.3.0 pins for nine packages, API reconciliation, `AgentErrorResults` Skill\* arms + guard 18→22); G2 = 4–6 (`Skill` aggregate, EF config, `PostgresSkillStore` on the contract suite); G3 = 7 (migration + `Down`-chain test); G4 = 8–9 (options/validation, wiring in `AddDaedalusAgents` only); G5 = 10–11 (`skills/` folder, two real procedures, copy mechanism, startup test); G6 = 12–13 (appsettings, SSE decision); G7 = 14–17 (ArchUnit, README, diagrams, planning); G8 = 18–21 (regression, AppHost smoke, review/PR/merge, close #229 + `complete 1.3`).
4. **Design decisions argued in §0.6:** name as primary key; skills roots default **empty** but a configured-missing root **fails registration** (an agent that silently lost every procedure is indistinguishable from a healthy one); roots resolved against `ContentRootPath` Daedalus-side; **no DTO arm for `skill-catalogue-failed`** — pass-through pinned by a characterisation test, because skills ship no UI and the event never fails a turn.
5. **Risk 1 (highest): Plan A has not started.** `C:\Projects\Prive\Thalos.NET` HEAD is `1a5842b` with no `Thalos.NET.Skills` project — 0.3.0 does not exist yet. §0.2 gives the local-pack fallback via `scripts/pack-local.ps1` (whose header still says "eight packages" — if it packs eight, Plan A is incomplete: stop and report) and three hard rules for removing it before the PR.
6. **Risk 2: every API name in Tasks 4/6/8/9/13 is an assumption** from design §3–§5. Task 2 is a mandatory reconciliation against the real package XML docs with three explicit stop-and-report conditions (`UseSkillStore<T>` missing, sync not an `IHostedLifecycleService`, `SkillStoreContractTests` absent).
7. **Risk 3: the contract suite may contradict the aggregate's limits** (tag count/length, description, hash encoding). §0.6-7 makes the contract authoritative and tells the executor to relax constants and record it — the exact failure mode `AgentMemory` hit in 1.2 with untrimmed text.
8. **Risk 4: the content copy.** The `skills/` folder must reach the API's bin, three test outputs and the Docker image. Task 10 verifies all four rather than assuming (`.dockerignore`'s `*.md` does not cross `/`, so skills survive — but it gets an explicit `!skills/` and a real image check), and Task 18 warns that a broken copy now fails the browser suite because host start validates roots.
9. **Open questions for the user/Plan A:** does `SkillQuery.Tags` mean AND or ANY; does `ListAsync` pin an ordering; what does `AgentDefinition.Skills` default to when unset; and does `SkillOptions.Roots` accept assignment or only mutation. All four are Task-2 reconciliation items with a specified fallback, none blocks starting.
10. **Not in scope, recorded:** no API/Blazor surface, no `WatchFiles`, no usage counters, no pgvector `ISkillIndex`, no console-host skills — and 1.2's still-open items (Keycloak `developer` role, `AgentSession.RowVersion`, stale local pgvector volume) are carried into the STATE handoff in Task 17, not fixed here.

### Critical files for implementation
- `C:\Projects\Prive\daedalus\src\Daedalus.Agents\DaedalusAgentsServiceCollectionExtensions.cs`
- `C:\Projects\Prive\daedalus\src\Daedalus.Agents\Memory\PostgresMemoryStore.cs` (the store model to copy)
- `C:\Projects\Prive\daedalus\src\Daedalus.Domain\Entities\AgentMemory.cs` (the aggregate model to copy)
- `C:\Projects\Prive\daedalus\src\Daedalus.Infrastructure\Migrations\20260817194349_AddAgentMemories.cs` (migration conventions + the `Down`-chain lesson)
- `C:\Projects\Prive\daedalus\tests\Daedalus.Tests.Unit.Application\Agents\DaedalusAgentsRegistrationTests.cs` and `C:\Projects\Prive\daedalus\tests\Daedalus.Tests.Integration\Agents\PostgresMemoryStoreTests.cs`
