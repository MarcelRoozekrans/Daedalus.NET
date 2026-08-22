# Session State

**Last session:** 2026-08-22
**Current milestone:** 1 — Hermes-Style Agent Framework (**4 of 7 phases complete**)
**Current phase:** 1.4 — Channels: **complete**. Next is **1.5 — Subagents & scheduling** (#231).
**Branch state:** Daedalus `main` at `11ca18d` (PR #241 merged). Thalos.NET `main` at the 0.4.0 release; `v0.4.0` tagged, GitHub release published, all eleven packages on nuget.org.

## Last completed — phase 1.4, both halves

### Plan A (Thalos.NET) — merged as #41, released as 0.4.0

Two new packages, **937 tests**: `Thalos.NET.Channels` (`ChannelPump`, `IConversationMap`, slash-command dispatch, `DeltaCoalescer`, `ConfiguredSecurityContext`, in-box console channel) and `Thalos.NET.Channels.Telegram` (Bot API client, MarkdownV2 escaper, fence-aware splitter, long-poll source, edit-in-place adapter).

**Breaking change shipped:** `IChannelAdapter.DeliverAsync` re-keyed from `SessionId` to **`ConversationId`**. An adapter addresses a conversation; operator notices legitimately have no session, and the old key made every one of them undeliverable. `IConversationMap.GetBySessionAsync` was removed in the same change — **do not reintroduce it.**

### Plan B (Daedalus) — merged as #241

`PostgresConversationMap` + `ChannelConversations` table; `ZeroAlloc.Outbox` 2.5.2 with its message type, EF Core store and dispatcher; `AddDaedalusChannels`; Telegram polling in the API host; a new `src/Daedalus.Cli` interactive host; three architecture rules; and the real-Keycloak identity test.

## The identity gap is closed

`RealKeycloakIdentityTests` mints a real ROPC token from Testcontainers Keycloak and drives it through the untouched production `AddJwtBearer` pipeline, asserting a non-empty `sub`. Mutation-verified by removing `basic` from `defaultClientScopes`.

This closes the defect that returned 401 on every `AgentSessionsController` endpoint while `GET /api/agents` returned 200 — it survived phases 1.1–1.3 because every smoke test substituted `HeaderTestAuthHandler`, which fabricates `sub` directly.

## Carried into 1.5 — read before starting

1. **The outbox has no producer, and 1.5 is meant to supply it.** It is built, tested (round-trip plus retry-then-dead-letter) and wired, but nothing writes to it. Design §9 was corrected in place: routing terminal messages through it would break the edit-in-place streaming and post every reply twice. **1.5's proactive/unsolicited pushes are the intended writer** — they have no live turn to stream into, so no conflict.
2. **Upstream bug in Thalos 0.4.0, unfixed:** `ChannelPump.CreateAndBindAsync` discards `BindAsync`'s `Result` without checking `IsFailure`. If the conversation map throws, the operator gets **no reply at all** while at-most-once has already discarded the message — violating the design's own "the operator is always told something". Worth a 0.4.1.
3. **`AddDaedalusChannels` does NOT wire the outbox** — `AddDaedalusAgents` does, and the library's `AddOutbox()` uses a plain `AddHostedService`, so calling both would run two pollers. A host must call both. Pinned by `ApiHostChannelWiringTests`, which boots the real host.
4. **`DefaultAgent` is an agent NAME, not an id.** `AgentId` is ULID-backed with no string constructor. Pinned by `DefaultAgentConfigurationTests` against each host's real `appsettings.json`, but there is still no startup validation.

## Blockers / known issues

- **`Daedalus.Tests.Playwright.Api` fails 126/126.** `E2EServerFixture.GlobalSetupAsync()` touches `_factory.Services` — starting the host, including Thalos's `SkillSyncService` querying the `Skills` table — before its own `EnsureCreatedAsync()` creates the schema. **Pre-existing since phase 1.3**, invisible because `ci.yml` excludes `~Playwright` from both test steps. Untouched by 1.4. The pre-push review FAILs on it: `docs/pre-push-review-2026-08-21-2230.md`. **Worth its own fix.**
- **The Telegram path has never been exercised end to end** — no bot token, no phone. Every Telegram claim rests on library documentation and decompilation. The **console** path was driven by hand: `/cancel` demonstrably tore down a live Anthropic SSE stream mid-tool-call, and the busy notice arrived during a running turn — the two behaviours plan A found structurally broken.
- **9 integration tests fail on this machine** — all `AuthenticationFlowTests`, unable to reach a running API on `localhost:8080` because a `traefik` container holds the port. Environmental. Note the repo has a `RequiresApiFactAttribute` that would SKIP rather than fail; those tests do not use it.
- **Operators must redact or disable HTTP-client OpenTelemetry for `api.telegram.org`** — the bot token sits in the URL path and would appear in `url.full`. No such instrumentation is registered today.

## Open decisions (user)

1. Fix the `Playwright.Api` fixture, or file it and leave it.
2. Configure a bot token so the Telegram path can be verified end to end.
3. Delete the merged branches `feature/thalos-channels` (Thalos.NET) and `feature/daedalus-channels` (Daedalus), local and remote.
4. Delete the two SDD workspaces: `Thalos.NET/.superpowers/sdd/2026-08-20-thalos-channels-plan-a/` and `daedalus/.superpowers/sdd/2026-08-20-thalos-channels-plan-b/`. Git history is the record now.
5. Carried from 1.1: manual sample smoke of `samples/Thalos.Sample.Console` with a real `ANTHROPIC_API_KEY`.
6. Two untracked pre-pivot files (`docs/regression-report-2026-03-01-1800.md` + screenshots) — left alone by choice.

## Environment

Docker is UP. The local pgvector volume has a collation-version mismatch (`docker volume rm daedalus_postgres_data` or `REINDEX DATABASE daedalus;`). Aspire reuses an existing Keycloak container, so a `keycloak-realm.json` change needs `docker rm -f daedalus-realm-*`. Orphaned `dcp.exe`/dashboard processes from a killed AppHost run hold ports and make the next start appear to hang.

## Recommended next step

Run `start-next-phase`. Phase 1.5 (**Subagents & scheduling: `ZeroAlloc.Saga` orchestration, `ZeroAlloc.Scheduling` autonomous runs**, #231) is `pending` with no design spec, so it routes to `superpowers:brainstorming` first.

**A process lesson worth carrying.** Across 31 tasks in this phase, findings were overwhelmingly plan defects and test-quality gaps rather than implementation defects. Several were caught only because a reviewer **ran** something rather than reading it — one test was failing 4 runs in 7 while reported green. Four pieces of dead API were removed before shipping. And my baseline for plan B covered only the suites I expected to touch, which left a 126-test Playwright failure outside my field of view for the whole plan: **baseline every test project, not just the ones the work should touch.**
