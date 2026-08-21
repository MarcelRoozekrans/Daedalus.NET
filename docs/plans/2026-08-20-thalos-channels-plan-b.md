# Phase 1.4 Channels — Plan B (Daedalus) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Daedalus consume Thalos.NET 0.4.0's channel packages — a PostgreSQL conversation map, durable outbound delivery via `ZeroAlloc.Outbox`, Telegram polling inside the API host, and a new interactive CLI host — and close the phase-1.1 identity gap that let a live 401 ship.

**Architecture:** `Thalos.NET.Channels` supplies the `ChannelPump`, command handling and console channel; `Thalos.NET.Channels.Telegram` supplies the transport. Daedalus provides the two things a library cannot: durable storage (`PostgresConversationMap`, the outbox tables) and host wiring (`AddDaedalusChannels`, the Telegram poller in the API host, `Daedalus.Cli`). Terminal messages go through the outbox; live deltas do not.

**Tech Stack:** .NET 10, EF Core + Npgsql, Thalos.NET 0.4.0, `ZeroAlloc.Outbox` 2.5.2 (+ `.EfCore`), xUnit + Testcontainers (PostgreSQL and Keycloak), AwesomeAssertions, NSubstitute.

**Spec:** `docs/plans/2026-08-20-thalos-channels-design.md` — read it alongside this plan.

**Plan A (already shipped):** `docs/plans/2026-08-20-thalos-channels-plan-a.md`. Thalos.NET 0.4.0 is on nuget.org; `Directory.Packages.props` is already bumped (commit `ee184d7`).

## Global Constraints

- **Target framework:** `net10.0`. Do not add or change TFMs.
- **Central package management** — `PackageReference` carries NO `Version`; add versions to `Directory.Packages.props`.
- **`#pragma warning disable` is not acceptable.** Plan A ran 20 tasks with zero suppressions; every analyzer complaint was resolved by following the analyzer. If you believe one is warranted, stop and report it.
- **0 warnings AND 0 errors** — warnings are errors here.
- Domain entities stay framework-free (`Daedalus.Domain` has no EF Core dependency); persistence lives in `Daedalus.Infrastructure`/`Daedalus.Agents`.
- Conventional commits. **Header ≤ 100 characters** — plan A's only CI failure was a 110-character header, so check with `git log --format='%s' | awk 'length > 100'` before pushing.
- Do NOT push, open PRs, tag or publish. Local commits only.

### Verified API surface — use exactly this

Confirmed against the shipped 0.4.0 packages. **`GetBySessionAsync` was removed in 0.4.0 — do not implement it.**

```csharp
// Thalos.Channels
public interface IConversationMap
{
    ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(string channelId, ConversationId conversationId, CancellationToken ct);
    ValueTask<UnitResult<AgentError>>                   BindAsync(ConversationBinding binding, CancellationToken ct);
    ValueTask<UnitResult<AgentError>>                   UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct);
}

public sealed record ConversationBinding(
    string ChannelId, ConversationId ConversationId, SessionId SessionId, AgentId AgentId, DateTimeOffset LastActivityAt);

// Registration (Thalos.Channels / Thalos.Channels.Telegram)
ThalosBuilder UseChannels(this ThalosBuilder, IConfiguration configuration);        // binds "Thalos:Channels"
ThalosBuilder UseConversationMap<TMap>(this ThalosBuilder) where TMap : class, IConversationMap;
ThalosBuilder AddConsoleChannel(this ThalosBuilder);
ThalosBuilder AddTelegramChannel(this ThalosBuilder, IConfiguration configuration); // binds "Thalos:Channels:Telegram"
```

**Contract:** an unknown conversation returns `Success(null)`, **not** a failure — unbound is the normal state of a first message. `UnbindAsync` on an absent binding succeeds. Bindings are scoped by channel. **Key on `ConversationId.Value`** (the normalised string), never on the struct: `default(ConversationId) != new ConversationId("")` because record-struct equality compares the private backing field.

**`AgentId` is a ULID, not a string.** There is no `new AgentId("daedalus")`. Agents are named by `AgentDefinition.Name`; the pump resolves names via `IAgentCatalog`. `ConfiguredSecurityContext(string id, IEnumerable<string> roles)` already exists in `Thalos.NET.Channels` — **do not write a second copy in Daedalus.**

### Test-quality bar

Plan A shipped four tests that could not fail, each caught late and expensively: an expected value equal to a type's default; a fence input containing no reserved characters; a fixture that never entered the branch it claimed to cover; and nine notice call sites nothing ever asserted. Apply the lens:

- Every expected value must differ from what a broken or no-op implementation produces.
- For any assertion on a `bool`, `0`, `null` or `""`, ask whether a totally broken implementation would also produce it.
- If you cannot cover something, **say so** rather than claiming coverage. Overstated verification claims cost more review cycles in plan A than genuine gaps did.

### A note on this plan's code blocks

Plan A's dominant failure mode was **the plan asserting APIs that did not exist** — `[TypedId(typeof(string))]`, `new AgentId("daedalus")`, `IndexOf(' ', StringComparison.Ordinal)`, a test that could never pass. Where this plan shows code, treat it as intent and **verify each type and member against the real assembly before relying on it**. Where it describes behaviour in prose, that is deliberate: the behaviour is the contract, the shape is yours to discover.

---

### Task 1: The real-Keycloak identity test

**Sequenced first deliberately.** This phase adds a second way to construct an `ISecurityContext`; if the identity path is going to expose something, it should surface on day one rather than at the smoke run. It also closes a gap that shipped as a live defect: against real Keycloak, `GET /api/agents` answered 200 while every `AgentSessionsController` endpoint answered 401, because both clients declared `defaultClientScopes: ["profile","email"]` and omitted Keycloak's built-in `basic` scope — where `sub` moved in KC 24+. With no `sub`, `ClaimsSecurityContext.Id` fell through to `AnonymousId`. It survived phases 1.1–1.3 because `AgentEndpointsSmokeTests` substitutes `HeaderTestAuthHandler`, so **no test ever exercised a real Keycloak claim shape**.

**Files:**
- Test: `tests/Daedalus.Tests.Integration/Authentication/RealKeycloakIdentityTests.cs`
- Read first: `tests/Daedalus.Tests.Integration/Fixtures/KeycloakFixture.cs`, `KeycloakCollection.cs`, `ApiWebApplicationFactory.cs`, `HeaderTestAuthHandler.cs`, and `src/Daedalus.Agents/Security/ClaimsSecurityContext.cs`.

**Interfaces:**
- Consumes: `KeycloakFixture` (`TokenEndpoint`, `Authority`, `Realm`, `BaseUrl`), `ClaimsSecurityContext`.
- Produces: a test proving a **real** Keycloak token yields a non-anonymous `ISecurityContext.Id`.

- [ ] **Step 1: Understand the existing fixtures before writing anything**

`KeycloakFixture` starts Testcontainers Keycloak and imports the repo-root `keycloak-realm.json`. Read how `AuthenticationFlowTests` / `JwtAuthenticationTests` obtain a token from it, and how `ApiWebApplicationFactory` swaps in `HeaderTestAuthHandler`. Your test must **not** use that handler — bypassing real JWT validation is precisely the gap being closed.

Write in your report: how a token is obtained, and how you configured the API host to validate against `KeycloakFixture.Authority` instead of the test handler.

- [ ] **Step 2: Write the failing test**

Assert, against a token minted by the real Keycloak container:

1. The token contains a **`sub` claim** — the specific thing whose absence caused the defect. Assert on the decoded token, so a regression in `keycloak-realm.json`'s `defaultClientScopes` fails here rather than as a mystery 401.
2. `ClaimsSecurityContext` built from that principal has `Id != AnonymousSecurityContext.AnonymousId` and equal to the token's `sub`.
3. An authenticated call to a session-scoped endpoint returns something other than 401.

Assertion 1 is the load-bearing one: the other two would also pass under a different auth mechanism, but only a real `sub` from a real realm proves the scope configuration.

- [ ] **Step 3: Run it and watch it fail for the right reason**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~RealKeycloakIdentityTests`
Expected initially: **PASS**, because `keycloak-realm.json` was already fixed in PR #239. That is fine — this test is a regression guard, not a bug reproduction. **Prove it has teeth**: temporarily remove `basic` from `defaultClientScopes` in `keycloak-realm.json`, confirm the test FAILS, restore it. Report what you saw. Without that mutation the test is unverified.

> Note: Aspire/Testcontainers reuse an existing Keycloak container, so a `keycloak-realm.json` change may appear to do nothing. Run `docker rm -f $(docker ps -aq --filter name=keycloak)` between mutation runs, or the mutation will silently test the old realm.

- [ ] **Step 4: Commit**

```bash
git add tests/Daedalus.Tests.Integration
git commit -m "test(auth): pin the real Keycloak claim shape that HeaderTestAuthHandler hides"
```

---

### Task 2: The `ChannelConversations` table

**Files:**
- Create: `src/Daedalus.Domain/Entities/ChannelConversation.cs`
- Modify: `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs`
- Create: a migration under `src/Daedalus.Infrastructure/Migrations/`
- Test: `tests/Daedalus.Tests.Unit.Domain/Entities/ChannelConversationTests.cs`

**Interfaces:**
- Produces: the `ChannelConversation` aggregate and its table. Task 3's store maps to it.

- [ ] **Step 1: Model the entity on `Skill`**

Read `src/Daedalus.Domain/Entities/Skill.cs` first — it is the closest precedent (an `Entity<TId>` with a natural key, `MaxXLength` constants, a static `Create` returning `CSharpFunctionalExtensions.Result`, framework-free). Mirror that shape.

The key is composite: `(ChannelId, ConversationId)`. `Entity<TId>` takes a single id, so decide how to represent it — a composite key configured in the DbContext, or a synthesised string id — and **state your choice and why in your report**. Whichever you pick, uniqueness on `(ChannelId, ConversationId)` must be enforced by the database, not merely by convention.

Fields: `ChannelId`, `ConversationId`, `SessionId` (Guid), `AgentId` (Guid), `LastActivityAt` (UTC), `CreatedAt` (UTC).

`SessionId` and `AgentId` are ULID-backed `[TypedId]`s in Thalos whose underlying value is a `Guid` — store the Guid, not the 26-character rendered form, and say in your report which you confirmed by reading the generated type.

- [ ] **Step 2: Write failing domain tests**

Cover: `Create` rejects a blank `ChannelId` and a blank `ConversationId`; a valid create round-trips every field; the timestamps are UTC. Use non-default values throughout — a test asserting `Guid.Empty` or `""` would pass against a broken constructor.

- [ ] **Step 3: Implement the entity, add the `DbSet`, generate the migration**

Add `public DbSet<ChannelConversation> ChannelConversations => Set<ChannelConversation>();` and configure the key/index. Then generate the migration with the repo's usual command — find it in `docs/` or the `skills/daedalus-migrations` procedure rather than guessing; there is a documented procedure for exactly this.

- [ ] **Step 4: Prove the migration rolls back and forward**

Mirror what `AddSkills` did: a test that migrates the chain **down past your migration and back up again** against a Testcontainers PostgreSQL. A migration that only ever runs forward is untested.

- [ ] **Step 5: Run and commit**

Run the domain suite and the migration test. Expected: green.

```bash
git commit -m "feat(channels): add the ChannelConversations table"
```

---

### Task 3: `PostgresConversationMap`

**Files:**
- Create: `src/Daedalus.Agents/Channels/PostgresConversationMap.cs`
- Test: `tests/Daedalus.Tests.Integration/Channels/PostgresConversationMapTests.cs`

**Interfaces:**
- Consumes: `IConversationMap` (signatures in Global Constraints), `ApplicationDbContext`.
- Produces: the store the pump uses in the API host.

- [ ] **Step 1: Read the precedent**

`src/Daedalus.Agents/Skills/PostgresSkillStore.cs` is the model: `IDbContextFactory<ApplicationDbContext>` + `TimeProvider`, registered as a **singleton**, fresh short-lived `DbContext` per call, `Result<T, AgentError>` on every boundary, Npgsql exceptions propagate.

- [ ] **Step 2: Write the failing tests**

Against Testcontainers PostgreSQL, cover the contract **exactly** as specified in Global Constraints:

1. An unknown conversation returns `Success(null)` — **not** a failure. (A store that returned an error here would make every first message look broken.)
2. `Bind` then `Get` round-trips all five `ConversationBinding` members.
3. `Bind` twice for the same conversation **replaces** rather than duplicating — assert the row count is 1 and the second binding won.
4. Bindings are **scoped by channel**: the same `ConversationId` under `"telegram"` and `"console"` are independent.
5. `Unbind` removes it, and `Unbind` again still succeeds (idempotent).
6. **The sharp edge**: a binding stored under `new ConversationId("")` is retrievable via `default(ConversationId)`. This fails if the implementation keys on the struct instead of `.Value`. Plan A shipped this test in the in-memory map for the same reason; the Postgres one needs it too.

- [ ] **Step 3: Implement**

Key on `ConversationId.Value`. Use the database's uniqueness constraint for the upsert rather than read-then-write, or state in your report why a read-modify-write is safe here — the API host is single-instance for Telegram, but the CLI can run concurrently against the same database.

- [ ] **Step 4: Run, then commit**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter FullyQualifiedName~PostgresConversationMapTests`

```bash
git commit -m "feat(channels): add PostgresConversationMap"
```

---

### Task 4: The outbox

**Files:**
- Modify: `Directory.Packages.props` (add `ZeroAlloc.Outbox` and `ZeroAlloc.Outbox.EfCore`, both `2.5.2`)
- Modify: `src/Daedalus.Agents/Daedalus.Agents.csproj`
- Create: `src/Daedalus.Agents/Channels/ChannelMessageQueued.cs`
- Create: a migration for the outbox table
- Test: `tests/Daedalus.Tests.Integration/Channels/ChannelOutboxTests.cs`

**Interfaces:**
- Produces: `ChannelMessageQueued(string ChannelId, string ConversationId, string Text, string? ReplaceMessageId)` marked `[OutboxMessage]`, and the EF Core store wiring.

- [ ] **Step 1: Read the library's own quick start first**

`ZeroAlloc.Outbox` is source-generated: `[OutboxMessage]` on a record emits `IOutboxWriter<T>` and a DI extension. Read the package README (or `C:\Projects\Prive\ZeroAlloc\ZeroAlloc.Outbox\README.md` if present locally) and confirm the generated extension's exact name before wiring it — **do not assume `AddChannelMessageQueuedOutbox()`**; the generator's naming is its own business.

- [ ] **Step 2: Add the message type and the store**

`AddOutbox(...)` with `.WithEfCore<ApplicationDbContext>()`. Generate the outbox table's migration. Configure polling interval, batch size and max attempts explicitly rather than relying on defaults, and record the values you chose.

- [ ] **Step 3: Write the failing test**

Prove the round-trip against Testcontainers PostgreSQL: write a message via the generated writer, run the worker (or invoke the dispatcher directly), assert it was delivered and the row moved out of pending. Also assert a **failed** dispatch retries and eventually dead-letters rather than being lost silently — that is the property the outbox exists for, and the one a happy-path-only test would leave unverified.

- [ ] **Step 4: Run and commit**

```bash
git commit -m "feat(channels): add durable outbound delivery via ZeroAlloc.Outbox"
```

---

### Task 5: The dispatcher

**Files:**
- Create: `src/Daedalus.Agents/Channels/ChannelMessageQueuedDispatcher.cs`
- Test: `tests/Daedalus.Tests.Unit.Application/Channels/ChannelMessageQueuedDispatcherTests.cs`

- [ ] **Step 1: Write the failing test**

The dispatcher implements `IOutboxDispatcher<ChannelMessageQueued>` and must **resolve the right `IChannelAdapter` by `ChannelId`**. Cover: a message for `"telegram"` goes to the Telegram adapter and not the console one; an unknown `ChannelId` is handled without throwing (a throw would fail the outbox message forever); and the `ConversationId` is passed through unchanged.

Register two substituted adapters with different `ChannelId`s so routing is attributable — a single-adapter test would pass even with the routing removed.

- [ ] **Step 2: Implement, run, commit**

Remember `IChannelAdapter.DeliverAsync` is keyed on **`ConversationId`** in 0.4.0.

```bash
git commit -m "feat(channels): route outbox messages to the matching channel adapter"
```

---

### Task 6: `AddDaedalusChannels`

**Files:**
- Create: `src/Daedalus.Agents/Channels/DaedalusChannelsServiceCollectionExtensions.cs`
- Test: `tests/Daedalus.Tests.Unit.Application/Channels/DaedalusChannelsRegistrationTests.cs`

- [ ] **Step 1: Read `DaedalusAgentsServiceCollectionExtensions` first**

It is the house pattern for this repo: options validated with fail-fast messages, `TryAdd*`, and — importantly — the precedent that **skills are registered in `AddDaedalusAgents` only**, with a test pinning that `AddDaedalusMemory` registers no skills. Follow that discipline: a test must pin what this method does and does not register.

- [ ] **Step 2: Write failing tests, then implement**

`AddDaedalusChannels` composes: `UseChannels(configuration)`, `UseConversationMap<PostgresConversationMap>()`, and — only when Telegram is configured — `AddTelegramChannel(configuration)`. Cover: the conversation map resolves to the Postgres implementation, not the in-memory default; calling it twice does not double-register the pump; and Telegram is absent when not configured.

- [ ] **Step 3: Commit**

```bash
git commit -m "feat(channels): wire channels into the Daedalus host"
```

---

### Task 7: Telegram in the API host

**Files:**
- Modify: `src/Daedalus.Api/Program.cs`
- Modify: `src/Daedalus.Api/appsettings.json` (structure only — **no token**)
- Modify: `src/Daedalus.AppHost/` (Aspire parameter for the bot token)

- [ ] **Step 1: Wire it**

Call `AddDaedalusChannels` beside the existing `AddDaedalusAgents` (`Program.cs:69`). The Telegram poller runs here because this host already owns the runtime, the database and Sentinel.

- [ ] **Step 2: Configuration and secrets**

`Thalos:Channels` and `Thalos:Channels:Telegram` sections. **The bot token must never appear in `appsettings.json`** — user-secrets locally, an Aspire parameter / environment variable in the container. Put the *shape* in `appsettings.json` with an empty token, and the value nowhere in the repo.

Set `AllowedUserIds` to a real single id in your local user-secrets. **An empty allow-list is a startup failure by design** — the library rejects it — so a misconfiguration fails loudly rather than opening the bot to anyone who finds it.

- [ ] **Step 3: Single-instance guard**

`getUpdates` refuses concurrent pollers. Ensure the API host cannot run two: the `Thalos:Channels:Telegram:Enabled` flag governs it, and if the host is ever scaled the flag must be off on all but one replica. **Document this in the host's configuration comments** — it is an operational trap, not a code one.

- [ ] **Step 4: Verify the host still starts**

Build and run the AppHost. Plan A's most instructive bug was a configuration path that every test passed and only a real host start could catch — the smoke run is the only gate that exercises a development host, and it is not ceremony.

```bash
git commit -m "feat(channels): run the Telegram channel in the API host"
```

---

### Task 8: `Daedalus.Cli`

**Files:**
- Create: `src/Daedalus.Cli/` (csproj, `Program.cs`)
- Modify: `Daedalus.sln`

- [ ] **Step 1: Create the host**

A plain console host that wires `AddDaedalusChannels` plus `AddConsoleChannel()`, and runs the pump. Model the service registration on `src/Daedalus.Console/Program.cs`, but note that host is the Ralph worker — do not inherit its Ralph-specific services.

**Do NOT register this project in AppHost.** It needs a TTY and is launched by hand; an AppHost-managed service would start it headless with no stdin.

- [ ] **Step 2: Know the two console limitations before you test by hand**

Both are documented in the library's README and are expected, not bugs to chase:
- Ctrl+C burns the host shutdown timeout — `Console.In`'s `ReadLineAsync(ct)` does not observe cancellation.
- On `SessionBusy` / `SessionNotFound` / `SessionClosed` the adapter never sees a terminal event, so the line is not closed; the next delta starts on a fresh line.

- [ ] **Step 3: Run it against a real agent**

`dotnet run --project src/Daedalus.Cli`, then exercise `/help`, `/agents`, `/new`, a real turn, `/status`, `/cancel` mid-turn, and `/end`. **`/cancel` and the busy notice are the two behaviours plan A found to be structurally broken and rebuilt** — verify them by hand, because no automated test in this repo covers the console end-to-end.

Record the transcript in your report.

```bash
git commit -m "feat(channels): add the Daedalus.Cli interactive host"
```

---

### Task 9: Architecture test

**Files:**
- Modify: the ArchUnit test project (find it — plan A's equivalent lives in `Thalos.NET.Tests.Architecture`; Daedalus's is wherever `ArchUnit` is already used)

- [ ] **Step 1: Pin the layering**

Mirror what phases 1.2 and 1.3 did when they added `Thalos.NET.Memory` and `.Skills`: assert the new packages load and that the dependency direction holds in **both** directions. Read the existing rules first and follow their idiom.

- [ ] **Step 2: Prove the rule is live**

An architecture rule that resolves to an empty type set passes while checking nothing. Assert the candidate set is non-empty, or mutate the rule and confirm it fails. Plan A's Task 19 found a rule whose name promised more than it checked; do not repeat it.

```bash
git commit -m "test(channels): pin the Daedalus channel layering"
```

---

### Task 10: Full verification and the smoke run

- [ ] **Step 1: Full suite**

```bash
dotnet build -warnaserror
dotnet test
```

Expected: 0 warnings, and every suite green — Domain 275+, Application 382+, Unit 123+, Infrastructure 130+, Integration 361+, browser 99 with `Skipped: 0`, plus your new tests. **Docker must be running** for Integration; it was down throughout plan A, so confirm it is up rather than assuming.

- [ ] **Step 2: The AppHost smoke run**

Start the AppHost and exercise the Telegram bot from a real phone: `/help`, `/new`, a real turn with a tool call, `/cancel`, `/status`, `/end`. Confirm the streamed message edits in place rather than posting one message per delta.

Watch specifically for the **operator notices** — `/help`, the busy notice, unknown command. Those were dropped entirely by a defect plan A found late; they are the behaviour most worth confirming with your own eyes.

- [ ] **Step 3: Environment gotchas**

- The local pgvector volume has a collation-version mismatch: `docker volume rm daedalus_postgres_data` or `REINDEX DATABASE daedalus;`.
- Aspire reuses an existing Keycloak container, so a `keycloak-realm.json` change needs `docker rm -f daedalus-realm-*` to take effect — otherwise a change appears applied while doing nothing.

- [ ] **Step 4: Pre-push review**

Run `pre-push-review` before the branch is offered. Phases 1.2 and 1.3 both did; `audit-milestone` checks for a PASS report.

---

### Task 11: Documentation

- [ ] **Step 1: Update the docs the phase touched**

Record: the two new configuration sections; that the bot token lives in user-secrets and never in the repo; the single-instance Telegram constraint; at-most-once delivery; and that **operators should redact or disable HTTP-client OpenTelemetry for `api.telegram.org`**, since the bot token sits in the URL path and would otherwise appear in `url.full`.

- [ ] **Step 2: Commit**

```bash
git commit -m "docs(channels): document the Daedalus channel configuration"
```

---

## Self-Review

**Spec coverage.** Design §4 (Daedalus layout) → Tasks 2, 3, 6, 7, 8. §5 (conversation map) → Tasks 2, 3. §9 (outbox) → Tasks 4, 5. §10 (testing) → Tasks 1, 3, 4, 5, 6, 9, 10. §8 (identity) → Tasks 1, 7. §11 (delivery) → Tasks 10, 11.

**Deliberately out of scope**, per the design's own "out of scope" list: webhooks, multi-user operation, account linking, inbound media, inline keyboards, proactive pushes (1.5 owns those and will write into the outbox laid down here), and an outbox dashboard.

**Known unknowns, flagged rather than guessed:**

- **The `ZeroAlloc.Outbox` generated extension name** (Task 4) — I have not verified it. The plan says to read the package rather than assume, because assuming an API is exactly how plan A lost four tasks to `new AgentId("daedalus")`.
- **The composite-key representation** for `ChannelConversation` (Task 2) — `Entity<TId>` takes a single id; the choice is left to the implementer with a requirement to justify it.
- **Daedalus's ArchUnit project location** (Task 9) — plan A's was in the Thalos repo; this plan says to find the local equivalent rather than naming a path I have not confirmed.

**Sequencing.** Task 1 is first by design, not convenience. Tasks 2→3 and 4→5 are hard dependencies. Task 6 needs 3 and 5. Tasks 7 and 8 both need 6. Task 10 needs everything.
