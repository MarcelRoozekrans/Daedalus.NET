# Session State

**Last session:** 2026-08-21
**Current milestone:** 1 — Hermes-Style Agent Framework (3 of 7 phases complete)
**Current phase:** 1.4 — Channels: Telegram (+ CLI). **Half complete** — plan A merged, plan B not started.
**Branch state:** Thalos.NET `main` at `9bf872c` (PR #41 merged 2026-08-21). Daedalus `main` clean apart from the two untracked pre-pivot regression files you chose to leave.

## Last completed

### Plan A (Thalos.NET) — merged, 20 tasks

Two new packages, **937 tests green** (916 locally + the 21 Testcontainers tests that only ran in CI):

- **`Thalos.NET.Channels`** — `ChannelPump` (hosted service binding inbound messages to agent sessions), `IConversationMap` + in-memory implementation, slash-command dispatch, `DeltaCoalescer`, `ConfiguredSecurityContext`, and an in-box console channel.
- **`Thalos.NET.Channels.Telegram`** — hand-rolled Bot API client (4 endpoints), MarkdownV2 escaper, fence-aware splitter, long-poll source, edit-in-place adapter.

Design: `docs/plans/2026-08-20-thalos-channels-design.md`. Plan A: `docs/plans/2026-08-20-thalos-channels-plan-a.md`.

### BREAKING CHANGE shipped in that merge

`IChannelAdapter.DeliverAsync` re-keyed from `SessionId` to **`ConversationId`**. An adapter addresses a conversation; operator notices (`/help`, unknown command, busy, "that session had already ended") legitimately have **no** session. The old signature made every notice undeliverable — the Telegram adapter silently dropped all of them. `IConversationMap.GetBySessionAsync` was **removed** in the same change; it existed only to serve the old key.

## Blockers

**Thalos.NET 0.4.0 is NOT released.** `release-please.yml` is `workflow_dispatch`-only (deliberately gated, same as 0.1.0–0.3.0). No release PR exists. **The user is handling the release themselves** and will say when 0.4.0 is on nuget.org.

Plan B cannot be executed until then — `Daedalus.Agents` must `PackageReference` both new packages at 0.4.0, and nothing in plan B compiles without them. **Plan B has also not been written yet**, by the user's choice, so that it can be written against the shipped API.

## Interface of record for plan B

`PostgresConversationMap` must implement (namespace `Thalos.Channels`):

```csharp
ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(string channelId, ConversationId conversationId, CancellationToken ct);
ValueTask<UnitResult<AgentError>>                   BindAsync(ConversationBinding binding, CancellationToken ct);
ValueTask<UnitResult<AgentError>>                   UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct);
// NOTE: GetBySessionAsync was REMOVED. Do not implement it.

public sealed record ConversationBinding(string ChannelId, ConversationId ConversationId, SessionId SessionId, AgentId AgentId, DateTimeOffset LastActivityAt);
```

Contract: an unknown conversation is `Success(null)`, **not** a failure. `Unbind` is idempotent. Bindings are scoped by channel. **Key on `ConversationId.Value`** (the normalised string), never on the struct — `default(ConversationId) != new ConversationId("")` because record-struct equality compares the private backing field.

Registration surface Daedalus calls:

```csharp
ThalosBuilder.UseChannels(IConfiguration)            // binds "Thalos:Channels"
ThalosBuilder.UseConversationMap<TMap>()             // callable before OR after UseChannels
ThalosBuilder.AddConsoleChannel()
ThalosBuilder.AddTelegramChannel(IConfiguration)     // binds "Thalos:Channels:Telegram"
```

Other facts plan B needs: `AgentId` is a **ULID**, not a string — agents are named by `AgentDefinition.Name` and resolved by scanning `IAgentCatalog.Agents` (`OrdinalIgnoreCase`). `ConfiguredSecurityContext(string id, IEnumerable<string> roles)` already exists in `Thalos.NET.Channels` — **do not write a second copy in Daedalus**; roles compare **ordinally**, and an empty role set is the intended read-only posture.

## Plan B scope (from design §4, §9, §10)

`PostgresConversationMap` + `AddChannelConversations` migration; `ZeroAlloc.Outbox.EfCore` + `AddChannelOutbox` migration + `ChannelMessageQueued` dispatcher; `AddDaedalusChannels`; Telegram poller inside the API host (single-instance); a new `src/Daedalus.Cli` host (**not** registered in AppHost — it needs a TTY); and the phase-1.1 **real-Keycloak identity test** that phases 1.1–1.3 never had, sequenced first.

## Known issues carried from plan A

1. `DeltaCoalescer.Flush()` is public with **zero production callers** — deleting it is free before 1.0, breaking after.
2. Console adapter never sees a terminal event on `SessionBusy`/`SessionNotFound`/`SessionClosed`, so the line isn't closed (cosmetic).
3. Console Ctrl+C burns the host shutdown timeout — `Console.In`'s `ReadLineAsync(ct)` doesn't observe cancellation.
4. Inline single-backtick code spans aren't tracked (only triple fences) — they render with visible backticks. No `400` risk.
5. **Operators must redact HTTP-client OpenTelemetry for `api.telegram.org`** — the bot token sits in the URL path and would appear in `url.full`. Noted in the packed README; nothing in the package can prevent it.
6. Untested: typing-throttle expiry, true parallel concurrency, multi-chunk plain-text fallback, and the non-400 fallback (a proxy's HTML error page yields a `JsonException` that drops the render).

## Open decisions (user)

1. **Trigger release-please for 0.4.0** — user is doing this themselves.
2. Delete the merged branch `feature/thalos-channels` (still present locally and on origin).
3. Delete the SDD workspace `Thalos.NET/.superpowers/sdd/2026-08-20-thalos-channels-plan-a/` (ledger, 20 task reports, review packages). Git history is the record now.
4. Carried from 1.1: manual sample smoke of `samples/Thalos.Sample.Console` with a real `ANTHROPIC_API_KEY`.
5. Two untracked pre-pivot files (`docs/regression-report-2026-03-01-1800.md` + screenshots) — left alone by choice.

## Environment

**Docker was down on this machine for the whole plan-A run** — `Thalos.NET.Tests.Memory.RagNet` (21 Testcontainers tests) never ran locally; they passed in CI on the PR. Start Docker before trusting a local full-suite run. Also carried: the local pgvector volume has a collation-version mismatch (`docker volume rm daedalus_postgres_data` or `REINDEX DATABASE daedalus;`), and Aspire reuses an existing Keycloak container, so a `keycloak-realm.json` change needs `docker rm -f daedalus-realm-*` to take effect.

## Recommended next step

Wait for the user to publish **Thalos.NET 0.4.0** to nuget.org. Once it resolves:

1. Bump `Directory.Packages.props` to `0.4.0` for all nine existing `Thalos.NET.*` entries and add `Thalos.NET.Channels` + `Thalos.NET.Channels.Telegram`.
2. Write **plan B** via `superpowers:writing-plans` against the design and the interface of record above.
3. Execute it, sequencing the real-Keycloak identity test first.
4. Then `complete-phase 1.4`.

A note for whoever picks this up: across plan A's 20 tasks, **every finding was a plan defect, a test-quality gap, or a fix-induced regression — none was an implementation defect.** Several were caught only because a test *could not fail*. Worth carrying that lens into plan B: an assertion whose expected value equals a type's default, or a fixture that never enters the branch it claims to cover, will pass and prove nothing.
