# Thalos.NET Agent Core — Design (Milestone 1, Phase 1.1)

**Date:** 2026-08-16
**Status:** approved in brainstorming, awaiting implementation plan
**Milestone:** 1 — Hermes-Style Agent Framework
**Phase:** 1.1 — Thalos.NET core + AI.Sentinel + Daedalus HTTP/Blazor channel
**Inputs:** [codebase map](2026-08-16-codebase-map.md), `docs/planning/ROADMAP.md`

---

## 1. Goal

Deliver a working, secured, persistent conversational agent end-to-end, built **beside** the
Ralph Loop (strangler pattern), so later phases add memory (Rag.NET), more channels, skills and
subagents without reopening the core.

The agent framework is a **new standalone library, Thalos.NET**, published to nuget.org.
Daedalus is its first consumer. Named after Talos, the bronze guardian of Crete; spelled
`Thalos` because `Talos.*` is taken on nuget.org and `Thalos` is entirely free.

### In scope

- Thalos.NET repo + packages: `Thalos.NET.Abstractions`, `Thalos.NET`, `Thalos.NET.Mcp`,
  `Thalos.NET.Anthropic`, `Thalos.NET.Sentinel`
- Agent runtime on Microsoft Agent Framework 1.17 (`ChatClientAgent`, `AgentSession`,
  `ChatHistoryProvider`) with tool calling (MCP + in-process)
- AI.Sentinel 2.0.1 wrapped around the model boundary with tool-call authorization
- Sessions persisted in Postgres via a Daedalus adapter
- Daedalus REST endpoints (SSE streaming) + one Blazor chat page
- Phase-1 demo agent: "Daedalus Architect" over `Daedalus.sln` via **roslyn-codelens-mcp**
- Housekeeping prerequisites (NuGet audit errors, CPM, MAF upgrade)

### Out of scope (later phases, see §10)

Rag.NET memory, skills, Telegram/Discord/CLI channels, subagents/scheduling, Ralph retirement,
Daedalus-wide ZeroAlloc migration.

---

## 2. Decisions taken during brainstorming

| # | Decision | Rationale |
|---|---|---|
| D1 | Libraries are nuget.org packages by Marcel: **Rag.NET 0.1.0** (breaking changes coming to 1.0), **AI.Sentinel 2.0.1** | Rag.NET must sit behind a Thalos-owned port; Sentinel is settled and slots in as an `IChatClient` decorator |
| D2 | "Hermes-like" = all four traits: general loop + tools, persistent memory + skills, multi-channel, subagents | Multi-phase milestone; phase 1.1 = core + Sentinel + one channel |
| D3 | Ralph: **strangler** — new core alongside, retire later | Tests stay green, no functional regression mid-milestone |
| D4 | Phase-1 channel: **HTTP API + Blazor page** | Exercises auth, streaming, persistence; Playwright-testable per milestone DoD |
| D5 | Core built on **Microsoft Agent Framework** (approach A) rather than generalising Ralph middleware (B) or hand-rolling (C) | MAF already in tree; `FunctionInvokingChatClient` already used; Sentinel designed for this composition |
| D6 | **Separate repo + NuGet from day one**, named **Thalos.NET** | Same model as Rag.NET/AI.Sentinel; forces the boundary; free on nuget.org |
| D7 | Repo at `C:\Projects\Prive\Thalos.NET`; local folder feed during dev; publish 0.1.0 at phase end | Fast iteration while API is unsettled |
| D8 | Thalos.NET is **ZeroAlloc-native**; Daedalus only bridges at the adapter/API layer | Coherent with Rag.NET/AI.Sentinel DNA; Daedalus migration is its own phase |
| D9 | MCP servers (roslyn-codelens-mcp, memorylens-mcp, context7, local tools) are `IToolSource`s via `Thalos.NET.Mcp` | Existing `McpToolBuilder` already supports stdio/http/local — port, don't rewrite |

---

## 3. Architecture

### 3.1 Two repositories

```
Thalos.NET  (new)                          Daedalus  (this repo)
──────────────────────────────             ────────────────────────────────────────
Thalos.NET.Abstractions  ◄──────────────── src/Daedalus.Agents      (adapter, NEW)
Thalos.NET               ◄──────────────── │  PostgresAgentSessionStore : IAgentSessionStore
Thalos.NET.Mcp           ◄──────────────── │  DaedalusToolSource        : IToolSource
Thalos.NET.Anthropic     ◄──────────────── │  AddDaedalusAgents(config)
Thalos.NET.Sentinel      ◄──────────────── │  AgentSession / AgentMessage aggregates + migration
                                            src/Daedalus.Api           + AgentSessionsController (REST + SSE)
                                            src/Daedalus.Web           + Pages/Agent.razor
```

Thalos.NET has **zero knowledge** of Daedalus, EF Core, Postgres or Blazor.

### 3.2 Thalos.NET package cut

| Package | Depends on | Contents |
|---|---|---|
| `Thalos.NET.Abstractions` | `ZeroAlloc.Results`, `.ValueObjects`, `.Mediator` (contracts), `.Authorization`, `.AsyncEvents`, `Microsoft.Extensions.AI.Abstractions` | Ports `IAgentRuntime`, `IAgentSessionStore`, `IToolSource`, `IChannelAdapter`, `IChatClientDecorator`. Models `AgentDefinition`, `AgentTurnRequest`, `AgentTurnResult`, `AgentEvent` hierarchy, `AgentError`, `AgentSessionRecord`, `TurnUsage`, `SessionState`. Typed ids `AgentId`, `SessionId`, `TurnId`, `ToolCallId`. Notifications `SessionCreated/Closed`, `TurnStarted/Completed`, `ToolCallRequested/Completed`. |
| `Thalos.NET` | Abstractions, `Microsoft.Agents.AI` 1.17, `ZeroAlloc.StateMachine`, `.Telemetry`, `.Validation`, `.Inject` | `ThalosAgentRuntime`, `AgentFactory` (chat-client pipeline composer), `ToolCatalog`, `LocalToolSource`, `SessionStoreChatHistoryProvider`, `InMemorySessionStore`, `AgentSessionMachine`, `AddThalos(cfg)`. |
| `Thalos.NET.Mcp` | Thalos.NET, `ModelContextProtocol` | `McpServerDefinition`, `McpToolSource`, `AddMcpServersFromFile(".mcp.json")`. |
| `Thalos.NET.Anthropic` | Thalos.NET, `Anthropic` | `UseAnthropic(apiKey, model, maxTokens)`. |
| `Thalos.NET.Sentinel` | Thalos.NET, `AI.Sentinel` | `UseAISentinel(opts)` decorator; exception → `AgentError` mapping; policy bridge. |

Repo conventions mirror AI.Sentinel: `Thalos.NET.slnx`, `Directory.Build.props` (Sonar + Meziantou +
NetAnalyzers @ `latest-all`, `TreatWarningsAsErrors`), `global.json`, GitVersion + release-please,
renovate, `src/ tests/ samples/ docs/ benchmarks/`, MIT, CI packs on push and publishes on tag.

### 3.3 ZeroAlloc ecosystem mapping

**Phase 1.1 (in the core):**

| Package | Use |
|---|---|
| `ZeroAlloc.Results` | Error model: `Result<T, AgentError>` / `UnitResult<AgentError>` on every boundary. Rag.NET already returns these. |
| `ZeroAlloc.ValueObjects` | `[TypedId]` for `AgentId`, `SessionId`, `TurnId`, `ToolCallId` (parsing + JSON generated). |
| `ZeroAlloc.Mediator` | Lifecycle notifications; AI.Sentinel already publishes `ThreatDetected`/`InterventionApplied` on it — one bus. |
| `ZeroAlloc.Authorization` | `ISecurityContext` + `IAuthorizationPolicy` for tool-call authorization; same abstraction AI.Sentinel's `RequireToolPolicy` uses. |
| `ZeroAlloc.AsyncEvents` | Streaming turn events (`AsyncEventHandler<AgentEvent>`) for SSE channels. |
| `ZeroAlloc.Telemetry` | `[Instrument]` on `IAgentRuntime`, `IToolCatalog`, `IAgentSessionStore` → `thalos.turn`, `thalos.tool.invoke` spans + metrics beside `ai.sentinel`. |
| `ZeroAlloc.Validation` | `[Validate]` on `AgentDefinition`, `AgentTurnRequest`; `.AspNetCore` on Daedalus DTOs (422). |
| `ZeroAlloc.StateMachine` | `AgentSessionMachine` — session lifecycle. |
| `ZeroAlloc.Inject` | Attribute registration inside Thalos generating `AddThalos()`; default mode extends MS DI. |
| `ZeroAlloc.Mapping` | Daedalus API DTO mapping (adapter/API only). |

**Later phases:** `ZeroAlloc.Saga` (subagent orchestration), `.Scheduling` (autonomous runs),
`.Outbox` (reliable channel delivery), `.Rest` (channel API clients), `.ORM` (AOT session store for
CLI hosts), `.Templates` (repo conventions). Not applicable: `.Notify`.

### 3.4 Daedalus-side layering

- `Daedalus.Agents` → `Thalos.NET.*`, `Domain`, `Infrastructure` (for `ApplicationDbContext`).
- `Api` → `Daedalus.Agents`. `Web` unchanged in references (talks HTTP).
- ArchUnit additions: `Domain`/`Application` must not reference `Thalos.*` or `Daedalus.Agents`;
  `Daedalus.Agents` must not reference `Api`.
- Ralph code paths untouched.

### 3.5 Dev loop

Thalos.NET: `dotnet pack` → `C:\Projects\Prive\.nuget-local` (folder feed). Daedalus `nuget.config`
lists it; Daedalus enables **real CPM** (`ManagePackageVersionsCentrally=true`) and pins
`Thalos.NET.*` to `0.1.0-local.*` during dev, `0.1.0` once published.

---

## 4. Agent core (`Thalos.NET`)

```csharp
public sealed record AgentDefinition(
    AgentId Id, string Name, string Description, string Instructions, string Model,
    IReadOnlyList<string> ToolNames,   // glob-capable: "roslyn.*", "memorylens.snapshot"
    ChatOptions? Defaults);
```

`AgentFactory.Create(definition)` builds the `IChatClient` pipeline

```
provider client (Anthropic)
  → each IChatClientDecorator in registration order   (Sentinel lives here)
  → UseFunctionInvocation()
  → ChatClientAgent(instructions, tools from ToolCatalog, SessionStoreChatHistoryProvider)
```

Agents are cached per definition id.

```csharp
public interface IAgentRuntime
{
    ValueTask<Result<SessionId, AgentError>>       CreateSessionAsync(AgentId agent, ISecurityContext caller, CancellationToken ct);
    ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct);
    IAsyncEnumerable<AgentEvent>                   RunTurnStreamingAsync(AgentTurnRequest request, CancellationToken ct);
}

public sealed record AgentTurnRequest(SessionId Session, string Text, ISecurityContext Caller);
public sealed record AgentTurnResult(TurnId Turn, string Text, TurnUsage Usage, IReadOnlyList<ToolCallSummary> ToolCalls, TimeSpan Elapsed);
public readonly record struct TurnUsage(int InputTokens, int OutputTokens, string ModelId);
```

**`RunTurnAsync`:** load session → `AgentSessionMachine.TryFire(Start)` (else `SessionBusy`) →
publish `TurnStarted` → `agent.RunAsync(text, session)` (MAF runs the tool loop) → history provider
persists → `TryFire(Complete | Fail | AwaitApproval)` → publish `TurnCompleted` → result.
Streaming variant wraps `RunStreamingAsync` and yields `TextDelta`, `ToolCallStarted/Finished`,
`UsageReported`, `Completed`, `Error`.

**Session state machine (`ZeroAlloc.StateMachine`):**

```
Idle ──Start──► Running ──Complete──► Idle
                Running ──Fail──────► Failed
                Running ──AwaitApproval──► AwaitingApproval ──Approve──► Running
                                             AwaitingApproval ──Deny────► Idle
                any ──Close──► Closed (terminal)
```

Security context flows in with every request; Thalos never invents identity.

---

## 5. Tools & MCP

`IToolSource { string Name; ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(ct); }`

`ToolCatalog` aggregates all sources, prefixes tool names by source (`roslyn.find_callers`),
applies the agent's glob allow-list.

`Thalos.NET.Mcp` ports Daedalus `McpToolBuilder`:
- `McpServerDefinition` = today's `McpServerConfig` (`Type: stdio|http|sse`, `Command/Args/Env/Cwd`,
  `Url/Headers`, `Timeout`, `Tools`) — same JSON as Claude Code `.mcp.json`, loadable directly.
- `McpToolSource` per server: lazy connect, cached client + tools, owns stdio process lifetime,
  reconnect on failure, singleton, disposed on shutdown.
- In-process tools (`[McpServerToolType]` scan + DI-scope-per-call `ScopedLocalTool`) live in
  `Thalos.NET` `LocalToolSource` (transport-independent).

**Servers in phase 1.1:**

| Server | Transport | Use |
|---|---|---|
| roslyn-codelens-mcp (`RoslynCodeLens.Mcp`) | stdio (`dnx RoslynCodeLens.Mcp -- Daedalus.sln`) or `--http` | Demo agent "Daedalus Architect". Mutating tools (`apply_code_action`, `rename_symbol`) behind policy `developer`. |
| memorylens-mcp (`MemoryLens.Mcp`) | stdio | Registered; proves multi-server config; no phase-1 scenario. |
| context7 | http | Docs lookup (already configured). |
| Daedalus learnings / failure-patterns | local | Moved to `DaedalusToolSource`. |

**Tool authorization:** `ZeroAlloc.Authorization` policies (`[Policy("developer")]`) evaluated
against the caller's `ISecurityContext` (from JWT). AI.Sentinel `UseToolCallAuthorization()`
enforces at chat-client level using the same policies. Thalos publishes `ToolCallRequested`; the
`AwaitingApproval` state exists in phase 1.1 but there is no approval UI yet — a denied tool returns
an error result to the model and the turn continues.

Future (named, not built): `Thalos.NET.Mcp.Server` exposing agents as MCP tools.

---

## 6. Sessions & persistence

```csharp
public interface IAgentSessionStore
{
    ValueTask<Result<AgentSessionRecord, AgentError>>            CreateAsync(AgentId agent, ISecurityContext owner, CancellationToken ct);
    ValueTask<Result<AgentSessionRecord, AgentError>>            GetAsync(SessionId id, CancellationToken ct);
    ValueTask<Result<IReadOnlyList<ChatMessage>, AgentError>>    LoadMessagesAsync(SessionId id, CancellationToken ct);
    ValueTask<UnitResult<AgentError>>                            AppendMessagesAsync(SessionId id, IReadOnlyList<ChatMessage> messages, TurnUsage usage, CancellationToken ct);
    ValueTask<UnitResult<AgentError>>                            UpdateStateAsync(SessionId id, SessionState state, CancellationToken ct);
    ValueTask<Result<IReadOnlyList<AgentSessionRecord>, AgentError>> ListAsync(string ownerId, int skip, int take, CancellationToken ct);
}

public sealed record AgentSessionRecord(SessionId Id, AgentId Agent, string OwnerId, SessionState State,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, int TurnCount, long TotalInputTokens, long TotalOutputTokens);
```

`SessionStoreChatHistoryProvider : ChatHistoryProvider` — `ProvideChatHistoryAsync` →
`LoadMessagesAsync`; `StoreChatHistoryAsync` → `AppendMessagesAsync` (request + response, history
filtered out). `SessionId` kept in `ProviderSessionState`. Thalos ships `InMemorySessionStore`.

**Daedalus:** Domain aggregates `AgentSession` (record fields + `RowVersion`) and `AgentMessage`
(`SessionId`, `Sequence`, `Role`, `ContentJson` serialized with `AIJsonUtilities.DefaultOptions`,
`InputTokens`, `OutputTokens`, `ModelId`, `CreatedAt`). `PostgresAgentSessionStore` over
`ApplicationDbContext`; migration `AddAgentSessions`. Ownership filter: `OwnerId == caller.Id`
unless `admin`. Token columns can feed `Costs` later (not phase 1.1).

---

## 7. Channel: HTTP API + Blazor

**`AgentSessionsController`** — `api/v1/agents/…`, `[Authorize]`, `llm-operations` rate limiter on turns.

| Verb | Route | Body → Result |
|---|---|---|
| GET | `/agents` | available `AgentDefinition`s |
| POST | `/agents/{agentId}/sessions` | → `201 { sessionId }` |
| GET | `/sessions?skip&take` | caller's sessions |
| GET | `/sessions/{id}` | record + messages |
| POST | `/sessions/{id}/turns` | `{ text }` → `AgentTurnResult` |
| POST | `/sessions/{id}/turns/stream` | `{ text }` → SSE `text-delta` / `tool-call` / `tool-result` / `usage` / `done` / `error` |
| DELETE | `/sessions/{id}` | close |

`HttpSecurityContextFactory` builds `ISecurityContext` from `HttpContext.User` (sub, realm roles).
DTOs validated by `ZeroAlloc.Validation.AspNetCore`, mapped by `ZeroAlloc.Mapping`.
`IChannelAdapter` (`ChannelId`, `DeliverAsync(SessionId, AgentEvent)`) is the seam for Telegram
etc.; the HTTP channel doesn't need it in phase 1.1.

**`Pages/Agent.razor`:** agent picker → session list → chat pane (reuses `Brainstorm.razor` bubble
pattern); `ApiClient.StreamTurnAsync` → `IAsyncEnumerable<AgentEventDto>` from the SSE endpoint;
tool calls as collapsible cards; quarantine as red system message; nav item "Agent".

---

## 8. Errors & Sentinel actions

- `AgentError { Code, Message, Detail? }` codes: `SessionNotFound`, `SessionBusy`, `Unauthorized`,
  `ToolDenied`, `Quarantined`, `ProviderError`, `Cancelled`, `Validation`.
- `Thalos.NET.Sentinel` mapping: `SentinelException` (Quarantine) → `Quarantined`, session → `Idle`,
  turn discarded (Sentinel already wrote the audit entry); `ToolCallAuthorizationException` → tool
  returns `"denied: <policy>"`, turn continues; `Alert`/`Log` pass through and Thalos re-publishes
  `ThreatDetectedNotification` for channels.
- Config: `Thalos:Sentinel:{OnCritical,OnHigh,OnMedium,OnLow,ToolPolicies[],DisabledDetectors[]}`;
  semantic detectors receive the existing Ollama `IEmbeddingGenerator` when present.
- API: `SessionBusy` → 409, `Quarantined` → 422 (+code), `Unauthorized` → 403, via existing
  ProblemDetails conventions. Streaming errors emit terminal `error` event then close.

---

## 9. Testing

**Thalos.NET repo**
- `Thalos.NET.Tests.Unit` (xUnit, NSubstitute, AwesomeAssertions): `AgentSessionMachine` (all
  state × trigger), `ToolCatalog` globbing/prefixing, `AgentFactory` decorator ordering (Sentinel
  before `UseFunctionInvocation`), `SessionStoreChatHistoryProvider`, `ThalosAgentRuntime` against
  a scripted fake `IChatClient` (responses + tool calls, no network), notification order.
- `Thalos.NET.Tests.Architecture` (ArchUnitNET): Abstractions has no MAF/provider refs; core never
  references `Anthropic`/`AI.Sentinel`; reflection only in `LocalToolSource`.
- `Thalos.NET.Tests.Sentinel`: real AI.Sentinel + fake inner client — injection → `Quarantined`;
  denied policy → tool error, turn continues.
- `Thalos.NET.Tests.Mcp`: in-repo stdio MCP test server — connect/list/cache/reconnect.
- `samples/Thalos.Sample.Console`: REPL with Anthropic + roslyn-codelens (manual smoke, doc example).
- CI: build → tests → pack → publish on tag.

**Daedalus repo**
- Unit.Domain: `AgentSession`/`AgentMessage` invariants.
- Integration (Testcontainers): `PostgresAgentSessionStore`; `AgentSessionsController` via
  `WebApplicationFactory` + fake `IAgentRuntime` (auth, 409/422, SSE framing).
- Unit/Architecture: new ArchUnit rules.
- Playwright.Browser: `Agent.razor` create → send → streamed reply + tool card (milestone
  "Regression test PASS").
- TDD throughout.

---

## 10. Housekeeping (inside phase 1.1, before framework work)

1. Clear the **127 NuGet-audit build errors** (`MessagePack` 2.5.192, `OpenTelemetry.*` 1.15.0,
   `Microsoft.OpenApi` 2.0.0, `SSH.NET` 2025.1.0 transitives) — CI green first.
2. Enable real CPM in Daedalus; move inline versions to `Directory.Packages.props`; add local feed.
3. Upgrade `Microsoft.Agents.AI` rc1 → 1.17.0 in Daedalus (Ralph only uses `AsIChatClient`).
4. Workstation only: global git `core.excludesFile=.gitignore` breaks SourceLink's
   `GetUntrackedFiles`; make absolute or remove.

---

## 11. Milestone roadmap implied

| Phase | Name | Depends on |
|---|---|---|
| **1.1** | **Thalos.NET core + Sentinel + Daedalus HTTP/Blazor channel** *(this design)* | — |
| 1.2 | Memory: `Thalos.NET.Memory` port + `Thalos.NET.Memory.RagNet` adapter (pgvector); replaces hand-rolled learnings slice | 1.1 |
| 1.3 | Skills: reusable procedures the agent loads/refines (Rag-backed) | 1.2 |
| 1.4 | Channels: Telegram (+ CLI) via `IChannelAdapter` + `ZeroAlloc.Outbox` | 1.1 |
| 1.5 | Subagents & scheduling: `ZeroAlloc.Saga`, `ZeroAlloc.Scheduling` | 1.1 |
| 1.6 | Ralph retirement + Daedalus ZeroAlloc migration (CSFE→Results, FluentValidation→Validation, CQRS→Mediator) | 1.2–1.5 |
| 1.7 | Thalos.NET 1.0, docs, architecture-diagrams rewrite | all |

Phase 1.1 will be planned as two documents: **Plan A — Thalos.NET repo**, **Plan B — Daedalus
integration**; A's local-feed pack is the handoff.

---

## 12. Open items carried into planning

- Exact `ZeroAlloc.*` package versions and any inter-package version constraints (Mediator ↔
  Authorization ↔ AI.Sentinel share `ISecurityContext`) — verify at plan time.
- MAF 1.17 `ChatHistoryProvider` exact virtual method names (`ProvideChatHistoryAsync` /
  `StoreChatHistoryAsync` per docs) — confirm against the package.
- Whether Sentinel's semantic detectors should be on by default in Daedalus dev (needs Ollama).
- roslyn-codelens launch mode for the demo (stdio via `dnx` vs `--http` sidecar in Aspire).
