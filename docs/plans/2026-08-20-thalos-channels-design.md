# Phase 1.4 — Channels: Telegram (+ CLI) via `IChannelAdapter` + `ZeroAlloc.Outbox`

**Date:** 2026-08-20 · **Milestone:** 1 (Hermes-style agent framework) · **Issue:** #230 · **Depends on:** phase 1.1
(design `docs/plans/2026-08-16-thalos-agent-core-design.md`, Thalos.NET 0.3.0 on nuget.org)

## 1. Goal

Give Thalos agents a **second and third way in**. Phase 1.1 shipped one channel — HTTP + Blazor — and declared
`IChannelAdapter` as a seam without implementing it. This phase makes the seam real by landing two
implementations at once: a **Telegram bot** (the agent reachable from a phone) and an **interactive CLI**
(the agent reachable from a terminal, no browser). Outbound delivery of terminal messages is made durable
with `ZeroAlloc.Outbox`.

Two implementations in one phase is the point. A transport abstraction validated against exactly one
transport is a guess; the console adapter and the Telegram adapter differ in every dimension that matters
(edit-in-place vs append, 1000 ms vs 0 ms flush, rate-limited vs not), and the pump has to serve both.

### In scope

- `Thalos.NET.Channels` (net8.0 + net10.0): `ChannelPump` hosted service, `IConversationMap` port +
  in-memory implementation, command parsing, `DeltaCoalescer`, and a **console adapter + source** as the
  in-box reference channel. No third-party dependencies.
- `Thalos.NET.Channels.Telegram` (net8.0 + net10.0): thin Bot API client, long-poll source, Telegram renderer.
- `Thalos.NET.Abstractions`: additive `IChannelSource`, `InboundMessage`, `ConversationId` typed id.
- Daedalus: `PostgresConversationMap` + migration, `ZeroAlloc.Outbox.EfCore` + migration, outbox dispatcher,
  `AddDaedalusChannels`, Telegram poller in the API host, a new `Daedalus.Cli` host.
- The phase-1.1 **real-Keycloak identity test** that phases 1.1–1.3 never had (§10).
- Thalos.NET **0.4.0** released, Daedalus consuming it.

### Out of scope (decisions, not omissions)

Webhooks; multi-user or public bot operation; account linking; Discord/Slack/WhatsApp; inbound media,
files, voice or images; inline keyboards and callback queries; message reactions; per-channel agent
routing beyond `/new <agent name>`; unsolicited/proactive pushes (1.5 owns these, and the outbox laid down
here is what they will write into); an outbox dashboard.

## 2. Decisions taken during brainstorming

| # | Decision | Consequence |
|---|---|---|
| C1 | The Telegram channel serves **one operator**, not a user base. | One allow-listed Telegram user id, one configured principal. No registration, linking, tenancy or quota. |
| C2 | The CLI is a **real interactive channel**, not an HTTP client. | It implements the same seam, which is what proves the seam generalises. |
| C3 | **Long polling**, not webhooks. | Works behind NAT, in Docker, in Aspire, with no public ingress, cert or tunnel. Costs one long-lived poller and forces single-instance operation. |
| C4 | Chats map to sessions via **explicit commands + idle rollover**. | `/new`, `/end`, `/status`; a 12h idle timeout stops context growing without bound. |
| C5 | A running turn is shown as **one message, edited in place**, with a live activity line. | Feels live within Telegram's ~1 msg/sec/chat budget and leaves one clean message in history. |
| C6 | The outbox carries **terminal messages only**. | Deltas are ephemeral; persisting hundreds of rows per turn and replaying stale deltas after a crash would be actively wrong. |
| C7 | Telegram runs as a **configured principal with read-only roles**, distinct from the Keycloak `sub`. | A leaked bot token cannot mutate the repo and cannot read browser-started sessions. Reversible via one config line. |
| C8 | Approach **A**: two new packages, console adapter in the library, Telegram transport isolated. | Mirrors the `Thalos.NET.Memory` / `.Memory.RagNet` split; Thalos.NET 1.0 ships with a working reference channel. |
| C9 | ~~`IChannelSource` is **added**, `IChannelAdapter` is **not reshaped**.~~ **Reversed during Task 17:** `IChannelSource` is added *and* `IChannelAdapter.DeliverAsync` is **re-keyed from `SessionId` to `ConversationId`**. | **This is a breaking change to `Thalos.NET.Abstractions`, shipped in 0.4.0 with no deprecation path.** It was taken because the original decision was wrong, not merely inconvenient: an adapter addresses a *conversation* — a chat, a socket, a terminal — and most of what a channel must say (`/help`, an unknown command, the busy notice, "that session had already ended") belongs to a conversation with **no** session, or none by the time the notice is sent. A session-keyed seam could only carry those by inventing a `SessionId` bound to nothing, which the Telegram adapter then failed to resolve and dropped **silently** — every operator notice lost. The cost was judged acceptable because 0.3.0 shipped `IChannelAdapter` as a declared seam with **zero implementations anywhere**, so there was nothing running against the old signature to break; `PortsShapeTests` was updated to pin the new shape. `IConversationMap.GetBySessionAsync` — which existed only to serve the old key — was **removed** in the same change. |
| C10 | Inbound updates are **acked before processing** (at-most-once). | A crash mid-turn loses that message rather than re-running a turn that may have written memories or touched a repo. |

### Direction note

The 1.1 design pencilled in `ZeroAlloc.Rest` for channel API clients. **`ZeroAlloc.Rest` targets `net10.0`
only, while Thalos multi-targets `net8.0;net10.0`.** The Telegram client is therefore hand-rolled over
`HttpClient` with `System.Text.Json` source-generated contexts — four endpoints, AOT-safe, no dependency.
Revisit if Thalos ever drops net8.0.

## 3. Ports

`IChannelAdapter` (outbound, shipped in 0.3.0) and `IChannelSource` (inbound, new) are peers and both live
in `Thalos.NET.Abstractions/Ports/`.

```csharp
/// <summary>A source of inbound messages for one channel.</summary>
public interface IChannelSource
{
    string ChannelId { get; }
    IAsyncEnumerable<InboundMessage> ReadAsync(CancellationToken ct);
}

public sealed record InboundMessage(
    string ChannelId,
    ConversationId ConversationId,   // Telegram chat id / "console" — opaque to Thalos
    string Text,
    ISecurityContext Caller,         // the channel supplies it; Thalos never infers
    string? ExternalMessageId);
```

`ConversationId` joins `AgentId` / `SessionId` / `TurnId` / `ToolCallId` as a `[TypedId]`, string-backed
(Telegram chat ids are numeric, a tty is not).

## 4. Packages and layout

| Package | Contents |
|---|---|
| `Thalos.NET.Channels` | `ChannelPump`, `IConversationMap` + `InMemoryConversationMap`, command parsing, `DeltaCoalescer`, `ConsoleChannelSource` + `ConsoleChannelAdapter`, `AddThalosChannels()`. |
| `Thalos.NET.Channels.Telegram` | `TelegramBotClient` (4 endpoints), `TelegramChannelSource`, `TelegramChannelAdapter`, `MarkdownV2Escaper`, `AddTelegramChannel()`. |

**Daedalus:** `Daedalus.Agents` gains `Channels/PostgresConversationMap.cs`,
`Channels/ChannelMessageQueuedDispatcher.cs`, `Security/ConfiguredSecurityContext.cs` and
`AddDaedalusChannels`. `Daedalus.Migrations` gains `AddChannelConversations` and `AddChannelOutbox`.
A new `src/Daedalus.Cli` hosts the pump over the console source. The Telegram poller runs **inside the API
host**, which already owns the runtime, the database and Sentinel.

**Why a new project rather than reusing `Daedalus.Console`.** That host is an Aspire-managed background
worker running `RalphLoopWorker` on the pre-ZeroAlloc stack (`CSharpFunctionalExtensions`), and 1.6 deletes
it. An interactive CLI is the opposite shape: it needs a TTY and is launched by hand, so running it as an
AppHost-managed service would start it headless with no stdin. `Daedalus.Cli` is therefore a separate
project and is **not** registered in AppHost.

```text
Telegram getUpdates ─► TelegramChannelSource ─┐
                                              ├─► ChannelPump ─┬─ command → IConversationMap + Create/CloseSession
Console stdin ───────► ConsoleChannelSource ──┘                │
                                                               └─ text → IAgentRuntime.RunTurnStreamingAsync
                                                                              │ AgentEvent stream
                                                                        DeltaCoalescer
                                                          ┌───────────────────┴───────────────────┐
                                                  live edits (direct)                   terminal message (outbox)
                                                  IChannelAdapter.DeliverAsync    IOutboxWriter<ChannelMessageQueued>
                                                                                         └─► worker ─► IChannelAdapter
```

**Single-instance constraint.** Telegram's `getUpdates` refuses concurrent pollers; two API replicas would
fight over updates and drop messages. Enforced by an explicit `Channels:Telegram:Enabled` flag, never by
assuming a replica count.

## 5. Sessions and commands

```csharp
public interface IConversationMap
{
    ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(string channelId, ConversationId conversationId, CancellationToken ct);
    ValueTask<UnitResult<AgentError>> BindAsync(ConversationBinding binding, CancellationToken ct);
    ValueTask<UnitResult<AgentError>> UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct);
}

public sealed record ConversationBinding(
    string ChannelId, ConversationId ConversationId,
    SessionId SessionId, AgentId AgentId, DateTimeOffset LastActivityAt);
```

Daedalus implements it as `ChannelConversations`, PK `(ChannelId, ConversationId)`. The in-memory
implementation ships in the library, for tests and for the CLI.

Commands are parsed by the pump, so every channel gets identical semantics:

| Command | Behaviour |
|---|---|
| `/new [agent name]` | Closes the bound session if open, creates a fresh one, rebinds. No argument → configured default agent. |
| `/end` | `CloseSessionAsync`, unbind. |
| `/status` | Current agent, session id, turn count, age. |
| `/agents` | Lists available `AgentDefinition`s. |
| `/cancel` | Aborts the in-flight turn. Earns its place because turns here run past a minute. |
| `/help` | The above. |

**Four lifecycle edges, each resolved so the operator never meets a dead end:**

1. **Plain text, nothing bound** — implicit `/new` with the default agent, then run the turn. Without this,
   the first message after any restart is an error.
2. **Idle rollover** — if `now - LastActivityAt > IdleTimeout` (default 12h), close, create, rebind, and
   *say so*. Silent rollover makes the agent look amnesiac.
3. **Bound session gone or `Closed`** — e.g. `AgentSessionCrashRecovery` closed it after a restart. Rebind
   transparently and say so.
4. **Turn already running** (`SessionBusy`) — reply "still working — `/cancel` to stop it". Not queued: a
   visibly busy agent is more honest than a quietly growing queue.

## 6. The Telegram adapter

**Client.** Hand-rolled over `HttpClient`, four endpoints: `getUpdates`, `sendMessage`, `editMessageText`,
`sendChatAction`. Telegram's error envelope is parsed properly, including `429` → honour
`parameters.retry_after` rather than a blind backoff.

**Long poll.** `getUpdates(offset, timeout: 50, allowed_updates: ["message"])`. Telegram remembers the
confirmed offset, so nothing is persisted locally. Per C10 the offset advances **before** the turn runs;
the pump logs any message dropped by a crash so the loss is not invisible.

**Three constraints that would otherwise bite in production:**

- **~1 message/sec per chat**, and `editMessageText` counts against it. `DeltaCoalescer` flushes at most
  once per second (configurable) and suppresses no-op edits, which Telegram rejects with
  `400 message is not modified`.
- **4096-char limit.** Long answers split on paragraph, then line boundaries, never mid-code-fence.
- **MarkdownV2 escaping.** It requires escaping every one of ``_ * [ ] ( ) ~ ` > # + - = | { } . !`` and
  agent output is full of those; a single missed escape returns `400` and the message is lost. Escape
  properly, and on a `400` parse failure **resend the same content as plain text**. Losing formatting beats
  losing the answer.

**Rendering.** One message per turn, edited in place: a live activity line (`▸ roslyn__find_callers`,
replaced as tools run) above the accumulated text. The final edit drops the activity line, leaving just the
answer — tool calls visible while they matter, no debris afterwards.

## 7. The console adapter

`ConsoleChannelSource` reads stdin with `ConversationId = "console"`. `ConsoleChannelAdapter` writes deltas
straight through with a **zero flush interval**, tool notices dimmed via ANSI. Same pump, same commands,
same coalescer configured differently.

That difference — 1000 ms versus 0 ms, edit-in-place versus append — is exactly what proves the abstraction
is not Telegram-shaped.

## 8. Identity and privilege

### Configuration

`BotToken` comes from user-secrets locally and an Aspire parameter / environment variable in the container.
Never `appsettings.json`.

```text
Channels:Telegram:
  Enabled: true
  BotToken: <secret>
  AllowedUserIds: [ 123456789 ]
  PrincipalId: "telegram:marcel"
  Roles: [ ]                    # empty: see "role strings" below
  DefaultAgent: "daedalus"
  IdleTimeout: "12:00:00"
  FlushInterval: "00:00:01"
```

### Three gates, all in the source, before a message becomes an `InboundMessage`

1. **Private chats only** — `chat.type != "private"` is dropped. Otherwise anyone who adds the bot to a
   group gets a conversation with the agent.
2. **Allow-list** — `from.id` not in `AllowedUserIds` is **dropped silently, not answered**. Replying
   confirms to a prober that the bot is live and backed by something worth probing.
3. **Principal** — `ConfiguredSecurityContext` (sibling to `ClaimsSecurityContext`), `Id = PrincipalId`,
   roles from config. The channel adds no new enforcement path to get wrong.

**Role strings.** `DeveloperPolicy` passes when `ctx.Roles` contains `developer` **or** `admin`; roles are
otherwise plain strings that mean nothing unless some policy checks them. So the read-only posture is
achieved by the *absence* of those two values, and the default is an **empty** role set rather than an
invented `"user"` — a role nothing evaluates would read as if it granted something. Empty roles still give
full access to the principal's own sessions (ownership is by `caller.Id`, not by role) while failing the
`developer` gate on the mutating `roslyn__apply_*` / `rename_*` tools and shared-owner memory delete.

**Owner separation (C7).** Sessions are owned by `caller.Id`, so `telegram:marcel` and the Keycloak `sub`
are different owners: Telegram sessions do not appear in the web UI's session list and vice versa. This is
deliberate — a leaked bot token cannot read back browser-started sessions. Setting `PrincipalId` to the
Keycloak `sub` would give cross-channel continuity instead; it is a one-line config change with no schema
impact, and collapsing the split later is far cheaper than separating it after shared history accumulates.

**Sentinel is unchanged, and that is the point.** Turns enter through `IAgentRuntime`, so scanning,
quarantine and tool authorization apply identically to Telegram, the CLI and HTTP. No channel gets a bypass
and no channel-specific security code exists to drift.

## 9. Outbox, errors and edge cases

```csharp
[OutboxMessage]
public sealed record ChannelMessageQueued(
    string ChannelId, string ConversationId, string Text);
```

`ChannelMessageQueuedDispatcher` resolves the right `IChannelAdapter` by `ChannelId` and calls
`DeliverAsync`. `ZeroAlloc.Outbox.EfCore` over `ApplicationDbContext`, migration `AddChannelMessageOutbox`,
with polling, batch size and retry configured explicitly rather than left to library defaults.

> **Correction (2026-08-21, during plan B execution).** This section originally said terminal messages were
> "written on `TurnCompletedEvent` / `TurnFailedEvent`" and routed through the outbox. **That is not
> implementable, and it contradicts §5.**
>
> The shipped `ChannelPump` has no outbox seam — it delivers terminal events straight to
> `IChannelAdapter.DeliverAsync`. The only interception point is a decorating adapter, and intercepting a
> terminal event there breaks the streaming design: the Telegram adapter sends one message and edits it in
> place, using the terminal event to perform the final edit and clear its per-conversation state. Divert that
> event and the streamed message never finalises — its activity line dangles — while the outbox later posts
> the same answer as a **second** message. Every reply would appear twice.
>
> §5 ("one message, edited in place") is the behaviour that shipped and the one users see, so §9 yields.
> **In this phase the outbox has no producer.** Terminal messages are delivered directly by the pump, as
> plan A built it.
>
> The infrastructure is kept rather than removed, because it is exactly what §12 already earmarks for **1.5**:
> proactive and unsolicited pushes from scheduled and subagent runs, which have no live turn to stream into
> and therefore no conflict with the edit-in-place design. It is built, tested (round-trip plus a
> retry-then-dead-letter proof) and wired; 1.5 supplies the writer.
>
> Kept deliberately as a record of a design error caught in execution rather than silently rewritten.

### Errors and edge cases

The rule is that the operator is always told something.

| Failure | Behaviour |
|---|---|
| `SessionBusy` | "Still working — `/cancel` to stop it." |
| `Quarantined` (Sentinel) | Terminal message naming the detector; session returns to Idle. |
| `ToolDenied` | Turn continues; the denial shows in the activity line, as on the web. |
| `ProviderError` / `StoreError` | Terminal message with the `AgentError` code, delivered directly by the pump like any other terminal message — the outbox has no producer in this phase (see the §9 correction above), so there is no retry of the notice itself. |
| Telegram `400` parse failure | Resend as plain text (§6). |
| Other Telegram `4xx` | Surfaced to the operator via the pump's normal terminal-message path; the outbox's dead-lettering does not apply here, since nothing routes terminal messages through the outbox in this phase. |
| Telegram / network down | Long poll retries with capped backoff; the pump never crashes the host. |
| Crash mid-turn | Message lost by design (C10) and logged; the operator can see it in their own chat history and retype. |

## 10. Testing

**Thalos.NET repo** — `Thalos.NET.Tests.Channels`:

- `ChannelPump` across all four §5 lifecycle edges; command parsing including unknown and malformed commands.
- `DeltaCoalescer` flush cadence, no-op suppression, terminal flush.
- `MarkdownV2Escaper` against a corpus of adversarial agent output (nested code fences, unbalanced
  backticks, every reserved character).
- Message splitting at paragraph / line boundaries and around code fences.
- `TelegramBotClient` against a stubbed `HttpMessageHandler`: `429 retry_after`, `400 not modified`,
  `400` parse failure → plain-text fallback.
- Architecture test: `Thalos.NET.Channels` does not reference `Thalos.NET.Channels.Telegram`.

**Daedalus:**

- `PostgresConversationMap` on Testcontainers; migration roll-back past `AddChannelConversations` and
  forward again, as 1.3 did for `AddSkills`.
- Outbox dispatcher routes by `ChannelId` to the correct adapter.
- **Identity tests (§8), scheduled early in the plan.** One boots the API against **real Keycloak** and
  asserts `HttpSecurityContextFactory` yields a non-anonymous `Id` from a genuine token — the regression
  `HeaderTestAuthHandler` masked through phases 1.1 and 1.2, and that shipped as a live 401 on every
  session endpoint. The other asserts the Telegram principal resolves to the configured id and roles **and
  that a `developer`-gated tool is denied for it**. A privilege boundary is only real if a test says so.
- CLI end-to-end over the in-memory map.
- ArchUnit loads `Thalos.NET.Channels`; both directions proven, as for Memory and Skills.

**Smoke:** an AppHost run driving a real bot. This is the gate that caught the content-root defect in 1.3
and remains the only thing that exercises a development host.

## 11. Delivery

1. Plan A (`docs/plans/2026-08-20-thalos-channels-plan-a.md`) — Thalos.NET repo, released as **0.4.0**.
2. Plan B (`docs/plans/2026-08-20-thalos-channels-plan-b.md`) — Daedalus consuming 0.4.0.

The identity tests from §10 are sequenced **first in plan B**, ahead of the Telegram work: this phase adds
a second way to construct an `ISecurityContext`, and if that is going to expose something it should surface
on day one rather than at the smoke run.

## 12. Follow-ups recorded for later phases

- Webhook transport, if the bot ever needs to scale past one operator or latency becomes a complaint.
- Proactive/unsolicited pushes from scheduled and subagent runs (**1.5**), writing into the outbox laid
  down here.
- Inbound media, files and voice; inline keyboards for approval workflows — a natural fit for Sentinel's
  approval path, deliberately not started here.
- Discord and Slack adapters, which should cost only a transport if the pump is right.
- `/elevate` with an expiry and an audit trail, if read-only roles prove too restrictive in daily use.
- An outbox dashboard (`ZeroAlloc.Outbox.Dashboard.Blazor`) in the Daedalus web UI.
