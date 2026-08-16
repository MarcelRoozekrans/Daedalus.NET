# Thalos.NET — Plan B: Daedalus Integration + Housekeeping

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make Daedalus the first consumer of Thalos.NET: fix the broken build, enable CPM with a local feed, add the `Daedalus.Agents` adapter (Postgres session store, in-process tools, composition root), expose the agent over REST + SSE, add a Blazor chat page, and prove it end-to-end with integration + Playwright tests — while leaving the Ralph Loop untouched (strangler).

**Architecture:** `Daedalus.Agents` (new project) references `Thalos.NET.*` packages from the local feed plus `Domain` and `Infrastructure` (for `ApplicationDbContext`). It implements `IAgentSessionStore` over two new aggregates (`AgentSession`, `AgentMessage`), exposes Daedalus knowledge tools as `[ThalosToolType]` classes, and composes everything in `AddDaedalusAgents(configuration)`. `Daedalus.Api` adds `AgentsController` + `AgentSessionsController` (SSE streaming); `Daedalus.Web` adds `Agent.razor`. DTOs live in `Daedalus.Application/DTOs/Agents/` (plain records) so the WASM client can share them without referencing Thalos.

**Tech Stack:** as Daedalus today (.NET 10, EF Core 10 + Npgsql, Blazor WASM + Radzen, xUnit/NUnit/Playwright, Testcontainers) + `Thalos.NET.*` 0.1.0-local, ZeroAlloc.Validation.AspNetCore + ZeroAlloc.Mapping (adapter/API only), Anthropic 12.40, Microsoft.Extensions.AI 10.9, ModelContextProtocol 2.2.

**Prerequisite:** Plan A complete; `pwsh scripts/pack-local.ps1` has produced `Thalos.NET.*` packages in `C:\Projects\Prive\.nuget-local` (note the exact `0.1.0-local.<stamp>` version).

**Design doc:** `docs/plans/2026-08-16-thalos-agent-core-design.md` · **Tracking:** #227

---

## 0. Read this first

### 0.1 Verified facts about the Daedalus repo (2026-08-16)

- `dotnet build` **fails** with 127 `NU1902/NU1903` errors (`TreatWarningsAsErrors`). Origins (`dotnet nuget why`):
  - `MessagePack 2.5.192` ← `StreamJsonRpc 2.22.23` ← **`Aspire.Hosting 13.1.0`**, which `Daedalus.ServiceDefaults` references (and AppHost). Fix: Aspire 13.1.0 → **13.4.6** (`Aspire.Hosting`, `Aspire.Hosting.AppHost`, `Aspire.Hosting.PostgreSQL`; `Aspire.Hosting.Keycloak` → `13.4.6-preview.1.26319.6`; `CommunityToolkit.Aspire.Hosting.Ollama` → `13.4.1-beta.706`).
  - `OpenTelemetry.* 1.15.0` — fix: **1.17.0** for all six OTel packages.
  - `Microsoft.OpenApi 2.0.0` ← `Microsoft.AspNetCore.OpenApi 10.0.3` — fix: **10.0.11**.
  - `SSH.NET 2025.1.0` ← `Testcontainers 4.10.0` — fix: Testcontainers(.PostgreSql/.Keycloak) → **4.14.0** (verify SSH.NET ≥ 2026.0.0 afterwards; if not, pin `SSH.NET` 2026.0.0 via transitive pinning).
- Local-only: SourceLink's `GetUntrackedFiles` crashes because the **global** git config has `core.excludesFile = .gitignore` (relative). Fix on the workstation: `git config --global --unset core.excludesFile` (or set an absolute path). Not a repo change.
- `Directory.Packages.props` exists but CPM is **off** (`ManagePackageVersionsCentrally` not set); every csproj pins versions inline, and per-project `<PackageReference Update="…Analyzer…" Version="…">` overrides exist in 10 csproj files.
- `Microsoft.Agents.AI 1.0.0-rc1` and `Microsoft.Agents.AI.Anthropic 1.0.0-rc1` are referenced by `Daedalus.Infrastructure` but **unused in code** (only comments). Remove them. Ralph uses the `Anthropic` SDK's `AsIChatClient` directly (`Anthropic 12.8.0` → bump to **12.40.0** to match Thalos), `Microsoft.Extensions.AI 10.3.0` → **10.9.0**, `ModelContextProtocol 1.0.0-rc.1` → **2.2.0** (same client API shape; `McpToolBuilder` compiles unchanged — verify).
- Conventions: primary constructors; `[LoggerMessage]` partial methods with per-class EventId ranges; CSharpFunctionalExtensions `Result` in Ralph code (Daedalus.Agents uses ZeroAlloc.Results at the Thalos boundary and only converts where an existing Daedalus interface demands CSFE); DTOs in `Daedalus.Application/DTOs`; `Web` references `Application` for DTOs; controllers `[ApiController][ApiVersion("1.0")][Route("api/[controller]")][Authorize]`, rate-limit policy `llm-operations`; `ApiJsonSerializerContext` lists response DTOs; integration tests use `[Collection(DatabaseCollection.Name)]` + `PostgresFixture` (Testcontainers) and instantiate controllers directly; Playwright browser tests use `E2EServerFixture` + stubs + PageObjects.
- ArchUnit rules live in `tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs`.
- Existing in-process MCP tools: `Infrastructure/Agents/Tools/DaedalusLearningsTools.SearchLearnings(...)` and `DaedalusFailurePatternsTools.SearchFailurePatterns(...)`, `[McpServerToolType]`, primary-ctor DI. Ralph keeps using them; Thalos gets thin `[ThalosToolType]` wrappers.

### 0.1b Verified Thalos.NET facts (post Plan A final review, HEAD `0f3a337`, feed `0.1.0-local.20260816183958`)

- `IAgentSessionStore` has **eight** members: the seven in the design plus `ValueTask<Result<bool, AgentError>> TryTransitionAsync(SessionId id, SessionState from, SessionState target, CancellationToken ct)` — an atomic compare-and-swap (`UPDATE … SET State=@target, LastActivityAt=@now WHERE Id=@id AND State=@from` → rows affected == 1 ⇒ `true`; unknown id ⇒ `SessionNotFound`). The runtime claims turns with it. `SessionStoreContractTests` (in `Thalos.NET.Testing`) covers it incl. 20 concurrent claims → exactly one `true`.
- Contract tests API: derive from `Thalos.Testing.SessionStoreContractTests` and implement `protected abstract ValueTask<IAgentSessionStore> CreateStoreAsync(TimeProvider clock)` — the store must read time from `clock` (a `FakeTimeProvider`) and persist ≥ 1 ms precision. **`PostgresAgentSessionStore` must therefore use the injected `TimeProvider` for `CreatedAt/LastActivityAt`.**
- Crash recovery: durable stores/hosts should reset stale `Running` sessions to `Idle` at startup (add a small startup step in Daedalus: `UPDATE AgentSessions SET State=0 WHERE State=1`).
- Non-owner access returns `SessionNotFound` (404), not `Unauthorized`; admin role literal `"admin"`. `AgentError.Detail` never carries raw exception text (type names only) — map it straight into ProblemDetails.
- `TurnFailedNotification`/`TurnFailedEvent` carry the accumulated `TurnUsage` (bill quarantined/failed turns).
- `RecordingNotificationPublisher` ships in `Thalos.NET.Testing` (use it in Daedalus tests).
- AI.Sentinel: **without `SentinelOptions.EmbeddingGenerator` only lexical detectors run** — Daedalus must set it to the Ollama `IEmbeddingGenerator<string, Embedding<float>>` (Task 9 wiring; use `PostConfigure`/direct assignment inside the `UseAISentinel` lambda via a captured `IServiceProvider`-free path — see Task 9 note). Sentinel is the innermost decorator; quarantine detail is `"<Severity>: <DetectorId>"`; rate limit → `ProviderError`.
- MCP: `AddMcpServersFromFile(absolutePath)`; keys must match `^[a-zA-Z0-9_-]+$` and contain no `__`; tools appear as `{name}__{tool}` (`roslyn__find_callers`); `shutdownTimeout` default 2 s; a source failure fails the agent build for that turn (retried next turn) — so a slow roslyn-codelens start shows up as `ProviderError` until connected.
- `AnthropicOptions.DefaultModel` = `claude-sonnet-5`; set explicitly in Daedalus config.
- `AgentEvent` kinds: `text-delta`, `tool-call`, `tool-result`, `usage` (one per turn), `done`, `error`.
- Lifecycle: `AgentFactory`/`AnthropicChatClientProvider` are `IDisposable`, `McpToolSource` `IAsyncDisposable` + `IDisposable`.

### 0.1c Follow-ups discovered during Tasks 5–7 (2026-08-16)

- **Integration suite pre-existing red**: ~15 test classes/fixtures build `DbContextOptions` with `UseNpgsql(cs)` and no `UseVector()` (plus InMemory + `Vector` cases; `AspirePostgresFixture`/docker-compose still on `postgres:16-alpine`) → 139 failures unrelated to this work. **Fix before Task 13**: add `PostgresFixture.CreateDbContextOptions()` (with `UseVector()`) and use it everywhere; switch InMemory-based tests to Postgres or ignore the `Embedding` mapping for InMemory; bump `AspirePostgresFixture`/compose images to `pgvector/pgvector:pg16`.
- **`AgentSession.RowVersion` is inert on Npgsql** (byte[] rowversion is never populated) — the store relies on atomic `ExecuteUpdateAsync` statements instead; follow-up: `UseXminAsConcurrencyToken()` (new migration) or drop the column.
- Crash recovery: reset stale `Running` sessions to `Idle` at API startup (Task 12).

### 0.2 Branching

Work on `feature/thalos-integration` off `main`. Small commits per task. Run `pre-push-review` before the final merge/PR. Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

### 0.3 Task map

| # | Task | Area |
|---|---|---|
| 1 | Workstation fix + branch | — |
| 2 | Enable CPM, consolidate versions | build |
| 3 | Fix vulnerable/dead package versions → build + unit tests green | build |
| 4 | Local feed + Thalos.NET package pins | build |
| 5 | Domain: `AgentSession`, `AgentMessage`, `AgentSessionState` | Domain |
| 6 | EF configurations, DbSets, migration `AddAgentSessions` | Infrastructure |
| 7 | `Daedalus.Agents` project + `PostgresAgentSessionStore` + contract tests | Agents |
| 8 | `DaedalusKnowledgeTools` (`[ThalosToolType]`) | Agents |
| 9 | Options + `HttpSecurityContextFactory` + `AddDaedalusAgents` composition root | Agents |
| 10 | DTOs + mapper (`Application/DTOs/Agents`, mapping in Agents) | Application/Agents |
| 11 | `AgentsController` + `AgentSessionsController` (REST + SSE) + ProblemDetails mapping | Api |
| 12 | Api `Program.cs` wiring, appsettings, `.mcp.json`, JSON context, auth policy | Api |
| 13 | Controller integration tests (Testcontainers + fake runtime) | tests |
| 14 | Web: `AgentApiClient` with SSE reader | Web |
| 15 | Web: `Agent.razor` + nav | Web |
| 16 | ArchUnit rules | tests |
| 17 | Playwright browser test (page object + scenario, stub runtime) | tests |
| 18 | Docs, regression test, pre-push review, merge | — |

---

## Task 1: Workstation fix + branch

**Step 1:** fix the SourceLink crash (workstation, not repo):
```powershell
git config --global --get core.excludesFile      # shows ".gitignore"
git config --global --unset core.excludesFile
```
**Step 2:** `git switch -c feature/thalos-integration`
**Step 3:** confirm the *only* remaining build failure is NuGet audit:
```powershell
dotnet build --nologo -p:NuGetAudit=false 2>&1 | Select-String -Pattern " error " | Select-Object -First 5
```
Expected: no output (build succeeds with audit off). If SourceLink still throws, stop and re-check step 1.

No commit (nothing changed in the repo).

---

## Task 2: Enable Central Package Management

**Files:**
- Modify: `Directory.Build.props` (add CPM flags; move analyzer versions out)
- Rewrite: `Directory.Packages.props`
- Modify: every `*.csproj` under `src/`, `tests/`, `benchmarks/` (strip `Version="…"` and `Update="…"` analyzer overrides)

**Step 1: Collect current versions** — run this in the repo root; it prints one `<PackageVersion>` line per package at the **highest** version currently used anywhere:

```powershell
$refs = Get-ChildItem -Recurse -Filter *.csproj | Where-Object FullName -notmatch '\\(bin|obj)\\' |
  ForEach-Object { Select-Xml -Path $_.FullName -XPath "//PackageReference[@Include or @Update]" } |
  ForEach-Object { [pscustomobject]@{ Id = ($_.Node.Include ?? $_.Node.Update); Version = $_.Node.Version } } |
  Where-Object Version
$refs | Group-Object Id | ForEach-Object {
  $v = ($_.Group.Version | Sort-Object { try { [version]($_ -replace '-.*$','') } catch { [version]'0.0' } } -Descending)[0]
  "    <PackageVersion Include=`"$($_.Name)`" Version=`"$v`" />"
} | Sort-Object
```

**Step 2: Write `Directory.Packages.props`** — paste the generated list, then **override these entries** to the fixed versions (Task 3 depends on them being here):

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <!-- Aspire (13.1.0 → 13.4.6 fixes MessagePack via StreamJsonRpc) -->
    <PackageVersion Include="Aspire.Hosting" Version="13.4.6" />
    <PackageVersion Include="Aspire.Hosting.AppHost" Version="13.4.6" />
    <PackageVersion Include="Aspire.Hosting.PostgreSQL" Version="13.4.6" />
    <PackageVersion Include="Aspire.Hosting.Keycloak" Version="13.4.6-preview.1.26319.6" />
    <PackageVersion Include="CommunityToolkit.Aspire.Hosting.Ollama" Version="13.4.1-beta.706" />
    <!-- OpenTelemetry 1.15.0 → 1.17.0 -->
    <PackageVersion Include="OpenTelemetry" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.SqlClient" Version="1.17.0" />
    <!-- OpenAPI -->
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.11" />
    <!-- Testcontainers 4.10.0 → 4.14.0 (SSH.NET) -->
    <PackageVersion Include="Testcontainers" Version="4.14.0" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.14.0" />
    <PackageVersion Include="Testcontainers.Keycloak" Version="4.14.0" />
    <!-- AI stack aligned with Thalos.NET -->
    <PackageVersion Include="Anthropic" Version="12.40.0" />
    <PackageVersion Include="Microsoft.Extensions.AI" Version="10.9.0" />
    <PackageVersion Include="Microsoft.Extensions.AI.Abstractions" Version="10.9.0" />
    <PackageVersion Include="ModelContextProtocol" Version="2.2.0" />
    <!-- Analyzers (highest of the per-project overrides) -->
    <PackageVersion Include="SonarAnalyzer.CSharp" Version="10.19.0.132793" />
    <PackageVersion Include="Meziantou.Analyzer" Version="2.0.296" />
    <PackageVersion Include="Microsoft.CodeAnalysis.NetAnalyzers" Version="10.0.102" />
    <!-- Thalos.NET (local feed during phase 1.1; see Task 4) -->
    <PackageVersion Include="Thalos.NET" Version="0.1.0-local.20260816183958" />
    <PackageVersion Include="Thalos.NET.Abstractions" Version="0.1.0-local.20260816183958" />
    <PackageVersion Include="Thalos.NET.Testing" Version="0.1.0-local.20260816183958" />
    <PackageVersion Include="Thalos.NET.Mcp" Version="0.1.0-local.20260816183958" />
    <PackageVersion Include="Thalos.NET.Anthropic" Version="0.1.0-local.20260816183958" />
    <PackageVersion Include="Thalos.NET.Sentinel" Version="0.1.0-local.20260816183958" />
    <PackageVersion Include="ZeroAlloc.Results" Version="1.2.0" />
    <PackageVersion Include="ZeroAlloc.Authorization" Version="2.1.0" />
    <PackageVersion Include="ZeroAlloc.Validation" Version="1.5.6" />
    <PackageVersion Include="ZeroAlloc.Validation.Generator" Version="1.5.6" />
    <PackageVersion Include="ZeroAlloc.Validation.AspNetCore" Version="1.5.6" />
    <PackageVersion Include="ZeroAlloc.Mapping" Version="1.6.1" />
    <!-- … all other generated entries … -->
  </ItemGroup>
</Project>
```
Remove `Microsoft.Agents.AI` and `Microsoft.Agents.AI.Anthropic` lines entirely (dead references). Delete the stale `FluentAssertions`, `xunit 2.8.1`-style leftovers only if no csproj references them (the generator only emits referenced ones, so just don't re-add them).

**Step 3: Strip versions from csproj files**

```powershell
Get-ChildItem -Recurse -Filter *.csproj | Where-Object FullName -notmatch '\\(bin|obj)\\' | ForEach-Object {
  $p = $_.FullName
  $c = Get-Content $p -Raw
  # drop per-project analyzer overrides (block form)
  $c = [regex]::Replace($c, '\s*<PackageReference Update="[^"]+" Version="[^"]+">.*?</PackageReference>', '', 'Singleline')
  # drop inline Version attributes
  $c = [regex]::Replace($c, '(<PackageReference Include="[^"]+")\s+Version="[^"]+"', '$1')
  Set-Content $p $c -NoNewline
}
```
Then in `Directory.Build.props` remove `Version="…"` from the three analyzer `PackageReference`s (CPM supplies them) and delete the `Microsoft.Agents.AI*` references from `src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`.

**Step 4: Restore + build**
```powershell
dotnet restore
dotnet build --nologo
```
Expected: **`0 Error(s)`** — the audit errors disappear with the bumped versions. Fix any `NU1008` ("PackageReference has Version but CPM is on") by removing the stray attribute; any `NU1010` ("missing PackageVersion") by adding the entry.

**Step 5: Unit tests**
```powershell
dotnet test --nologo --filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"
```
Expected: 739 pass (253 + 291 + 68 + 127).

**Step 6: Commit**
```powershell
git add -A
git commit -m "build: enable central package management with transitive pinning; consolidate versions

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 3: Package bumps — verify no behaviour change

Task 2 already applied the bumps; this task verifies them and adjusts code if an API moved.

**Step 1:** Aspire 13.4.6 — run the AppHost briefly (`dotnet run --project src/Daedalus.AppHost`, wait for the dashboard, Ctrl+C). If `AddOllama`/`AddKeycloak`/`WithRealmImport` signatures changed, fix `src/Daedalus.AppHost/Program.cs` minimally.
**Step 2:** ModelContextProtocol 2.2.0 — `McpToolBuilder` must compile as-is (`McpClient.CreateAsync`, `StdioClientTransport(options)`, `HttpClientTransport(options)`, `ListToolsAsync`). If a ctor now wants an `ILoggerFactory`, pass `null`.
**Step 3:** Anthropic 12.40 — `RalphAgentFactory.CreateChatClient` (`new AnthropicClient(new ClientOptions { ApiKey })` + `.AsIChatClient(model, maxTokens)`) must compile as-is.
**Step 4:** OpenTelemetry 1.17 — `ServiceDefaults` compiles unchanged.
**Step 5:** Integration tests (Docker required):
```powershell
dotnet test tests/Daedalus.Tests.Integration --nologo
```
Expected: pass (Testcontainers 4.14).
**Step 6:** Commit only if code changed: `fix: adapt to Aspire 13.4 / MCP 2.2 API changes`.

---

## Task 4: Local feed + Thalos.NET pins

**Files:**
- Create: `nuget.config` (repo root)
- Modify: `Directory.Packages.props` (replace `0.1.0-local.20260816183958`)

`nuget.config`
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="thalos-local" value="C:\Projects\Prive\.nuget-local" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
    <packageSource key="thalos-local"><package pattern="Thalos.NET*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

> CI cannot see `C:\Projects\Prive\.nuget-local`. Until Thalos.NET 0.1.0 is on nuget.org, CI restores fail. Two options — pick **(a)**: (a) commit the six `.nupkg` files under `packages-local/` and point the source at `$(MSBuildThisFileDirectory)packages-local` (relative path works in `nuget.config`); delete the folder when switching to nuget.org at phase end. (b) skip CI on this branch. Use (a): `New-Item -ItemType Directory packages-local; Copy-Item C:\Projects\Prive\.nuget-local\Thalos.NET*.nupkg packages-local\` and set `value="packages-local"`.

Replace every `0.1.0-local.20260816183958` with the exact version produced by Plan A's `pack-local.ps1`. `dotnet restore` → succeeds. Commit `build: add local Thalos.NET feed and package pins`.

---

## Task 5: Domain — `AgentSession`, `AgentMessage`, `AgentSessionState`

Domain must not reference Thalos (ArchUnit). Ids are `Guid` (Thalos typed ids are Guid-backed; the adapter converts).

**Files:**
- Create: `src/Daedalus.Domain/Entities/AgentSessionState.cs`
- Create: `src/Daedalus.Domain/Entities/AgentSession.cs`
- Create: `src/Daedalus.Domain/Entities/AgentMessage.cs`
- Test: `tests/Daedalus.Tests.Unit.Domain/Entities/AgentSessionTests.cs`

**Step 1: Failing tests**

```csharp
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

public sealed class AgentSessionTests
{
    [Fact]
    public void Create_sets_defaults()
    {
        var s = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), "alice", DateTime.UtcNow).Value;
        s.State.Should().Be(AgentSessionState.Idle);
        s.TurnCount.Should().Be(0);
        s.TotalInputTokens.Should().Be(0);
        s.OwnerId.Should().Be("alice");
    }

    [Fact]
    public void Create_requires_owner_and_ids()
    {
        AgentSession.Create(Guid.Empty, Guid.NewGuid(), "a", DateTime.UtcNow).IsFailure.Should().BeTrue();
        AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), " ", DateTime.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void RecordTurn_accumulates_and_bumps_activity()
    {
        var t0 = new DateTime(2026, 8, 16, 10, 0, 0, DateTimeKind.Utc);
        var s = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), "a", t0).Value;
        s.RecordTurn(100, 20, t0.AddMinutes(1));
        s.RecordTurn(50, 10, t0.AddMinutes(2));
        s.TurnCount.Should().Be(2);
        s.TotalInputTokens.Should().Be(150);
        s.TotalOutputTokens.Should().Be(30);
        s.LastActivityAt.Should().Be(t0.AddMinutes(2));
    }

    [Fact]
    public void SetState_updates_state_and_activity()
    {
        var s = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), "a", DateTime.UtcNow).Value;
        var later = DateTime.UtcNow.AddSeconds(5);
        s.SetState(AgentSessionState.Running, later);
        s.State.Should().Be(AgentSessionState.Running);
        s.LastActivityAt.Should().Be(later);
    }

    [Fact]
    public void AgentMessage_requires_content()
    {
        AgentMessage.Create(Guid.NewGuid(), 0, "user", "", null, null, null, DateTime.UtcNow).IsFailure.Should().BeTrue();
        AgentMessage.Create(Guid.NewGuid(), 0, "assistant", "{\"role\":\"assistant\"}", 10, 2, "m", DateTime.UtcNow).IsSuccess.Should().BeTrue();
    }
}
```

**Step 2: Run → fails.**

**Step 3: Implement**

`AgentSessionState.cs`
```csharp
namespace Daedalus.Domain.Entities;

/// <summary>Mirror of Thalos.SessionState kept in Domain so Domain stays framework-free.</summary>
public enum AgentSessionState
{
    Idle = 0,
    Running = 1,
    AwaitingApproval = 2,
    Closed = 3,
}
```

`AgentSession.cs`
```csharp
#pragma warning disable CA1819 // EF Core concurrency token standard pattern
#pragma warning disable S1144 // EF Core sets RowVersion via reflection

using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>Header of a Thalos agent conversation. Messages live in <see cref="AgentMessage"/>.</summary>
public sealed class AgentSession : AggregateRoot<Guid>
{
    public Guid AgentId { get; private set; }
    public string OwnerId { get; private set; } = string.Empty;
    public AgentSessionState State { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime LastActivityAt { get; private set; }
    public int TurnCount { get; private set; }
    public long TotalInputTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }
    public byte[]? RowVersion { get; private set; }

    private AgentSession() { } // EF Core

    public static Result<AgentSession> Create(Guid id, Guid agentId, string ownerId, DateTime utcNow)
    {
        if (id == Guid.Empty) return Result.Failure<AgentSession>("Session id is required.");
        if (agentId == Guid.Empty) return Result.Failure<AgentSession>("Agent id is required.");
        if (string.IsNullOrWhiteSpace(ownerId)) return Result.Failure<AgentSession>("Owner id is required.");

        return Result.Success(new AgentSession
        {
            Id = id,
            AgentId = agentId,
            OwnerId = ownerId,
            State = AgentSessionState.Idle,
            CreatedAt = utcNow,
            LastActivityAt = utcNow,
        });
    }

    public void RecordTurn(int inputTokens, int outputTokens, DateTime utcNow)
    {
        TurnCount++;
        TotalInputTokens += inputTokens;
        TotalOutputTokens += outputTokens;
        LastActivityAt = utcNow;
    }

    public void SetState(AgentSessionState state, DateTime utcNow)
    {
        State = state;
        LastActivityAt = utcNow;
    }
}
```

`AgentMessage.cs`
```csharp
using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
/// One Microsoft.Extensions.AI ChatMessage, stored as JSON (<see cref="ContentJson"/>) so tool-call/result
/// content round-trips exactly. <see cref="Role"/> and token columns are denormalised for querying/costs.
/// </summary>
public sealed class AgentMessage : Entity<Guid>
{
    public Guid SessionId { get; private set; }
    public int Sequence { get; private set; }
    public string Role { get; private set; } = string.Empty;
    public string ContentJson { get; private set; } = string.Empty;
    public int? InputTokens { get; private set; }
    public int? OutputTokens { get; private set; }
    public string? ModelId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private AgentMessage() { } // EF Core

    public static Result<AgentMessage> Create(Guid sessionId, int sequence, string role, string contentJson, int? inputTokens, int? outputTokens, string? modelId, DateTime utcNow)
    {
        if (sessionId == Guid.Empty) return Result.Failure<AgentMessage>("Session id is required.");
        if (sequence < 0) return Result.Failure<AgentMessage>("Sequence must be non-negative.");
        if (string.IsNullOrWhiteSpace(role)) return Result.Failure<AgentMessage>("Role is required.");
        if (string.IsNullOrWhiteSpace(contentJson)) return Result.Failure<AgentMessage>("Content is required.");

        return Result.Success(new AgentMessage
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Sequence = sequence,
            Role = role,
            ContentJson = contentJson,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ModelId = modelId,
            CreatedAt = utcNow,
        });
    }
}
```

**Step 4: Run tests → 5 pass. Step 5: Commit** `feat(domain): AgentSession and AgentMessage aggregates for Thalos sessions`.

---

## Task 6: EF configuration + migration

**Files:**
- Create: `src/Daedalus.Infrastructure/Persistence/Configurations/AgentSessionConfiguration.cs`
- Create: `src/Daedalus.Infrastructure/Persistence/Configurations/AgentMessageConfiguration.cs`
- Modify: `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs` (two `DbSet`s)
- Generate: `src/Daedalus.Infrastructure/Migrations/2026MMDDhhmmss_AddAgentSessions.cs` (+ Designer, snapshot)

`AgentSessionConfiguration.cs`
```csharp
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

internal sealed class AgentSessionConfiguration : IEntityTypeConfiguration<AgentSession>
{
    public void Configure(EntityTypeBuilder<AgentSession> builder)
    {
        builder.ToTable("AgentSessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.AgentId).IsRequired();
        builder.Property(s => s.OwnerId).IsRequired().HasMaxLength(256);
        builder.Property(s => s.State).IsRequired().HasConversion<int>();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.LastActivityAt).IsRequired();
        builder.Property(s => s.TurnCount).IsRequired();
        builder.Property(s => s.TotalInputTokens).IsRequired();
        builder.Property(s => s.TotalOutputTokens).IsRequired();
        builder.Property(s => s.RowVersion).IsRowVersion();
        builder.HasIndex(s => new { s.OwnerId, s.CreatedAt }).HasDatabaseName("IX_AgentSession_Owner_CreatedAt");
        builder.HasIndex(s => s.AgentId).HasDatabaseName("IX_AgentSession_AgentId");
    }
}
```

`AgentMessageConfiguration.cs`
```csharp
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

internal sealed class AgentMessageConfiguration : IEntityTypeConfiguration<AgentMessage>
{
    public void Configure(EntityTypeBuilder<AgentMessage> builder)
    {
        builder.ToTable("AgentMessages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.SessionId).IsRequired();
        builder.Property(m => m.Sequence).IsRequired();
        builder.Property(m => m.Role).IsRequired().HasMaxLength(32);
        builder.Property(m => m.ContentJson).IsRequired().HasColumnType("jsonb");
        builder.Property(m => m.ModelId).HasMaxLength(128);
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.HasOne<AgentSession>().WithMany().HasForeignKey(m => m.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(m => new { m.SessionId, m.Sequence }).IsUnique().HasDatabaseName("IX_AgentMessage_Session_Sequence");
    }
}
```

`ApplicationDbContext.cs` — add:
```csharp
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<AgentMessage> AgentMessages => Set<AgentMessage>();
```

**Migration** (uses the existing design-time factory):
```powershell
dotnet ef migrations add AddAgentSessions --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations
```
Open the generated migration; verify it creates `AgentSessions`, `AgentMessages`, the FK, and the two indexes, and nothing else (a diff to Ralph tables means the snapshot was stale — investigate, don't ship).

Build; run unit tests; commit `feat(infrastructure): EF configuration and migration for AgentSessions/AgentMessages`.

---

## Task 7: `Daedalus.Agents` project + `PostgresAgentSessionStore` + contract tests

**Files:**
- Create: `src/Daedalus.Agents/Daedalus.Agents.csproj`
- Create: `src/Daedalus.Agents/Sessions/PostgresAgentSessionStore.cs`
- Create: `tests/Daedalus.Tests.Integration/Agents/PostgresAgentSessionStoreTests.cs`
- Modify: `Daedalus.sln` (add project), `tests/Daedalus.Tests.Integration/Daedalus.Tests.Integration.csproj` (ref `Daedalus.Agents` + `Thalos.NET.Testing`)

`src/Daedalus.Agents/Daedalus.Agents.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Daedalus.Agents</RootNamespace>
    <!-- Thalos/MAF are not trim-safe yet; this project is never AOT-published -->
    <IsTrimmable>false</IsTrimmable>
    <EnableTrimAnalyzer>false</EnableTrimAnalyzer>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Daedalus.Domain\Daedalus.Domain.csproj" />
    <ProjectReference Include="..\Daedalus.Application\Daedalus.Application.csproj" />
    <ProjectReference Include="..\Daedalus.Infrastructure\Daedalus.Infrastructure.csproj" />
    <PackageReference Include="Thalos.NET" />
    <PackageReference Include="Thalos.NET.Mcp" />
    <PackageReference Include="Thalos.NET.Anthropic" />
    <PackageReference Include="Thalos.NET.Sentinel" />
    <PackageReference Include="ZeroAlloc.Mapping" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.Extensions.AI" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" />
    <PackageReference Include="Microsoft.Extensions.Options" />
  </ItemGroup>
</Project>
```
`dotnet sln Daedalus.sln add src/Daedalus.Agents/Daedalus.Agents.csproj`.

**Step 1: Failing test** — reuse Thalos's contract suite.

`tests/Daedalus.Tests.Integration/Agents/PostgresAgentSessionStoreTests.cs`
```csharp
using Daedalus.Agents.Sessions;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Testing;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>Runs Thalos.NET's IAgentSessionStore contract against the real Postgres-backed store.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresAgentSessionStoreTests(PostgresFixture fixture) : SessionStoreContractTests, IAsyncLifetime
{
    private readonly List<ApplicationDbContext> _contexts = [];

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public async Task DisposeAsync()
    {
        foreach (var c in _contexts) await c.DisposeAsync();
    }

    protected override ValueTask<IAgentSessionStore> CreateStoreAsync(TimeProvider clock)
    {
        var factory = new TestDbContextFactory(fixture.ConnectionString, _contexts);
        return new(new PostgresAgentSessionStore(factory, clock)); // the contract suite drives a FakeTimeProvider
    }

    private sealed class TestDbContextFactory(string connectionString, List<ApplicationDbContext> track) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            var ctx = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
            lock (track) track.Add(ctx);
            return ctx;
        }
    }
}
```

> Ensure the fixture's database has the new migration applied — `PostgresFixture` runs migrations on startup (check `PostgresFixture.InitializeAsync`; if it uses `EnsureCreated`, that's fine too — both create the new tables). Respawn's reset must include the new tables (it discovers them automatically).

**Step 2: Run → fails.**

**Step 3: Implement** `src/Daedalus.Agents/Sessions/PostgresAgentSessionStore.cs`

```csharp
using System.Text.Json;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Sessions;

/// <summary>
/// Thalos session store over <see cref="ApplicationDbContext"/>. Uses a fresh short-lived DbContext per call
/// (the store is a singleton inside Thalos; DbContexts are not).
/// </summary>
public sealed class PostgresAgentSessionStore(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock) : IAgentSessionStore
{
    private static readonly JsonSerializerOptions Json = AIJsonUtilities.DefaultOptions;

    public async ValueTask<Result<AgentSessionRecord, AgentError>> CreateAsync(AgentId agentId, string ownerId, CancellationToken ct)
    {
        var id = SessionId.New();
        var created = AgentSession.Create(id.Value, agentId.Value, ownerId, clock.GetUtcNow().UtcDateTime);
        if (created.IsFailure)
        {
            return Result<AgentSessionRecord, AgentError>.Failure(AgentError.Validation(created.Error));
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.AgentSessions.Add(created.Value);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<AgentSessionRecord, AgentError>.Success(ToRecord(created.Value));
    }

    public async ValueTask<Result<AgentSessionRecord, AgentError>> GetAsync(SessionId id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var s = await db.AgentSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value, ct).ConfigureAwait(false);
        return s is null
            ? Result<AgentSessionRecord, AgentError>.Failure(AgentError.SessionNotFound(id))
            : Result<AgentSessionRecord, AgentError>.Success(ToRecord(s));
    }

    public async ValueTask<Result<IReadOnlyList<AgentSessionRecord>, AgentError>> ListAsync(string ownerId, int skip, int take, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.OwnerId == ownerId)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Skip(skip).Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<AgentSessionRecord>, AgentError>.Success(rows.Select(ToRecord).ToList());
    }

    public async ValueTask<Result<IReadOnlyList<ChatMessage>, AgentError>> LoadMessagesAsync(SessionId id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.AgentSessions.AnyAsync(s => s.Id == id.Value, ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<ChatMessage>, AgentError>.Failure(AgentError.SessionNotFound(id));
        }

        var rows = await db.AgentMessages.AsNoTracking()
            .Where(m => m.SessionId == id.Value)
            .OrderBy(m => m.Sequence)
            .Select(m => m.ContentJson)
            .ToListAsync(ct).ConfigureAwait(false);

        var messages = new List<ChatMessage>(rows.Count);
        foreach (var json in rows)
        {
            var msg = JsonSerializer.Deserialize<ChatMessage>(json, Json);
            if (msg is not null) messages.Add(msg);
        }
        return Result<IReadOnlyList<ChatMessage>, AgentError>.Success(messages);
    }

    public async ValueTask<UnitResult<AgentError>> AppendMessagesAsync(SessionId id, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
        if (session is null)
        {
            return UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id));
        }

        var next = await db.AgentMessages.Where(m => m.SessionId == id.Value).Select(m => (int?)m.Sequence).MaxAsync(ct).ConfigureAwait(false) ?? -1;
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var message in messages)
        {
            next++;
            var usage = message.Contents.OfType<UsageContent>().FirstOrDefault()?.Details;
            var entity = AgentMessage.Create(id.Value, next, message.Role.Value, JsonSerializer.Serialize(message, Json),
                (int?)usage?.InputTokenCount, (int?)usage?.OutputTokenCount, null, now);
            if (entity.IsFailure)
            {
                return UnitResult<AgentError>.Failure(AgentError.StoreError(entity.Error));
            }
            db.AgentMessages.Add(entity.Value);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return UnitResult<AgentError>.Success();
    }

    public async ValueTask<UnitResult<AgentError>> RecordTurnAsync(SessionId id, TurnUsage usage, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
        if (session is null) return UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id));

        session.RecordTurn(usage.InputTokens, usage.OutputTokens, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return UnitResult<AgentError>.Success();
    }

    public async ValueTask<UnitResult<AgentError>> UpdateStateAsync(SessionId id, SessionState state, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
        if (session is null) return UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id));

        session.SetState((AgentSessionState)(int)state, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return UnitResult<AgentError>.Success();
    }

    /// <summary>Atomic compare-and-swap used by the runtime to claim a turn (see IAgentSessionStore remarks).</summary>
    public async ValueTask<Result<bool, AgentError>> TryTransitionAsync(SessionId id, SessionState from, SessionState target, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var fromState = (AgentSessionState)(int)from;
        var targetState = (AgentSessionState)(int)target;

        // Single UPDATE … WHERE State = @from — atomic without a transaction or row lock.
        var affected = await db.AgentSessions
            .Where(s => s.Id == id.Value && s.State == fromState)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.State, targetState)
                .SetProperty(s => s.LastActivityAt, now), ct)
            .ConfigureAwait(false);

        if (affected == 1)
        {
            return Result<bool, AgentError>.Success(true);
        }

        var exists = await db.AgentSessions.AsNoTracking().AnyAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
        return exists
            ? Result<bool, AgentError>.Success(false)
            : Result<bool, AgentError>.Failure(AgentError.SessionNotFound(id));
    }

    private static AgentSessionRecord ToRecord(AgentSession s) => new(
        new SessionId(s.Id), new AgentId(s.AgentId), s.OwnerId, (SessionState)(int)s.State,
        new DateTimeOffset(s.CreatedAt, TimeSpan.Zero), new DateTimeOffset(s.LastActivityAt, TimeSpan.Zero),
        s.TurnCount, s.TotalInputTokens, s.TotalOutputTokens);
}
```

> `SessionState` (Thalos) and `AgentSessionState` (Domain) share integer values by construction (Idle=0, Running=1, AwaitingApproval=2, Closed=3) — add a unit test in `Daedalus.Tests.Unit.Application`? No: put a 5-line xUnit fact in the integration test file asserting `Enum.GetValues<SessionState>().Select(v => (int)v)` equals the Domain enum's values, so a drift breaks loudly.
> Concurrency: `RowVersion` makes concurrent `UpdateStateAsync` calls throw `DbUpdateConcurrencyException`; catch it in `UpdateStateAsync`/`RecordTurnAsync` and return `AgentError.SessionBusy(id)` — add that catch, and a test that two parallel `UpdateStateAsync` calls don't both succeed silently is optional for phase 1.1.

`IDbContextFactory<ApplicationDbContext>` — check `AddApplicationDatabase` in ServiceDefaults registers a pooled factory (`AddPooledDbContextFactory` or `AddDbContextFactory`). If it only registers `AddDbContextPool`, add `services.AddDbContextFactory<ApplicationDbContext>(...)` alongside (same options) in `ServiceDefaults`.

**Step 4: Run** `dotnet test tests/Daedalus.Tests.Integration --filter "FullyQualifiedName~PostgresAgentSessionStore"` → 7 contract tests pass. **Step 5: Commit** `feat(agents): PostgresAgentSessionStore passing Thalos contract tests`.

---

## Task 8: `DaedalusKnowledgeTools`

**Files:**
- Create: `src/Daedalus.Agents/Tools/DaedalusKnowledgeTools.cs`
- Test: `tests/Daedalus.Tests.Unit.Application/Agents/DaedalusKnowledgeToolsTests.cs` (add refs to `Daedalus.Agents` + `Thalos.NET`)

Look at `DaedalusLearningsTools.SearchLearnings` and `DaedalusFailurePatternsTools.SearchFailurePatterns` signatures; delegate to the same underlying services (`ILearningsRepository`/`IFailurePatternDatabase`, or simply new up the existing MCP tool classes via DI and call them). Shape:

```csharp
using System.ComponentModel;
using Daedalus.Infrastructure.Agents.Tools;
using Thalos;

namespace Daedalus.Agents.Tools;

/// <summary>Daedalus knowledge exposed to Thalos agents (source name "daedalus": daedalus__search_learnings, daedalus__search_failure_patterns).</summary>
[ThalosToolType]
public sealed class DaedalusKnowledgeTools(DaedalusLearningsTools learnings, DaedalusFailurePatternsTools failures)
{
    [ThalosTool("search_learnings")]
    [Description("Semantic search over structured learnings from previous Daedalus tasks. Returns top matches with category and severity.")]
    public Task<string> SearchLearnings([Description("Natural-language query")] string query, [Description("Max results (1-20)")] int limit = 5, CancellationToken ct = default)
        => learnings.SearchLearnings(query, limit, ct);   // adapt to the real signature

    [ThalosTool("search_failure_patterns")]
    [Description("Finds known failure patterns similar to the description.")]
    public Task<string> SearchFailurePatterns([Description("Error or symptom description")] string description, [Description("Max results (1-20)")] int limit = 5, CancellationToken ct = default)
        => failures.SearchFailurePatterns(description, limit, ct); // adapt to the real signature
}
```
Register the two Infrastructure tool classes as scoped services if not already (`services.AddScoped<DaedalusLearningsTools>()` etc.) in `AddDaedalusAgents` (Task 9). Unit test: construct with NSubstitute'd inner tools, assert delegation. Commit `feat(agents): DaedalusKnowledgeTools exposing learnings/failure-pattern search to Thalos agents`.

---

## Task 9: Options, `HttpSecurityContextFactory`, `AddDaedalusAgents`

**Files:**
- Create: `src/Daedalus.Agents/DaedalusAgentsOptions.cs`
- Create: `src/Daedalus.Agents/Security/DeveloperPolicy.cs`
- Create: `src/Daedalus.Agents/Security/ClaimsSecurityContext.cs`
- Create: `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs`
- Test: `tests/Daedalus.Tests.Unit.Application/Agents/DaedalusAgentsRegistrationTests.cs`

`DaedalusAgentsOptions.cs`
```csharp
namespace Daedalus.Agents;

/// <summary>Bound from "Thalos". Agent definitions are declared in configuration so no redeploy is needed to add one.</summary>
public sealed class DaedalusAgentsOptions
{
    public const string SectionName = "Thalos";

    public string McpConfigPath { get; set; } = ".mcp.json";
    public List<AgentConfig> Agents { get; } = [];
    public List<ToolPolicyConfig> ToolPolicies { get; } = [];
    public SentinelConfig Sentinel { get; } = new();

    public sealed class AgentConfig
    {
        public string Id { get; set; } = "";            // ULID or GUID string
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Instructions { get; set; } = "";
        public string? Model { get; set; }
        public int? MaxOutputTokens { get; set; }
        public List<string> Tools { get; } = ["*"];
    }

    public sealed class ToolPolicyConfig
    {
        public string Pattern { get; set; } = "";
        public string Policy { get; set; } = "";
    }

    public sealed class SentinelConfig
    {
        public bool Enabled { get; set; } = true;
        public string OnCritical { get; set; } = "Quarantine";
        public string OnHigh { get; set; } = "Alert";
        public string OnMedium { get; set; } = "Log";
        public string OnLow { get; set; } = "Log";
        public List<string> DisabledDetectors { get; } = [];
    }
}
```

`Security/ClaimsSecurityContext.cs`
```csharp
using System.Security.Claims;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Security;

/// <summary>ISecurityContext from a JWT principal: Id = sub (or name), Roles = realm/role claims.</summary>
public sealed class ClaimsSecurityContext : ISecurityContext
{
    public ClaimsSecurityContext(ClaimsPrincipal principal)
    {
        Id = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.Identity?.Name ?? AnonymousSecurityContext.AnonymousId;
        Roles = principal.FindAll(c => c.Type is ClaimTypes.Role or "role" or "roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        Claims = principal.Claims.GroupBy(c => c.Type, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
    }

    public string Id { get; }
    public IReadOnlySet<string> Roles { get; }
    public IReadOnlyDictionary<string, string> Claims { get; }
}
```
> Keycloak puts realm roles under `realm_access.roles`; Daedalus's JWT setup already maps them (policies like `RequireRole("admin")` work today) — check `Program.cs` `TokenValidationParameters.RoleClaimType` and use the same claim type here.

`Security/DeveloperPolicy.cs`
```csharp
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Security;

[Policy("developer")]
public sealed class DeveloperPolicy : IAuthorizationPolicy
{
    public ValueTask<UnitResult<AuthorizationFailure>> EvaluateAsync(ISecurityContext ctx, CancellationToken ct = default) =>
        new(ctx.Roles.Contains("developer") || ctx.Roles.Contains("admin")
            ? UnitResult<AuthorizationFailure>.Success()
            : UnitResult<AuthorizationFailure>.Failure(new AuthorizationFailure("role", "developer or admin role required")));
}
```

`DaedalusAgentsServiceCollectionExtensions.cs`
```csharp
using AI.Sentinel;
using Daedalus.Agents.Security;
using Daedalus.Agents.Sessions;
using Daedalus.Agents.Tools;
using Daedalus.Infrastructure.Agents.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Anthropic;
using Thalos.Mcp;
using Thalos.Sentinel;

namespace Daedalus.Agents;

public static class DaedalusAgentsServiceCollectionExtensions
{
    /// <summary>Composition root for the Thalos-based agent stack. Ralph registrations are untouched.</summary>
    public static IServiceCollection AddDaedalusAgents(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        services.AddScoped<DaedalusLearningsTools>();
        services.AddScoped<DaedalusFailurePatternsTools>();

        services.AddThalos(thalos =>
        {
            thalos.UseAnthropic(configuration)
                  .UseSessionStore<PostgresAgentSessionStore>()
                  .AddLocalTools("daedalus", typeof(DaedalusKnowledgeTools))
                  .AddMcpServersFromFile(Path.IsPathRooted(options.McpConfigPath) ? options.McpConfigPath : Path.Combine(environment.ContentRootPath, options.McpConfigPath))
                  .AddPolicy<DeveloperPolicy>();

            foreach (var p in options.ToolPolicies) thalos.RequireToolPolicy(p.Pattern, p.Policy);

            foreach (var a in options.Agents)
            {
                thalos.AddAgent(new AgentDefinition
                {
                    Id = ParseAgentId(a.Id, a.Name),
                    Name = a.Name,
                    Description = a.Description,
                    Instructions = a.Instructions,
                    Model = a.Model,
                    MaxOutputTokens = a.MaxOutputTokens,
                    Tools = a.Tools,
                });
            }

            if (options.Sentinel.Enabled)
            {
                thalos.UseAISentinel(o =>
                {
                    o.OnCritical = Enum.Parse<SentinelAction>(options.Sentinel.OnCritical, ignoreCase: true);
                    o.OnHigh = Enum.Parse<SentinelAction>(options.Sentinel.OnHigh, ignoreCase: true);
                    o.OnMedium = Enum.Parse<SentinelAction>(options.Sentinel.OnMedium, ignoreCase: true);
                    o.OnLow = Enum.Parse<SentinelAction>(options.Sentinel.OnLow, ignoreCase: true);
                });
            }
        });

        // Semantic detectors light up automatically when the API host registered an Ollama IEmbeddingGenerator.
        services.AddOptions<SentinelOptions>().PostConfigure<IServiceProvider>((o, sp) =>
            o.EmbeddingGenerator ??= sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>());

        return services;
    }

    private static AgentId ParseAgentId(string raw, string name)
    {
        if (AgentId.TryParse(raw, null, out var id)) return id;
        if (Guid.TryParse(raw, out var guid)) return new AgentId(guid);
        throw new InvalidOperationException($"Agent '{name}': Id '{raw}' is not a ULID or GUID.");
    }
}
```
> `AddAISentinel(configure)` uses its own options object, not `IOptions<SentinelOptions>` — check `AI.Sentinel.ServiceCollectionExtensions` (reflected in Plan A §0.1: `AddAISentinel(Action<SentinelOptions>?)`). If it does not go through the Options pattern, resolve the embedding generator inside the `UseAISentinel` lambda instead: capture `services.BuildServiceProvider()`? **No** — never build a provider during registration. Instead: register the Ollama generator *before* `AddDaedalusAgents` in `Program.cs` (it already is, conditionally) and read it lazily via a small `IConfigureOptions<SentinelOptions>` if Sentinel supports it; otherwise leave semantic detectors off in phase 1.1 and note it in the commit.

Test: `services.AddLogging(); services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>()); services.AddDaedalusAgents(config, env)` with an in-memory `IConfiguration` containing one agent → `GetRequiredService<IAgentCatalog>().Agents` has one entry with the parsed id; `GetRequiredService<IAgentSessionStore>()` is the telemetry proxy over `PostgresAgentSessionStore`; missing `ANTHROPIC_API_KEY` doesn't throw at registration.

Commit `feat(agents): options, claims security context, developer policy, AddDaedalusAgents composition root`.

---

## Task 10: DTOs + mapper

**Files:**
- Create: `src/Daedalus.Application/DTOs/Agents/AgentDtos.cs`
- Create: `src/Daedalus.Agents/Api/AgentDtoMapper.cs`
- Test: `tests/Daedalus.Tests.Unit.Application/Agents/AgentDtoMapperTests.cs`

`AgentDtos.cs` (plain records; `Application` stays Thalos-free)
```csharp
namespace Daedalus.Application.DTOs.Agents;

public sealed record AgentSummaryDto(string Id, string Name, string Description, IReadOnlyList<string> Tools);
public sealed record AgentSessionDto(string Id, string AgentId, string OwnerId, string State, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, int TurnCount, long TotalInputTokens, long TotalOutputTokens);
public sealed record AgentMessageDto(string Role, string Text, IReadOnlyList<AgentToolCallDto> ToolCalls, DateTimeOffset? CreatedAt);
public sealed record AgentToolCallDto(string CallId, string ToolName, string? ArgumentsJson, string? ResultPreview);
public sealed record AgentSessionDetailDto(AgentSessionDto Session, IReadOnlyList<AgentMessageDto> Messages);
public sealed record CreateAgentSessionResponseDto(string SessionId);
public sealed record SendTurnRequestDto(string Text);
public sealed record TurnUsageDto(int InputTokens, int OutputTokens, string ModelId);
public sealed record AgentTurnResultDto(string TurnId, string Text, TurnUsageDto Usage, IReadOnlyList<AgentToolCallDto> ToolCalls, double ElapsedMs);
/// <summary>SSE payload. <c>Kind</c> mirrors the SSE event name: text-delta | tool-call | tool-result | usage | done | error.</summary>
public sealed record AgentEventDto(string Kind, string? Text = null, AgentToolCallDto? ToolCall = null, TurnUsageDto? Usage = null, AgentTurnResultDto? Result = null, string? ErrorCode = null, string? ErrorMessage = null, string? ErrorDetail = null);
```

`AgentDtoMapper.cs` — hand-written static mapper (ZeroAlloc.Mapping is source-generated for Command→Domain→DTO shapes; these are Thalos records → DTOs with string ids, so keep it explicit and small):
```csharp
using Daedalus.Application.DTOs.Agents;
using Microsoft.Extensions.AI;
using Thalos;

namespace Daedalus.Agents.Api;

public static class AgentDtoMapper
{
    public static AgentSummaryDto ToDto(AgentDefinition d) => new(d.Id.ToString(), d.Name, d.Description, d.Tools);
    public static AgentSessionDto ToDto(AgentSessionRecord r) => new(r.Id.ToString(), r.AgentId.ToString(), r.OwnerId, r.State.ToString(), r.CreatedAt, r.LastActivityAt, r.TurnCount, r.TotalInputTokens, r.TotalOutputTokens);
    public static TurnUsageDto ToDto(TurnUsage u) => new(u.InputTokens, u.OutputTokens, u.ModelId);
    public static AgentToolCallDto ToDto(ToolCallSummary c) => new(c.Id.ToString(), c.ToolName, c.ArgumentsJson, c.ResultPreview);
    public static AgentTurnResultDto ToDto(AgentTurnResult r) => new(r.TurnId.ToString(), r.Text, ToDto(r.Usage), r.ToolCalls.Select(ToDto).ToList(), r.Elapsed.TotalMilliseconds);

    /// <summary>Collapses stored ChatMessages into display messages: user/assistant text + tool calls with their results.</summary>
    public static IReadOnlyList<AgentMessageDto> ToDtos(IReadOnlyList<ChatMessage> messages)
    {
        var results = messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToDictionary(r => r.CallId, r => r.Result?.ToString());
        var list = new List<AgentMessageDto>();
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.Tool) continue; // folded into the assistant message that called it
            var calls = m.Contents.OfType<FunctionCallContent>()
                .Select(c => new AgentToolCallDto(c.CallId, c.Name, c.Arguments is null ? null : System.Text.Json.JsonSerializer.Serialize(c.Arguments), results.GetValueOrDefault(c.CallId)))
                .ToList();
            if (string.IsNullOrEmpty(m.Text) && calls.Count == 0) continue;
            list.Add(new AgentMessageDto(m.Role.Value, m.Text, calls, m.CreatedAt));
        }
        return list;
    }

    public static AgentEventDto ToDto(AgentEvent e) => e switch
    {
        TextDeltaEvent t => new(t.Kind, Text: t.Text),
        ToolCallStartedEvent c => new(c.Kind, ToolCall: new AgentToolCallDto(c.CallId.ToString(), c.ToolName, c.ArgumentsJson, null)),
        ToolCallFinishedEvent f => new(f.Kind, ToolCall: new AgentToolCallDto(f.CallId.ToString(), f.ToolName, null, f.ResultPreview)),
        UsageEvent u => new(u.Kind, Usage: ToDto(u.Usage)),
        TurnCompletedEvent d => new(d.Kind, Result: ToDto(d.Result)),
        TurnFailedEvent x => new(x.Kind, ErrorCode: x.Error.Code.ToString(), ErrorMessage: x.Error.Message, ErrorDetail: x.Error.Detail),
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };
}
```
Tests: round-trip a user/assistant/tool/assistant message list into 2 DTOs (assistant with one tool call whose `ResultPreview` came from the tool message); each `AgentEvent` subtype maps to the right `Kind`. Commit `feat(agents): agent DTOs and mapper`.

---

## Task 11: `AgentsController` + `AgentSessionsController` (REST + SSE)

**Files:**
- Create: `src/Daedalus.Api/Controllers/AgentsController.cs`
- Create: `src/Daedalus.Api/Controllers/AgentSessionsController.cs`
- Create: `src/Daedalus.Api/Agents/AgentErrorResults.cs`
- Modify: `src/Daedalus.Api/Daedalus.Api.csproj` (ref `Daedalus.Agents`, `ZeroAlloc.Validation.AspNetCore` optional)

`Agents/AgentErrorResults.cs` — one place that maps `AgentError` → HTTP:
```csharp
using Microsoft.AspNetCore.Mvc;
using Thalos;

namespace Daedalus.Api.Agents;

internal static class AgentErrorResults
{
    public static IActionResult ToActionResult(this AgentError error, ControllerBase controller)
    {
        var status = error.Code switch
        {
            AgentErrorCode.Validation => StatusCodes.Status400BadRequest,
            AgentErrorCode.Unauthorized => StatusCodes.Status403Forbidden,
            AgentErrorCode.AgentNotFound or AgentErrorCode.SessionNotFound or AgentErrorCode.ToolNotFound => StatusCodes.Status404NotFound,
            AgentErrorCode.SessionBusy or AgentErrorCode.SessionClosed => StatusCodes.Status409Conflict,
            AgentErrorCode.Quarantined or AgentErrorCode.ToolDenied => StatusCodes.Status422UnprocessableEntity,
            AgentErrorCode.Cancelled => 499,
            _ => StatusCodes.Status502BadGateway, // ProviderError, StoreError
        };
        return controller.Problem(title: error.Code.ToString(), detail: error.Detail is null ? error.Message : $"{error.Message} ({error.Detail})", statusCode: status,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code.ToString() });
    }
}
```

`Controllers/AgentsController.cs`
```csharp
using Daedalus.Agents.Api;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Thalos;

namespace Daedalus.Api.Controllers;

/// <summary>Lists available Thalos agents.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/agents")]
[Authorize(Policy = "AgentUse")]
[Produces("application/json")]
public sealed class AgentsController(IAgentCatalog catalog) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AgentSummaryDto>), StatusCodes.Status200OK)]
    public IActionResult GetAgents() => Ok(catalog.Agents.Select(AgentDtoMapper.ToDto).ToList());
}
```

`Controllers/AgentSessionsController.cs`
```csharp
using System.Text.Json;
using Daedalus.Agents.Api;
using Daedalus.Agents.Security;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Thalos;
using ZeroAlloc.Authorization;

namespace Daedalus.Api.Controllers;

/// <summary>Sessions and turns for Thalos agents. Streaming turns use Server-Sent Events.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/agents")]
[Authorize(Policy = "AgentUse")]
[Produces("application/json")]
public sealed partial class AgentSessionsController(IAgentRuntime runtime, IAgentSessionStore store, ILogger<AgentSessionsController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    private ISecurityContext Caller => new ClaimsSecurityContext(User);

    [HttpPost("{agentId}/sessions")]
    [ProducesResponseType(typeof(CreateAgentSessionResponseDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateSession(string agentId, CancellationToken ct)
    {
        if (!AgentId.TryParse(agentId, null, out var id))
        {
            return AgentError.Validation("agentId is not a valid id.").ToActionResult(this);
        }

        var result = await runtime.CreateSessionAsync(id, Caller, ct);
        if (result.IsFailure) return result.Error.ToActionResult(this);

        LogSessionCreated(logger, result.Value, id);
        return CreatedAtAction(nameof(GetSession), new { sessionId = result.Value.ToString() }, new CreateAgentSessionResponseDto(result.Value.ToString()));
    }

    [HttpGet("sessions")]
    [ProducesResponseType(typeof(IReadOnlyList<AgentSessionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSessions([FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);
        var result = await store.ListAsync(Caller.Id, Math.Max(0, skip), take, ct);
        return result.IsFailure ? result.Error.ToActionResult(this) : Ok(result.Value.Select(AgentDtoMapper.ToDto).ToList());
    }

    [HttpGet("sessions/{sessionId}")]
    [ProducesResponseType(typeof(AgentSessionDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSession(string sessionId, CancellationToken ct)
    {
        if (!SessionId.TryParse(sessionId, null, out var id)) return AgentError.Validation("sessionId is not a valid id.").ToActionResult(this);

        var session = await store.GetAsync(id, ct);
        if (session.IsFailure) return session.Error.ToActionResult(this);
        if (!IsOwnerOrAdmin(session.Value)) return AgentError.Unauthorized("Not your session.").ToActionResult(this);

        var messages = await store.LoadMessagesAsync(id, ct);
        if (messages.IsFailure) return messages.Error.ToActionResult(this);

        return Ok(new AgentSessionDetailDto(AgentDtoMapper.ToDto(session.Value), AgentDtoMapper.ToDtos(messages.Value)));
    }

    [HttpPost("sessions/{sessionId}/turns")]
    [EnableRateLimiting("llm-operations")]
    [ProducesResponseType(typeof(AgentTurnResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunTurn(string sessionId, [FromBody] SendTurnRequestDto request, CancellationToken ct)
    {
        if (!SessionId.TryParse(sessionId, null, out var id)) return AgentError.Validation("sessionId is not a valid id.").ToActionResult(this);

        var result = await runtime.RunTurnAsync(new AgentTurnRequest(id, request.Text, Caller), ct);
        return result.IsFailure ? result.Error.ToActionResult(this) : Ok(AgentDtoMapper.ToDto(result.Value));
    }

    /// <summary>Server-Sent Events: <c>event: &lt;kind&gt;</c> + <c>data: &lt;AgentEventDto json&gt;</c> per event; ends after <c>done</c> or <c>error</c>.</summary>
    [HttpPost("sessions/{sessionId}/turns/stream")]
    [EnableRateLimiting("llm-operations")]
    [Produces("text/event-stream")]
    public async Task RunTurnStream(string sessionId, [FromBody] SendTurnRequestDto request, CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        if (!SessionId.TryParse(sessionId, null, out var id))
        {
            await WriteEventAsync(new AgentEventDto("error", ErrorCode: "Validation", ErrorMessage: "sessionId is not a valid id."), ct);
            return;
        }

        await foreach (var evt in runtime.RunTurnStreamingAsync(new AgentTurnRequest(id, request.Text, Caller), ct))
        {
            await WriteEventAsync(AgentDtoMapper.ToDto(evt), ct);
        }
    }

    [HttpDelete("sessions/{sessionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CloseSession(string sessionId, CancellationToken ct)
    {
        if (!SessionId.TryParse(sessionId, null, out var id)) return AgentError.Validation("sessionId is not a valid id.").ToActionResult(this);
        var result = await runtime.CloseSessionAsync(id, Caller, ct);
        return result.IsFailure ? result.Error.ToActionResult(this) : NoContent();
    }

    private bool IsOwnerOrAdmin(AgentSessionRecord s) => string.Equals(s.OwnerId, Caller.Id, StringComparison.Ordinal) || Caller.Roles.Contains("admin");

    private async Task WriteEventAsync(AgentEventDto dto, CancellationToken ct)
    {
        await Response.WriteAsync($"event: {dto.Kind}\n", ct);
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(dto, SseJson)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Agent session {SessionId} created for agent {AgentId}")]
    private static partial void LogSessionCreated(ILogger logger, SessionId sessionId, AgentId agentId);
}
```

> Response compression is enabled globally in `Program.cs` (`UseResponseCompression`); Gzip buffers SSE. Exclude `text/event-stream` from `ResponseCompressionOptions.MimeTypes` (it isn't in the default list — verify the custom list in `Program.cs` doesn't add `text/*`), or add `[ResponseCompression(Enabled=false)]`-equivalent middleware ordering. Test in Task 13 asserts the first event arrives before the turn completes.

Commit `feat(api): agents + agent sessions controllers with SSE streaming and AgentError→ProblemDetails mapping`.

---

## Task 12: Api `Program.cs` wiring, appsettings, `.mcp.json`, JSON context, auth policy

**Files:**
- Modify: `src/Daedalus.Api/Program.cs`
- Modify: `src/Daedalus.Api/appsettings.json`, `appsettings.Development.json`
- Create: `src/Daedalus.Api/.mcp.json` (+ `CopyToOutputDirectory=PreserveNewest` in csproj)
- Modify: `src/Daedalus.Api/ApiJsonSerializerContext.cs`
- Modify: `src/Daedalus.AppHost/Program.cs` (nothing new needed: `ANTHROPIC_API_KEY` already passed to api)

`Program.cs` — after `AddAgentFrameworkServices` (Ralph) add:
```csharp
// Thalos-based agents (strangler: lives beside Ralph until phase 1.6)
builder.Services.AddDaedalusAgents(builder.Configuration, builder.Environment);
```
Auth policy: `options.AddPolicy("AgentUse", policy => policy.RequireAuthenticatedUser());`. Add all `Agents` DTOs (and `ProblemDetails` already there) to `ApiJsonSerializerContext` `[JsonSerializable]` list — including `List<AgentSummaryDto>`, `List<AgentSessionDto>`, `AgentSessionDetailDto`, `AgentTurnResultDto`, `AgentEventDto`, `CreateAgentSessionResponseDto`, `SendTurnRequestDto`.

`appsettings.json` additions:
```json
"Thalos": {
  "McpConfigPath": ".mcp.json",
  "Anthropic": { "DefaultModel": "claude-sonnet-4-5", "DefaultMaxOutputTokens": 8192 },
  "Sentinel": { "Enabled": true, "OnCritical": "Quarantine", "OnHigh": "Alert", "OnMedium": "Log", "OnLow": "Log" },
  "ToolPolicies": [
    { "Pattern": "roslyn__apply_*", "Policy": "developer" },
    { "Pattern": "roslyn__rename_*", "Policy": "developer" }
  ],
  "Agents": [
    {
      "Id": "01K2Q0000000000000000ARCH1",
      "Name": "Daedalus Architect",
      "Description": "Answers architecture questions about the Daedalus solution using Roslyn and Daedalus learnings.",
      "Instructions": "You are a senior .NET architect embedded in the Daedalus project. Use roslyn__* tools to inspect the solution and daedalus__* tools to recall past learnings and failure patterns. Cite symbols and files. If a tool fails, say so plainly.",
      "Tools": [ "roslyn__*", "daedalus__*", "context7__*" ]
    }
  ]
}
```
> Generate a real ULID for `Id` (`SessionId.New().ToString()` in a scratch, or any 26-char Crockford base32); the placeholder above is only shaped right. Keep it stable — sessions reference it.

`.mcp.json` (Api content root; adjust the solution path for the dev machine — CI/E2E don't need roslyn):
```json
{
  "mcpServers": {
    "roslyn": { "type": "stdio", "command": "dnx", "args": ["RoslynCodeLens.Mcp", "--yes", "--", "C:/Projects/Prive/daedalus/Daedalus.sln"], "env": { "ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS": "600" } },
    "context7": { "type": "http", "url": "https://context7.com/api" }
  }
}
```
Missing servers are non-fatal (`McpToolSource` returns `ProviderError`, catalog skips).

Run the API locally through Aspire, hit `GET /api/agents` with a dev token (Scalar UI at `/scalar`) → one agent. Commit `feat(api): wire Thalos agents, config, .mcp.json, JSON context, AgentUse policy`.

---

## Task 13: Controller integration tests

**Files:**
- Create: `tests/Daedalus.Tests.Integration/Controllers/AgentSessionsControllerIntegrationTests.cs`

Follow `ProjectsControllerIntegrationTests` style: real `PostgresAgentSessionStore` over the fixture DB, **fake `IAgentRuntime`** (`Thalos.NET.Testing` doesn't ship one — use NSubstitute), controller instantiated directly with a `ControllerContext` whose `HttpContext.User` carries `sub`/`role` claims.

Tests:
- `CreateSession_returns_201_with_location` (runtime returns Success(sessionId)).
- `GetSession_of_other_user_returns_403` (store row owned by "alice", caller "bob").
- `RunTurn_maps_SessionBusy_to_409` and `Quarantined_to_422_with_code_extension`.
- `RunTurnStream_writes_sse_events_incrementally`: runtime's `RunTurnStreamingAsync` yields `TextDelta`, then awaits a `TaskCompletionSource` before `TurnCompleted`; assert the response body already contains `event: text-delta` before releasing the TCS (use a `MemoryStream`-backed `DefaultHttpContext.Response.Body` and read its position). This is the test that catches response buffering.
- `ListSessions_returns_only_callers_sessions_newest_first`.

Commit `test(api): agent sessions controller integration tests incl. SSE incremental flush`.

---

## Task 14: Web — `AgentApiClient` with SSE reader

**Files:**
- Create: `src/Daedalus.Web/Services/AgentApiClient.cs`
- Modify: `src/Daedalus.Web/Program.cs` (register `AgentApiClient` with the same authorized `HttpClient` setup as `ApiClient`)
- Test: `tests/Daedalus.Tests.Unit/Web/SseReaderTests.cs` (add `Daedalus.Web` ref if not present — `Tests.Unit` already references Api/Console; add Web)

`AgentApiClient.cs`
```csharp
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Daedalus.Application.DTOs.Agents;

namespace Daedalus.Web.Services;

/// <summary>Client for /api/agents. Streaming uses fetch + a hand-rolled SSE parser (EventSource can't POST or send bearer tokens).</summary>
public sealed class AgentApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<AgentSummaryDto>?> GetAgentsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<IReadOnlyList<AgentSummaryDto>>("/api/agents", Json, ct);

    public async Task<string?> CreateSessionAsync(string agentId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/agents/{agentId}/sessions", content: null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateAgentSessionResponseDto>(Json, ct))?.SessionId;
    }

    public Task<IReadOnlyList<AgentSessionDto>?> ListSessionsAsync(int skip = 0, int take = 20, CancellationToken ct = default) =>
        http.GetFromJsonAsync<IReadOnlyList<AgentSessionDto>>($"/api/agents/sessions?skip={skip}&take={take}", Json, ct);

    public Task<AgentSessionDetailDto?> GetSessionAsync(string sessionId, CancellationToken ct = default) =>
        http.GetFromJsonAsync<AgentSessionDetailDto>($"/api/agents/sessions/{sessionId}", Json, ct);

    public async IAsyncEnumerable<AgentEventDto> StreamTurnAsync(string sessionId, string text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/sessions/{sessionId}/turns/stream") { Content = JsonContent.Create(new SendTurnRequestDto(text), options: Json) };
        request.SetBrowserResponseStreamingEnabled(true); // Blazor WASM: stream instead of buffering the whole body
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            yield return new AgentEventDto("error", ErrorCode: ((int)response.StatusCode).ToString(), ErrorMessage: await response.Content.ReadAsStringAsync(ct));
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var (_, data) in SseReader.ReadAsync(stream, ct))
        {
            var dto = JsonSerializer.Deserialize<AgentEventDto>(data, Json);
            if (dto is not null) yield return dto;
        }
    }
}

/// <summary>Minimal SSE parser: yields (event, data) per blank-line-terminated block. Handles multi-line data.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<(string? Event, string Data)> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream);
        string? evt = null;
        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return (evt, data.ToString()); }
                evt = null; data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue; // comment
            if (line.StartsWith("event:", StringComparison.Ordinal)) evt = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart()); }
        }
        if (data.Length > 0) yield return (evt, data.ToString());
    }
}
```
`SetBrowserResponseStreamingEnabled` is in `Microsoft.AspNetCore.Components.WebAssembly.Http` (`using Microsoft.AspNetCore.Components.WebAssembly.Http;`).

Test `SseReaderTests`: feed `"event: text-delta\ndata: {\"kind\":\"text-delta\"}\n\nevent: done\ndata: {}\n\n"` → two tuples with the right event names; multi-line data joins with `\n`.

Commit `feat(web): AgentApiClient with SSE streaming reader`.

---

## Task 15: Web — `Agent.razor` + nav

**Files:**
- Create: `src/Daedalus.Web/Pages/Agent.razor`
- Modify: `src/Daedalus.Web/Components/MainLayout.razor` (add `<RadzenPanelMenuItem Text="Agent" Icon="smart_toy" Path="agent"/>` after "Costs")

`Agent.razor` (follow the `Brainstorm.razor` layout: left column sessions, right column chat; keep it ~150 lines)
- `@page "/agent"` and `@page "/agent/{SessionId}"`; inject `AgentApiClient`, `NavigationManager`, `NotificationService`.
- On init: load agents (`GetAgentsAsync`), sessions (`ListSessionsAsync`); if `SessionId` route param, load detail.
- "New session" button → `CreateSessionAsync(selectedAgentId)` → navigate to `/agent/{id}`.
- Composer: `RadzenTextArea` + Send. On send: append user bubble, start `StreamTurnAsync`; for each `AgentEventDto`: `text-delta` appends to the live assistant bubble (`StateHasChanged` throttled), `tool-call`/`tool-result` add/update a collapsible `RadzenCard` ("⚙ roslyn__find_callers … ✓ 43 ms") under the bubble, `usage` shows a caption, `error` shows a red `RadzenAlert` (Quarantined gets a shield icon and the detail), `done` finalises.
- Disable the composer while streaming; `Escape`/Stop button cancels via `CancellationTokenSource`.
- `data-testid` attributes on: agent select (`agent-select`), new-session button (`agent-new-session`), composer (`agent-composer`), send (`agent-send`), message list (`agent-messages`), each assistant bubble (`agent-message-assistant`), tool card (`agent-tool-card`) — Playwright uses them.

Manual check via Aspire: create a session, ask "List the projects in this solution" → streamed answer with a `roslyn__…` tool card. Commit `feat(web): Agent chat page with streaming, tool cards and session list`.

---

## Task 16: ArchUnit rules

**Files:**
- Modify: `tests/Daedalus.Tests.Unit/Architecture/CleanArchitectureTests.cs`

Add (load `Daedalus.Agents` and the Thalos assemblies into the `ArchLoader`):
```csharp
[Fact] public void DomainLayer_ShouldNotDependOn_Thalos() => Types().That().Are(DomainTypes).Should().NotDependOnAnyTypesThat().ResideInNamespace("Thalos", true).Check(Architecture);
[Fact] public void ApplicationLayer_ShouldNotDependOn_Thalos() => Types().That().Are(ApplicationTypes).Should().NotDependOnAnyTypesThat().ResideInNamespace("Thalos", true).Check(Architecture);
[Fact] public void ApplicationLayer_ShouldNotDependOn_AgentsProject() => Types().That().Are(ApplicationTypes).Should().NotDependOnAnyTypesThat().ResideInAssembly(AgentsAssembly).Check(Architecture);
[Fact] public void AgentsProject_ShouldNotDependOn_ApiLayer() => Types().That().ResideInAssembly(AgentsAssembly).Should().NotDependOnAnyTypesThat().ResideInAssembly(ApiAssembly).Check(Architecture);
[Fact] public void RalphCode_ShouldNotDependOn_Thalos_YetStranglerBoundary() => Types().That().Are(InfrastructureTypes).Should().NotDependOnAnyTypesThat().ResideInNamespace("Thalos", true).Check(Architecture);
```
Run; all green. Commit `test(architecture): Thalos/Agents layering rules`.

---

## Task 17: Playwright browser test

**Files:**
- Modify: `tests/Daedalus.Tests.Playwright.Browser/Fixtures/E2EServerFixture.cs` — register a **stub `IAgentRuntime`** (scripted: any turn streams `text-delta "Hello from Thalos"`, a `tool-call`/`tool-result` pair for `roslyn__find_callers`, `usage`, `done`) and a stub `IAgentCatalog` with one agent, replacing the real registrations after `AddDaedalusAgents` (`services.Replace(...)`). Same pattern as `StubRalphLoopOrchestrator`.
- Create: `tests/Daedalus.Tests.Playwright.Browser/PageObjects/AgentPage.cs`
- Create: `tests/Daedalus.Tests.Playwright.Browser/Scenarios/AgentPageBrowserTests.cs`

Scenario: navigate `/agent` → select agent → New session → URL contains `/agent/` → type "hi" → Send → assistant bubble contains "Hello from Thalos" → one `agent-tool-card` visible with "roslyn__find_callers" → composer re-enabled. Screenshot to `docs/regression-screenshots/agent-page.png` in the passing state (the `regression-test` skill collects it).

Run: `dotnet test tests/Daedalus.Tests.Playwright.Browser --filter AgentPage` (needs the E2E env per README). Commit `test(e2e): Agent page browser scenario with stubbed runtime`.

---

## Task 18: Docs, regression, review, merge

1. `README.md`: add "Thalos agents" section (what it is, config keys, `.mcp.json`, roles `developer`/`admin` for mutating tools, endpoints table). Update the Project Structure tree with `Daedalus.Agents`.
2. `docs/architecture-diagrams.md`: add one Mermaid sequence diagram "Agent turn (Thalos)": Web → Api (SSE) → IAgentRuntime → ChatClientAgent → Sentinel → Anthropic → tools (MCP/local) → PostgresAgentSessionStore.
3. Run the `regression-test` skill against the running Aspire app (Agent page + one existing page for control) → report under `docs/`.
4. `dotnet format`; full `dotnet build` (0 warnings) and `dotnet test` (unit + integration; Playwright where the env allows).
5. `pre-push-review` on `feature/thalos-integration`; fix findings.
6. Merge to `main` (or open the PR — the user decides), close #227 with a summary comment linking both plans, the design, and the Thalos.NET repo/tag.
7. **Phase-end:** publish Thalos.NET `0.1.0` to nuget.org (tag `v0.1.0` in the Thalos repo, release workflow), switch Daedalus `Directory.Packages.props` to `0.1.0`, delete `packages-local/` and the local feed entry from `nuget.config`, commit `build: consume Thalos.NET 0.1.0 from nuget.org`.

## Definition of done for Plan B (= phase 1.1 done)

- Daedalus `main` builds with 0 warnings; unit + integration tests green; Playwright Agent scenario green; regression report PASS.
- A logged-in user can open `/agent`, start a session with "Daedalus Architect", ask a Roslyn-backed question and see a streamed, tool-annotated answer; prompt-injection input returns a visible quarantine error; `roslyn__apply_*` is denied for non-developers.
- Ralph functionality unchanged (existing 739 unit tests + integration suites still pass).
- Thalos.NET 0.1.0 on nuget.org; Daedalus consumes it; #227 closed; roadmap phase 1.1 marked complete.

