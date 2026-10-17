# Native AOT blocker survey: Daedalus and Thalos.NET

Date: 2026-10-17. SDK 10.0.402, runtime pack ILCompiler 10.0.13, linux-x64.
Daedalus at origin/main 04a67164; Thalos.NET at origin/main 2ca2396. Nothing committed, pushed or opened.
Worktrees were removed afterwards. Raw logs were in scratchpad/out/ (api.log, Cli.log, thalos-probe.log, ...).

## How the numbers were obtained (read this first)

- Environment has NO clang/gcc, NO zlib headers and NO `wasm-tools` workload. A real `PublishAot` publish therefore cannot finish.
- Trick used (changes nothing committed): put stub `clang` and `objcopy` scripts first on PATH (in the scratchpad).
  That satisfies the SDK prerequisite check, ILC then runs the full whole-program compile and prints every
  trim/AOT warning, and the build only fails at the final native link step (`clang ... exit 1`).
  So "publish" below = ILC compile completed with warnings only and zero ILC errors; the binary was NOT produced or run.
- Command pattern (Daedalus): `dotnet publish src/Daedalus.X -c Release -r linux-x64 -p:PublishAot=true -p:TreatWarningsAsErrors=false -p:WarningsAsErrors= -p:EnableTrimAnalyzer=true -p:EnableAotAnalyzer=true -p:TrimmerSingleWarn=false -p:EnforceCodeStyleInBuild=false -p:AnalysisLevel=latest`
  (command-line properties override the csproj `PublishAot=false` / `EnableTrimAnalyzer=false`).
- "Distinct warnings" = unique (code, origin member, callee) lines from ILC with `TrimmerSingleWarn=false`
  (per-method, not collapsed per assembly). One reflective call site usually yields an IL2026 + an IL3050 pair, so halve for "sites".
- Grouping is by the assembly/namespace of the ORIGIN method (the code that makes the unsafe call), then by what it calls.
- Thalos: analyzer build of every src project (`-p:IsAotCompatible=true -p:EnableTrimAnalyzer=true -p:EnableAotAnalyzer=true -f net10.0`) sees only
  Thalos's own source. To also see third-party internals I added a throwaway probe app (in scratchpad, outside the repo) with ProjectReference to all 18 Thalos
  projects + `TrimmerRootAssembly` for each, and ran the stubbed AOT publish on it (whole-program ILC warnings).
- Web: wasm-tools workload missing, so `RunAOTCompilation=true` fails with NETSDK1147. Only trim publish (`PublishTrimmed=true`) was run.
- Daedalus projects all currently set `PublishAot=false` (Api, Cli, Migrations, Web) and several set `EnableTrimAnalyzer=false`; Directory.Build.props has `TrimMode=partial`, `TreatWarningsAsErrors=true`.
  Daedalus consumes Thalos/ZeroAlloc/AI.Sentinel/Rag.NET as NuGet packages (versions in Directory.Packages.props), so their warnings below are those of the pinned package versions.

## Per-target results: Daedalus

| Target | ILC compile finished? | Distinct warnings | Note |
|---|---|---|---|
| src/Daedalus.Api | yes (only native link failed) | 16,725 | 14,948 are the Anthropic SDK alone |
| src/Daedalus.Cli | yes | 750 | EF Core dominated |
| src/Daedalus.Migrations | yes | 784 | EF Core dominated |
| src/Daedalus.Sandbox | yes | 8 | all via Thalos.NET.Sandbox.Host; near-AOT already |
| src/Daedalus.Web | no AOT (no wasm-tools); trim publish ok | 41 analyzer warnings, ILLink emitted none | see Web section |

"Which could publish at all": with a toolchain, all four server/CLI targets get through ILC with warnings only (no AOT errors seen), so a binary would be
produced; the question is whether it runs. Runtime breakage is near-certain for Api/Cli/Migrations (EF Core model building, MVC, reflection JSON). Sandbox is the only plausible one.
Not verified at runtime because the link step is impossible here.

### Daedalus.Api (16,725)

| Root cause group | Distinct | Ours? | Representative warnings | Fix read |
|---|---|---|---|---|
| Anthropic SDK (Anthropic.Models/Core/Helpers, M.E.AI Anthropic adapter) | 14,948 | third-party | IL2026+IL3050 `Anthropic.Models.ApiErrorObject.ApiErrorObject()` -> `JsonSerializer.SerializeToElement<String>`; `Anthropic.Core.FriendlyJsonPrinter.PrintValue`; `Anthropic.Helpers.BetaMessageContentAggregator.MergeBlock` | SDK model types use reflection-based System.Text.Json in every constructor. Upstream fix needed, or stop using the SDK on the AOT path (call REST via our own source-generated client, or use the M.E.AI abstraction over a hand-rolled client). Biggest single blocker by count, one root cause. |
| EF Core | 1,171 | third-party | IL3050 `DbContext..ctor(DbContextOptions)`; IL2026 `ModelBuilder.ApplyConfigurationsFromAssembly`; IL2091 `SimplePrincipalKeyValueFactory.NoNullsCustomEqualityComparer`; Linq.Expressions `Expression.New` in query compilation | EF Core runtime query compilation (expression trees) and model building are not AOT-safe. Needs precompiled queries + compiled model (`dotnet ef dbcontext optimize`), still experimental; or move off EF Core to ZeroAlloc.ORM/ADO.NET. |
| ASP.NET Core MVC | 143 | third-party | IL2026 `CreatedAtActionResult..ctor` -> `RouteValueDictionary(Object)`; `DataAnnotationsModelValidator.Validate`; model binding (63), `MvcCoreDiagnosticListenerExtensions` (34), ApplicationModels (12), Formatters (9) | Controllers are not supported by Native AOT. Replace with minimal APIs / Request Delegate Generator (18 controllers). |
| Aspire.Hosting stack | 128 | third-party, pulled in by us | IL2026 `Aspire.Hosting.DistributedApplicationBuilder.ConfigurePipelineOptions`; `k8s.KubernetesMetricsExtensions`; `StreamJsonRpc.JsonMessageFormatter` (Newtonsoft); `Aspire.Hosting.Dcp` | Daedalus.ServiceDefaults references `Aspire.Hosting` and `Aspire.Hosting.PostgreSQL` (the orchestration packages, with KubernetesClient, StreamJsonRpc, Newtonsoft, Polly, Grpc), and they flow into Api/Cli. Split what ServiceDefaults really needs (it likely only needs resource-name constants, see AspireExtensions.cs) out of the hosting packages. Daedalus-fixable, no upstream work. |
| Daedalus own code | 87 | ours | JSON: `ReviewEvidence.ParseFindings`, `NodeSelectionRecord.Parse/Serialize`, `PostgresAgentSessionStore.AppendMessagesAsync` (49); `Expression.New` via EF LINQ in `CostAnalyticsService` and entity configurations (17); `McpToolBuilder.BuildLocalTools` (Assembly.GetTypes, Type.GetMethods); DI `ForwardHostedServiceToConcreteSingleton<T>` IL2091 | Source-gen `JsonSerializerContext` (anonymous types must become records), explicit tool registration instead of assembly scan, `DynamicallyAccessedMembers` on generic helpers. |
| Thalos (ours) | 78 | ours | `OrmWorkflowStore.*` (36, JSON), `WorkflowVariableBlock` (10), `AnthropicCacheMarkers` (8), `ThalosBuilder.UseSessionStore<TStore>` IL2091, `ChannelThalosBuilderExtensions` options Bind, `LocalToolSource.Options`, Sandbox.Host `MapPost/MapGet` | See Thalos section. All fixable in Thalos with JSON contexts, `[DynamicallyAccessedMembers]` on builder generics, config binder source generator. |
| Npgsql.EntityFrameworkCore.PostgreSQL | 73 | third-party | IL3050 `NpgsqlArrayConverter<,,>.ArrayConversion` -> `Expression.Lambda`, `MakeGenericType` | Same as EF Core; Npgsql provider also not AOT-clean. Upstream. |
| YamlDotNet | 47 | third-party (via Thalos.Workflow ProcessLoader and KubernetesClient) | IL3050 `Deserializer..ctor` -> `DeserializerBuilder..ctor`; `TypeConverter.ChangeType` | Thalos side: switch to YamlDotNet static generator (StaticDeserializerBuilder / YamlDotNet.Analyzers.StaticGenerator) or replace the YAML process format. |
| OllamaSharp | 28 | third-party | IL2026 `OllamaApiClient.GetJsonContent<TRequest>`, `ChatAsync` -> `JsonSerializer.Serialize<ChatRequest>` | Upstream (reflection STJ). Only needed because of the Ollama provider; drop or isolate if Api targets AOT. |
| ZeroAlloc.* | 15 | ours | `ZeroAlloc.ValueObjects.EfCore.TypedIdModelConfigurationExtensions.AddTypedIdConventions` (Assembly.GetTypes, MakeGenericMethod/Type); `ZeroAlloc.Outbox.EfCore.EfCoreOutboxStore<>` (Expression trees); `OutboxServiceCollectionExtensions.WithSystemTextJsonSerializer` | ZeroAlloc-Net fixes: generate typed-id converter registration instead of reflection; Outbox EfCore store is inherently EF; keep STJ serializer behind a context-taking overload. |
| Microsoft.Extensions config/DI | 5 | third-party | IL2091 `NpgsqlServiceCollectionExtensions.AddNpgsql<TContext>`; `MvcServiceCollectionExtensions.AddControllers` | Falls away with EF/MVC. |
| AI.Sentinel | 1 | ours | IL2069 `SentinelOptions.DetectorRegistration..ctor(Type, Func)` field DAM mismatch (plus Daedalus `DisableDetector` uses `Assembly.GetExportedTypes`/`MakeGenericMethod`) | Easy: annotate `DetectorType` with DAM; expose a typed disable API so Daedalus needn't reflect. |
| System.Reflection (misc) | 1 | third-party | IL2067 `TypeExtensions.FindIndexerProperty` | EF internal helper. |

Not seen at all in Api: Microsoft.Agents.AI, ModelContextProtocol, Rag.NET, Radzen, JwtBearer, Scalar, Asp.Versioning, SignalR. Either they are clean or ILC did not reach them
(tree-shaken or not rooted). I did not verify which. Unsure.

### Daedalus.Cli (750) and Daedalus.Migrations (784)

| Root cause group | Cli | Migrations | Ours? | Representative | Fix read |
|---|---|---|---|---|---|
| EF Core | 543 | 718 | third-party | `ModelBuilder.ApplyConfigurationsFromAssembly`, `RelationalDatabaseFacadeExtensions.MigrateAsync` (IL3050), `Microsoft.EntityFrameworkCore.Query.*` | Compiled model plus precompiled queries (experimental); Migrations in particular should simply stay JIT (a tool, not a hot service) or use plain SQL scripts. |
| Npgsql.EntityFrameworkCore.PostgreSQL | 58 | 58 | third-party | `NpgsqlArrayConverter` | Upstream. |
| Thalos | 53 | 0 | ours | JSON in `Thalos.Workflow.*`, `UseSkills`, `UseMemory` | Thalos fixes. |
| Daedalus own code | 46 | 3 | ours | `ReviewEvidence`, `DaedalusAgents...ConfigureX`, `Program.<Main>` -> `MigrateAsync` | JSON contexts etc. |
| YamlDotNet | 32 | 0 | third-party | `Deserializer..ctor` | via Thalos.Workflow. |
| OllamaSharp | 16 | 0 | third-party | `OllamaApiClient.ChatAsync` | upstream. |
| ZeroAlloc.* | 1 | 4 | ours | `ZeroAlloc.ValueObjects.EfCore.TypedIdValueConverter<,>.BuildToProvider` (IL2090) | ZeroAlloc-Net. |

### Daedalus.Sandbox (8)

| Root cause | Distinct | Ours? | Warnings | Fix read |
|---|---|---|---|---|
| Thalos.NET.Sandbox.Host | 8 | ours | IL2026/IL3050 `SandboxHost.Map` -> `MapPost/MapGet(IEndpointRouteBuilder, String, Delegate)`; `Program.Main` -> `SandboxHost.CreateBuilder` (uses `WebApplication.CreateBuilder`, annotated RequiresDynamicCode); `LocalToolSource.ScopedTool.InvokeCoreAsync` -> `LocalToolSource.Options` (reflection tool discovery, hand-annotated RequiresUnreferencedCode) | Switch to `CreateSlimBuilder`, give `Map*` typed lambdas (Request Delegate Generator) and a JSON context, drop reflection tool discovery in the sandbox path. Smallest fix set; the only Daedalus target where full AOT is realistic. |

### Daedalus.Web (Blazor WASM)

- `RunAOTCompilation=true` publish: NOT possible, `wasm-tools` workload not installed (NETSDK1147). Not installed by me (would modify the SDK). So WASM AOT was not measured.
- Trim publish (`PublishTrimmed=true`, `TrimMode=full`): succeeds. Analyzer shows 41 distinct IL2026/IL3050/IL2110/IL2111 warnings, all in Daedalus code:
  `ApiClient`, `ProjectApiClient`, `AgentApiClient`, `RepositoriesApiClient` (HttpClientJsonExtensions Get/Post/PutAsJsonAsync, ReadFromJsonAsync without JsonTypeInfo: ~32),
  `Program.cs` (`ConfigurationBinder.Bind`, JSInterop `Invoke<T>`), `App_razor.g.cs` / `MainLayout.razor` (`LayoutView` IL2110/IL2111), `BrainstormService` (Application, `JsonStringEnumConverter`, `Deserialize<List<...>>`).
- ILLink printed no third-party warnings (Radzen.Blazor, ZeroAlloc.Flux, OpenTelemetry): the Blazor WASM SDK suppresses trim-analysis warnings by default and overriding `SuppressTrimAnalysisWarnings=false` did not surface any either. So the third-party situation for Web is UNKNOWN.
- Fix read: pass `JsonSerializerContext`/`JsonTypeInfo` overloads everywhere in the API clients (ours, mechanical). Radzen reflection use (DataGrid, dynamic LINQ, `DropDown` property paths) is the likely real blocker for WASM AOT/trimming; unverified.
- Also note: WASM AOT is a size/startup trade-off (bigger download), usually not worth it for a dashboard. Judgment call.

## Per-target results: Thalos.NET (18 packable projects under src/)

### A. Analyzer build (own source only, net10.0; TFMs are net8.0 and net10.0)

Projects with 0 warnings (7): Abstractions, Git, Git.LibGit2Sharp, Memory.RagNet, Sentinel, Sandbox, Sandbox.Docker.

| Project | Distinct | Representative | Kind |
|---|---|---|---|
| Thalos.NET.Workflow.Orm | 36 | IL2026+IL3050 `OrmWorkflowStore.cs(1027,23)` `JsonSerializer.SerializeToUtf8Bytes`; lines 306-315, 904-954, 1048-1152, 1219 Serialize/Deserialize | STJ without context (18 sites) |
| Thalos.NET.Workflow | 11 | `WorkflowVariableBlock.cs(403..451)` Serialize (5 sites); IL3050 `ProcessLoader.cs(32,63)` `DeserializerBuilder()` | STJ + YamlDotNet |
| Thalos.NET.Anthropic | 10 | `AnthropicCacheMarkers.cs(84,92,99,105)` `SerializeToElement`; `AnthropicThalosBuilderExtensions.cs(28)` `OptionsBuilder.Bind` | STJ + options binder |
| Thalos.NET | 9 | `ThalosBuilder.cs(50,65,72,79,138)` IL2091 (`AddPolicy<TPolicy>`, `UseChatClientProvider<TProvider>`, `AddToolSource<TSource>`, `UseSessionStore<TStore>`); `AuthorizingAIFunction.cs(68)`; `LocalToolSource.cs(316)` `Options(...)` annotated "Discovers tool methods via reflection" | DI generic DAM + STJ + reflection tool discovery |
| Thalos.NET.Sandbox.Host | 6 | `SandboxHost.cs(142-144)` `MapPost/MapGet(..., Delegate)` | minimal API delegate overload |
| Thalos.NET.Skills | 5 | `CharterThalosBuilderExtensions.cs(52)` `ConfigurationBinder.Get<T>`; IL2091 at 65, `SkillThalosBuilderExtensions.cs(72,81)` | binder + DI DAM |
| Thalos.NET.Testing | 4 | `ScriptedChatClient.cs(52,53)` Serialize/Deserialize | STJ (test helper) |
| Thalos.NET.Channels | 3 | `ChannelThalosBuilderExtensions.cs(42)` Bind, `(55)` IL2091 | binder + DI DAM |
| Thalos.NET.Memory | 2 | `MemoryThalosBuilderExtensions.cs(56,65)` IL2091 | DI DAM |
| Thalos.NET.Mcp | 2 | `McpConfigFile.cs(20)` `Deserialize<TValue>(String, ...)` | STJ |
| Thalos.NET.Channels.Telegram | 2 | `TelegramThalosBuilderExtensions.cs(85)` `OptionsBuilder.Bind` | options binder |
| Total | 90 (39 IL2026, 40 IL3050, 11 IL2091) | | |

Fix read, all Thalos-side: (1) a `[JsonSerializable]` context per assembly and pass `JsonTypeInfo` everywhere (accounts for ~70 of 90, 2 warnings per site);
(2) `[DynamicallyAccessedMembers(PublicConstructors)]` on the `TService/TImplementation` type params in builder extensions (11 IL2091, mechanical);
(3) `Bind`/`Get<T>` -> `EnableConfigurationBindingGenerator` or manual binding (5 sites); (4) `Map*` -> typed lambdas with RDG; (5) replace YamlDotNet runtime builder with static generator;
(6) `LocalToolSource` reflection tool discovery is by design (annotated RequiresUnreferencedCode): needs a source-generated tool registry. This is the one real design item.
Also no project sets `IsAotCompatible`/`IsTrimmable`, so nothing is guarded by CI yet; setting `<IsAotCompatible>true</IsAotCompatible>` after the fixes would lock it in.

### B. Whole-program probe (all 18 projects rooted, stubbed AOT publish; 3,490 distinct)

| Group | Distinct | Ours? | Note |
|---|---|---|---|
| Anthropic SDK (12.58.0 in Thalos) | 3,338 | third-party | same cause as Daedalus (fewer types reachable than in Daedalus, which pins 12.55.0 and uses Beta/Helpers more) |
| Thalos itself | 86 | ours | Workflow(.Orm) 45, Anthropic 10, Channels 5, Skills 5, ThalosBuilder 5, Sandbox 4, Testing 4, Tools 4, Mcp 2, Memory 2 (matches A) |
| YamlDotNet | 32 | third-party | `Deserializer()`, `TypeConverter.ChangeType` |
| Test-only (AwesomeAssertions, Reflectify) | 34 | third-party | from Thalos.NET.Testing, can be ignored (but note Testing is packable and drags a test assertion lib into the graph) |
| Rag.NET, AI.Sentinel, LibGit2Sharp, Telegram client, Docker client, ModelContextProtocol, Microsoft.Extensions.AI | 0 | n/a | No warnings surfaced. LibGit2Sharp is a native-lib wrapper: warning-free is not the same as AOT-proven (native library loading at runtime is unverified). Unsure. |

## Cross-target root-cause table (sorted by size; Daedalus numbers are Api / Cli / Migrations / Sandbox, Thalos = probe)

| # | Root cause | Api | Cli | Migr. | Sandbox | Thalos probe | Ours? | One-line fix read |
|---|---|---|---|---|---|---|---|---|
| 1 | Anthropic SDK reflection STJ in every model type | 14,948 | 0 | 0 | 0 | 3,338 | third-party | Upstream fix or bypass the SDK on the AOT path. Single cause. |
| 2 | EF Core (query compile, model building, change tracking) | 1,171 | 543 | 718 | 0 | n/a (Thalos uses ORM/ADO) | third-party | Compiled model + precompiled queries (experimental), or leave Cli/Migrations on JIT, or replace with ZeroAlloc.ORM. |
| 3 | ASP.NET Core MVC controllers | 143 | 0 | 0 | 0 | 0 | third-party | Rewrite 18 controllers as minimal APIs. |
| 4 | Aspire.Hosting stack pulled in by ServiceDefaults | 128 | 0 | 0 | 0 | 0 | ours (dependency choice) | Remove hosting packages from ServiceDefaults. |
| 5 | System.Text.Json reflection in our code (Daedalus + Thalos + M.E.AI glue) | ~105 | ~68 | ~0 | ~0 | ~70 | ours | Source-gen JsonSerializerContext per assembly. |
| 6 | Npgsql.EntityFrameworkCore.PostgreSQL | 73 | 58 | 58 | 0 | 0 | third-party | Upstream; follows EF Core. |
| 7 | YamlDotNet | 47 | 32 | 0 | 0 | 32 | third-party | Static generator or drop YAML in Thalos.Workflow. |
| 8 | OllamaSharp | 28 | 16 | 0 | 0 | 0 | third-party | Upstream, or drop provider on the AOT path. |
| 9 | DI generic DAM annotations (IL2091) in Thalos builders and Daedalus helper | ~24 | ~21 | 0 | 0 | ~14 | ours | Annotate type params; mechanical. |
| 10 | ZeroAlloc.* (ValueObjects.EfCore typed ids, Outbox.EfCore, Outbox STJ serializer) | 15 | 1 | 4 | 0 | 0 | ours (ZeroAlloc-Net) | Generate registration, remove Assembly scan/MakeGeneric. |
| 11 | Config binder (`Bind`, `Get<T>`) | ~8 | ~6 | 0 | 0 | ~8 | ours | Binding source generator. |
| 12 | Linq.Expressions in our EF-LINQ code (`Expression.New`, Daedalus Infrastructure) | 17 | 6+ | 2 | 0 | 0 | ours but inherent to EF | Falls away only with EF replacement or precompiled queries. |
| 13 | Reflection tool discovery (`LocalToolSource`, `McpToolBuilder` assembly scan) | ~6 | ~6 | 0 | 2 | 4 | ours | Source-generated tool registry. Design item. |
| 14 | Minimal API `Map*(Delegate)` / `WebApplication.CreateBuilder` in Thalos.Sandbox.Host | 3 | 0 | 0 | 6 | 3 | ours | RDG typed lambdas + `CreateSlimBuilder`. |
| 15 | AI.Sentinel DAM mismatch | 1 | 0 | 0 | 0 | 0 | ours | One annotation. |

Rows 5, 9, 11 overlap slightly with Daedalus/Thalos split counts (approximate, rounded from categorized samples; rows 1-4, 6-8, 10 are exact counts of distinct ILC warnings).

## Facts and uncertainties

- ASP.NET Core MVC controllers and Razor Pages under Native AOT: NOT supported in .NET 10 (Microsoft docs table "ASP.NET Core and Native AOT": MVC "Not supported", Razor Pages "Not supported", Blazor Server "Not supported", Minimal APIs "Partially supported", gRPC "Fully supported", SignalR "Partially supported", JWT auth "Fully supported"). My recollection of the docs, not re-fetched in this session (no web access used); the 143 MVC warnings here are consistent with it. Moderately sure.
- EF Core Native AOT in .NET 10: still experimental / not fully supported. Compiled models + precompiled queries (`dotnet ef dbcontext optimize --precompile-queries --nativeaot`) exist since EF 9 as experimental; dynamic queries (`Include` with runtime shape, non-constant composition) are unsupported. Npgsql provider support for precompiled queries is partial and not verified here. Moderately sure; the EF Core 1,171-warning count shows runtime path is not AOT-clean.
- Whether anything above actually runs under AOT was NOT tested (no native toolchain). ILC finishing means "compiles with warnings", not "works".
- Whether Microsoft.Agents.AI, ModelContextProtocol, Rag.NET, Radzen, JwtBearer, Scalar are AOT-clean is unknown: zero warnings were seen but reachability was not verified.
- The 14,948 Anthropic count depends on SDK version (12.55.0 in Daedalus vs 12.58.0 in Thalos probe) and reachability. Is a newer SDK source-generated? Unknown; check release notes.
- Blazor WASM third-party trim/AOT warnings: unknown (SDK suppresses; wasm-tools missing).
- Counts of "ours" rows 5, 9, 11 are categorized by pattern match and approximate.
