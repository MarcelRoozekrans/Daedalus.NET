# Phase 1.5 Plan A — Thalos.NET 0.5.0: `ISubagentRunner`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a detached-run primitive to Thalos.NET — run one agent turn with no live caller, under token, deadline and depth guards — and fix two parked 0.4.x defects, releasing as **Thalos.NET 0.5.0**.

**Architecture:** A new `Thalos.NET.Subagents` namespace inside the existing `Thalos.NET` package. `SubagentRunner` composes the three `IAgentRuntime` calls a detached run needs — `CreateSessionAsync` → `RunTurnAsync` → `CloseSessionAsync` — applying guards before the first model call. No new package, no dependency on `ZeroAlloc.Saga` or `ZeroAlloc.Scheduling`: orchestration is host policy and lives in Daedalus.

**Tech Stack:** .NET 8 + .NET 10 multi-target, C# 13, `ZeroAlloc.Results`, `ZeroAlloc.Authorization`, `ZeroAlloc.Inject`, `TimeProvider`. Tests: xunit, AwesomeAssertions, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/plans/2026-09-16-thalos-subagents-design.md` (in the Daedalus repo — read it alongside this plan)

**Repo:** `C:\Projects\Prive\Thalos.NET` — **not** the Daedalus repo. Every path below is relative to that repo.

## Global Constraints

- **Target frameworks:** `net8.0;net10.0`. Everything must compile on both. `TimeProvider` exists on both — do not add a polyfill.
- **`LangVersion` 13.0, `Nullable` enable.**
- **Never throw an `AgentError` — return it.** All fallible operations return `Result<T, AgentError>` or `UnitResult<AgentError>` from `ZeroAlloc.Results`.
- **Time comes from `TimeProvider`, injected.** Never `DateTimeOffset.UtcNow`. `AddThalos` already does `services.TryAddSingleton(TimeProvider.System)`.
- **Do not reintroduce `IConversationMap.GetBySessionAsync`.** It was removed in 0.4.0 deliberately.
- **`AgentErrorCode` members are appended, never reordered or renumbered** — the enum is serialized.
- **No dependency on `ZeroAlloc.Saga` or `ZeroAlloc.Scheduling`** in any `src/` project. Spec D3.
- **Public API additions require a `PublicAPI.Unshipped.txt` entry** where the project uses the public-API analyzer.
- **Commit format:** conventional commits. This repo runs release-please — a `feat:` commit drives the minor bump to 0.5.0.
- **Commit bodies must not contain nested parentheses.** release-please's parser drops the entire commit silently on `foo(bar(baz))`. Square brackets are fine.

## File Structure

| File | Responsibility |
|---|---|
| `src/Thalos.NET.Abstractions/AgentError.cs` | Modify — three appended `AgentErrorCode` members + three factory methods |
| `src/Thalos.NET.Abstractions/Subagents/SubagentBudget.cs` | Create — token cap + deadline, a value record |
| `src/Thalos.NET.Abstractions/Subagents/SubagentRunRequest.cs` | Create — the request record |
| `src/Thalos.NET.Abstractions/Ports/ISubagentRunner.cs` | Create — the port |
| `src/Thalos.NET/Subagents/SubagentOptions.cs` | Create — host-configurable defaults and caps |
| `src/Thalos.NET/Subagents/SubagentRunner.cs` | Create — the implementation |
| `src/Thalos.NET/ThalosOptions.cs` | Modify — expose `Subagents` |
| `src/Thalos.NET.Channels/ChannelPump.cs` | Modify — Task 1, the discarded-`Result` fix |
| `src/Thalos.NET.Channels.Telegram/TelegramChannelSource.cs` | Modify — Task 2, structured `SenderId` |
| `tests/Thalos.NET.Tests.Subagents/` | Create — new xunit project |

`SubagentBudget`, `SubagentRunRequest` and `ISubagentRunner` go in **Abstractions** because Daedalus's saga handler depends on them without depending on the runtime. `SubagentRunner` and `SubagentOptions` go in **Thalos.NET** beside `ThalosAgentRuntime`, matching how `IAgentRuntime` and its implementation are already split.

---

### Task 1: Fix `ChannelPump.CreateAndBindAsync` discarding `BindAsync`'s failure

Spec D6. `CreateAndBindAsync` checks `CreateSessionAsync`'s result but discards `BindAsync`'s. When the conversation map fails, the method returns a `ConversationBinding` that was never persisted, the caller proceeds as if bound, and the operator receives nothing — while at-most-once delivery has already discarded their message. This violates the channels design's own rule that *the operator is always told something*.

**Files:**
- Modify: `src/Thalos.NET.Channels/ChannelPump.cs` — `CreateAndBindAsync`, around lines 502–523; add a `LoggerMessage` beside the existing ones
- Test: `tests/Thalos.NET.Tests.Channels/ChannelPumpBindFailureTests.cs` (create)

**Interfaces:**
- Consumes: `IConversationMap.BindAsync` returning `ValueTask<UnitResult<AgentError>>`; `ChannelNotices` for notice text
- Produces: no public API change — behaviour only

- [ ] **Step 1: Read the current method and the notices**

Run: `sed -n '500,525p' src/Thalos.NET.Channels/ChannelPump.cs` and `cat src/Thalos.NET.Channels/ChannelNotices.cs`

Confirm `BindAsync`'s result is discarded and note the existing notice constants. If `ChannelNotices` has no suitable constant, add one named `BindFailed` with the text `"Could not start a session just now. Please try again."`

- [ ] **Step 2: Write the failing test**

```csharp
using AwesomeAssertions;
using NSubstitute;
using Thalos.Channels;
using ZeroAlloc.Results;

namespace Thalos.Tests.Channels;

public class ChannelPumpBindFailureTests
{
    [Fact]
    public async Task Operator_is_told_when_the_conversation_map_fails_to_bind()
    {
        var harness = ChannelPumpHarness.Create();
        harness.ConversationMap
            .BindAsync(Arg.Any<ConversationBinding>(), Arg.Any<CancellationToken>())
            .Returns(UnitResult<AgentError>.Failure(AgentError.StoreError("bind failed")));

        await harness.PumpOneAsync("hello");

        harness.Adapter.Delivered.Should().NotBeEmpty(
            "the operator must always be told something, even when binding fails");
    }
}
```

`ChannelPumpHarness` is the existing test harness in `tests/Thalos.NET.Tests.Channels`. Open that directory first and reuse whatever construction the existing pump tests use — if the harness has a different name, use the real one rather than creating a second harness.

- [ ] **Step 3: Run the test and verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels --filter FullyQualifiedName~ChannelPumpBindFailureTests`
Expected: FAIL — `Delivered` is empty, because the bind failure is swallowed.

- [ ] **Step 4: Implement the fix**

In `CreateAndBindAsync`, replace the discarding call:

```csharp
        var bound = await _conversations.BindAsync(binding, ct).ConfigureAwait(false);
        if (bound.IsFailure)
        {
            LogBindFailed(_logger, message.ChannelId, bound.Error.Code);
            await NotifyAsync(adapter, message.ConversationId, default, ChannelNotices.BindFailed, ct).ConfigureAwait(false);
            return null;
        }
```

Add beside the other `LoggerMessage` declarations in the file, using the next unused `EventId` in that file's range:

```csharp
    [LoggerMessage(EventId = 418, Level = LogLevel.Error,
        Message = "Binding a session on channel {ChannelId} failed with {ErrorCode}; the operator was told and the session was not bound")]
    private static partial void LogBindFailed(ILogger logger, string channelId, AgentErrorCode errorCode);
```

Check the file's existing `EventId` values first and pick a free one — do not assume 418 is unused.

`SessionId` is `default` in the notify call deliberately: the session exists but is unreachable through an unpersisted binding, and `NotifyAsync`'s own remarks explain that a fabricated id an adapter cannot resolve is strictly worse than an absent one.

- [ ] **Step 5: Run the test and verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels --filter FullyQualifiedName~ChannelPumpBindFailureTests`
Expected: PASS

- [ ] **Step 6: Run the whole Channels suite for regressions**

Run: `dotnet test tests/Thalos.NET.Tests.Channels`
Expected: all green. `CreateAndBindAsync` now returns `null` on bind failure — if a caller assumed non-null, fix the caller, do not weaken the test.

- [ ] **Step 7: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "fix(channels): tell the operator when binding a session fails"
```

---

### Task 2: Restore structured logging of `SenderId`

Spec D6. `LogRejectedSender` takes a pre-formatted `string`, so the call site does `message.From?.Id.ToString(CultureInfo.InvariantCulture) ?? "(none)"` and the structured log field `SenderId` carries a string — including the literal `"(none)"` — instead of the numeric id. Querying logs by sender id is the entire purpose of that line.

**Files:**
- Modify: `src/Thalos.NET.Channels.Telegram/TelegramChannelSource.cs:295` and `:367`
- Test: `tests/Thalos.NET.Tests.Channels.Telegram/TelegramChannelSourceLoggingTests.cs` (create)

**Interfaces:**
- Consumes: nothing new
- Produces: no public API change — `LogRejectedSender` is `private static partial`

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using Microsoft.Extensions.Logging;

namespace Thalos.Tests.Channels.Telegram;

public class TelegramChannelSourceLoggingTests
{
    [Fact]
    public async Task Rejected_sender_is_logged_with_a_numeric_SenderId_state_field()
    {
        var logger = new CapturingLogger();
        var source = TelegramSourceHarness.WithAllowedUsers(logger, allowed: 111);

        await source.ReadOneAsync(fromUserId: 222, text: "hello");

        var entry = logger.Entries.Single(e => e.EventId.Id == 705);
        entry.State.Should().ContainSingle(kv => kv.Key == "SenderId")
             .Which.Value.Should().BeOfType<long>().And.Be(222L);
    }
}
```

Use the existing harness and logger fake in `tests/Thalos.NET.Tests.Channels.Telegram` if present — read that directory before writing `CapturingLogger` or `TelegramSourceHarness`, and reuse rather than duplicate. If no capturing logger exists, write one that records `EventId` and the state key/value pairs from `ILogger.Log`.

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram --filter FullyQualifiedName~TelegramChannelSourceLoggingTests`
Expected: FAIL — `SenderId` is a `string`, not a `long`.

- [ ] **Step 3: Change the signature and the call site**

Line 367 becomes:

```csharp
    [LoggerMessage(EventId = 705, Level = LogLevel.Warning, Message = "Update {UpdateId} is from sender {SenderId}, outside AllowedUserIds; dropped silently, no reply sent")]
    private static partial void LogRejectedSender(ILogger logger, long updateId, long? senderId);
```

Line 295 becomes:

```csharp
                LogRejectedSender(_logger, update.UpdateId, message.From?.Id);
```

The `CultureInfo.InvariantCulture` formatting disappears with the `ToString`. Remove the `using System.Globalization;` only if nothing else in the file uses it — check first.

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram --filter FullyQualifiedName~TelegramChannelSourceLoggingTests`
Expected: PASS

- [ ] **Step 5: Run the Telegram suite**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET.Channels.Telegram tests/Thalos.NET.Tests.Channels.Telegram
git commit -m "fix(telegram): log SenderId as a number so it stays queryable"
```

---

### Task 3: Append the three subagent error codes

**Files:**
- Modify: `src/Thalos.NET.Abstractions/AgentError.cs`
- Test: `tests/Thalos.NET.Tests.Unit/AgentErrorTests.cs` (create or extend)

**Interfaces:**
- Produces: `AgentErrorCode.SubagentBudgetExceeded`, `.SubagentDeadlineExceeded`, `.SubagentDepthExceeded`; factories `AgentError.SubagentBudgetExceeded(int)`, `.SubagentDeadlineExceeded(TimeSpan)`, `.SubagentDepthExceeded(int, int)`

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;

namespace Thalos.Tests.Unit;

public class SubagentAgentErrorTests
{
    [Fact]
    public void SubagentBudgetExceeded_carries_its_code_and_the_cap()
    {
        var error = AgentError.SubagentBudgetExceeded(5000);

        error.Code.Should().Be(AgentErrorCode.SubagentBudgetExceeded);
        error.Message.Should().Contain("5000");
    }

    [Fact]
    public void SubagentDepthExceeded_reports_both_depth_and_max()
    {
        var error = AgentError.SubagentDepthExceeded(depth: 4, max: 2);

        error.Code.Should().Be(AgentErrorCode.SubagentDepthExceeded);
        error.Message.Should().Contain("4").And.Contain("2");
    }

    [Fact]
    public void Existing_codes_keep_their_ordinal_values()
    {
        // the enum is serialized; appending must not renumber what came before
        ((int)AgentErrorCode.Validation).Should().Be(0);
        ((int)AgentErrorCode.AgentNotFound).Should().Be(1);
    }
}
```

Verify the two ordinals in step 2 against the real file before trusting them — if `Validation` is not 0, use the actual values. The point of the test is that they do not move.

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Unit --filter FullyQualifiedName~SubagentAgentErrorTests`
Expected: FAIL — `SubagentBudgetExceeded` does not exist.

- [ ] **Step 3: Append the enum members**

At the **end** of `AgentErrorCode`, after the last `Skill*` member:

```csharp
    /// <summary>A detached subagent run hit its token budget and was stopped.</summary>
    SubagentBudgetExceeded,

    /// <summary>A detached subagent run hit its deadline and was stopped.</summary>
    SubagentDeadlineExceeded,

    /// <summary>A detached subagent run was refused because it exceeded the configured nesting depth.</summary>
    SubagentDepthExceeded,
```

- [ ] **Step 4: Append the factory methods**

After the last existing factory on `AgentError`:

```csharp
    /// <summary><see cref="AgentErrorCode.SubagentBudgetExceeded"/>: the run exceeded <paramref name="maxTokens"/>.</summary>
    public static AgentError SubagentBudgetExceeded(int maxTokens) =>
        new(AgentErrorCode.SubagentBudgetExceeded, $"The subagent run exceeded its budget of {maxTokens} tokens.");

    /// <summary><see cref="AgentErrorCode.SubagentDeadlineExceeded"/>: the run exceeded <paramref name="deadline"/>.</summary>
    public static AgentError SubagentDeadlineExceeded(TimeSpan deadline) =>
        new(AgentErrorCode.SubagentDeadlineExceeded, $"The subagent run exceeded its deadline of {deadline}.");

    /// <summary><see cref="AgentErrorCode.SubagentDepthExceeded"/>: depth <paramref name="depth"/> exceeds <paramref name="max"/>.</summary>
    public static AgentError SubagentDepthExceeded(int depth, int max) =>
        new(AgentErrorCode.SubagentDepthExceeded, $"Subagent depth {depth} exceeds the configured maximum of {max}.");
```

- [ ] **Step 5: Run the test and verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Unit --filter FullyQualifiedName~SubagentAgentErrorTests`
Expected: PASS

- [ ] **Step 6: Update the public API baseline if the analyzer demands it**

Run: `dotnet build src/Thalos.NET.Abstractions -c Release`
If this fails with RS0016 ("not part of the declared API"), append the new members to `src/Thalos.NET.Abstractions/PublicAPI.Unshipped.txt` exactly as the diagnostic spells them. If the project has no `PublicAPI.*.txt`, skip this step.

- [ ] **Step 7: Commit**

```bash
git add src/Thalos.NET.Abstractions tests/Thalos.NET.Tests.Unit
git commit -m "feat(abstractions): add subagent budget, deadline and depth error codes"
```

---

### Task 4: `SubagentBudget` and `SubagentRunRequest`

**Files:**
- Create: `src/Thalos.NET.Abstractions/Subagents/SubagentBudget.cs`
- Create: `src/Thalos.NET.Abstractions/Subagents/SubagentRunRequest.cs`
- Test: `tests/Thalos.NET.Tests.Unit/SubagentRunRequestTests.cs`

**Interfaces:**
- Consumes: `AgentId`, `SessionId`, `ISecurityContext`
- Produces:
  - `SubagentBudget(int MaxTotalTokens, TimeSpan Deadline)` with `static SubagentBudget Default { get; }` = `new(50_000, TimeSpan.FromMinutes(10))`
  - `SubagentRunRequest` with `required AgentId AgentId`, `required string Task`, `required ISecurityContext Caller`, `SubagentBudget Budget`, `int Depth`, `SessionId? ParentSessionId`

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;

namespace Thalos.Tests.Unit;

public class SubagentRunRequestTests
{
    [Fact]
    public void Default_budget_is_applied_when_none_is_supplied()
    {
        var request = new SubagentRunRequest
        {
            AgentId = AgentId.New(),
            Task = "summarise the findings",
            Caller = new StubSecurityContext("schedule:test"),
        };

        request.Budget.Should().Be(SubagentBudget.Default);
        request.Depth.Should().Be(0);
        request.ParentSessionId.Should().BeNull();
    }
}
```

`StubSecurityContext` — if `tests/Thalos.NET.Tests.Unit` already has a test `ISecurityContext`, use it. Otherwise write a minimal one implementing `Id`, `Roles` and `Claims`.

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Unit --filter FullyQualifiedName~SubagentRunRequestTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Create `SubagentBudget`**

```csharp
namespace Thalos;

/// <summary>
/// Ceilings for one detached run. Both are hard stops, not hints: a detached run has no human watching it, so an
/// unbounded loop costs real money with nobody to notice.
/// </summary>
/// <param name="MaxTotalTokens">Total tokens across every model round-trip of the run. Exceeding it fails the run.</param>
/// <param name="Deadline">Wall-clock ceiling for the whole run, measured from the moment the session is created.</param>
public readonly record struct SubagentBudget(int MaxTotalTokens, TimeSpan Deadline)
{
    /// <summary>50,000 tokens and 10 minutes — enough for a multi-step research turn, small enough to notice.</summary>
    public static SubagentBudget Default => new(50_000, TimeSpan.FromMinutes(10));
}
```

- [ ] **Step 4: Create `SubagentRunRequest`**

```csharp
using ZeroAlloc.Authorization;

namespace Thalos;

/// <summary>One detached run: an agent, a task, and the identity it runs as. See <see cref="ISubagentRunner"/>.</summary>
public sealed record SubagentRunRequest
{
    /// <summary>The agent to run. Resolved through <c>IAgentCatalog</c>; a subagent is an ordinary agent definition.</summary>
    public required AgentId AgentId { get; init; }

    /// <summary>The instruction for this run, sent as the single user message of a single turn.</summary>
    public required string Task { get; init; }

    /// <summary>
    /// The identity the run executes as. Never inferred: a detached run has no inbound request to derive one from,
    /// so the caller supplies it — normally a narrow, configured principal rather than a human's own context.
    /// </summary>
    public required ISecurityContext Caller { get; init; }

    /// <summary>Token and wall-clock ceilings. Defaults to <see cref="SubagentBudget.Default"/>.</summary>
    public SubagentBudget Budget { get; init; } = SubagentBudget.Default;

    /// <summary>
    /// Nesting depth; 0 for a run started by a host. Refused above <c>SubagentOptions.MaxDepth</c>. Nothing in
    /// Thalos increments this today — sagas are compile-time, so there is no recursion to prevent yet. It exists so
    /// that a future in-turn delegation tool cannot recurse without bound.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>The session that caused this run, when there is one. Telemetry lineage only; never authorization.</summary>
    public SessionId? ParentSessionId { get; init; }
}
```

- [ ] **Step 5: Run the test and verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Unit --filter FullyQualifiedName~SubagentRunRequestTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET.Abstractions tests/Thalos.NET.Tests.Unit
git commit -m "feat(abstractions): add SubagentBudget and SubagentRunRequest"
```

---

### Task 5: The `ISubagentRunner` port and `SubagentOptions`

**Files:**
- Create: `src/Thalos.NET.Abstractions/Ports/ISubagentRunner.cs`
- Create: `src/Thalos.NET/Subagents/SubagentOptions.cs`
- Modify: `src/Thalos.NET/ThalosOptions.cs`
- Test: `tests/Thalos.NET.Tests.Unit/SubagentOptionsTests.cs`

**Interfaces:**
- Consumes: `SubagentRunRequest`, `AgentTurnResult`, `AgentError`
- Produces:
  - `ISubagentRunner.RunAsync(SubagentRunRequest, CancellationToken) -> ValueTask<Result<AgentTurnResult, AgentError>>`
  - `SubagentOptions` with `int MaxDepth { get; set; } = 2` and `SubagentBudget DefaultBudget { get; set; } = SubagentBudget.Default`
  - `ThalosOptions.Subagents { get; }` returning `SubagentOptions`

- [ ] **Step 1: Create the port**

```csharp
using ZeroAlloc.Results;

namespace Thalos;

/// <summary>
/// Runs one agent turn with <em>no live caller</em> — nobody is holding a socket open for the answer. Used by hosts
/// for scheduled runs and for orchestrated subagent steps; both are the same thing triggered differently.
/// </summary>
/// <remarks>
/// Never streams. A live turn streams because someone is watching it arrive; a detached run has no such audience, so
/// it returns the buffered <see cref="AgentTurnResult"/> and the host decides how to deliver it — typically through a
/// transactional outbox, so a crash between "the agent decided what to say" and "the channel sent it" cannot drop it.
/// </remarks>
public interface ISubagentRunner
{
    /// <summary>
    /// Creates a fresh session for <c>request.AgentId</c> owned by <c>request.Caller</c>, runs exactly one turn with
    /// <c>request.Task</c>, closes the session, and returns the result. Unknown agent →
    /// <see cref="AgentErrorCode.AgentNotFound"/>; over budget → <see cref="AgentErrorCode.SubagentBudgetExceeded"/>;
    /// past the deadline → <see cref="AgentErrorCode.SubagentDeadlineExceeded"/>; too deeply nested →
    /// <see cref="AgentErrorCode.SubagentDepthExceeded"/>. The session is closed on every path, including failure.
    /// </summary>
    ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create `SubagentOptions`**

```csharp
namespace Thalos;

/// <summary>Host-configurable ceilings for detached runs. Bound from the "Thalos" configuration section.</summary>
public sealed class SubagentOptions
{
    /// <summary>
    /// Maximum <see cref="SubagentRunRequest.Depth"/> accepted. Default 2. Nothing increments depth today — this is
    /// the guard that stops a future delegation tool recursing without bound.
    /// </summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>Budget applied when a request does not carry one.</summary>
    public SubagentBudget DefaultBudget { get; set; } = SubagentBudget.Default;
}
```

- [ ] **Step 3: Expose it on `ThalosOptions`**

Add to `src/Thalos.NET/ThalosOptions.cs`, beside `Agents` and `ToolPolicies`:

```csharp
    /// <summary>Ceilings applied to detached runs by <see cref="ISubagentRunner"/>.</summary>
    public SubagentOptions Subagents { get; } = new();
```

- [ ] **Step 4: Write and run the test**

```csharp
using AwesomeAssertions;

namespace Thalos.Tests.Unit;

public class SubagentOptionsTests
{
    [Fact]
    public void ThalosOptions_exposes_subagent_defaults()
    {
        var options = new ThalosOptions();

        options.Subagents.MaxDepth.Should().Be(2);
        options.Subagents.DefaultBudget.Should().Be(SubagentBudget.Default);
    }
}
```

Run: `dotnet test tests/Thalos.NET.Tests.Unit --filter FullyQualifiedName~SubagentOptionsTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET.Abstractions src/Thalos.NET tests/Thalos.NET.Tests.Unit
git commit -m "feat(subagents): add the ISubagentRunner port and its options"
```

---

### Task 6: Create the `Thalos.NET.Tests.Subagents` project

**Files:**
- Create: `tests/Thalos.NET.Tests.Subagents/Thalos.NET.Tests.Subagents.csproj`
- Create: `tests/Thalos.NET.Tests.Subagents/SubagentRunnerHarness.cs`
- Modify: the solution file at the repo root

**Interfaces:**
- Produces: `SubagentRunnerHarness` with `IAgentRuntime Runtime { get; }` (an NSubstitute mock), `FakeTimeProvider Time { get; }`, `SubagentOptions Options { get; }`, and `ISubagentRunner Create()`

- [ ] **Step 1: Create the csproj**

`tests/Directory.Build.props` already supplies xunit, AwesomeAssertions, NSubstitute and the `Using` items, so keep this minimal and match the sibling projects:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Thalos.NET\Thalos.NET.csproj" />
    <ProjectReference Include="..\..\src\Thalos.NET.Testing\Thalos.NET.Testing.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add it to the solution**

Run: `dotnet sln add tests/Thalos.NET.Tests.Subagents/Thalos.NET.Tests.Subagents.csproj`

If the repo uses a `.slnx`, `dotnet sln` still handles it. Confirm with `ls *.sln*` first.

- [ ] **Step 3: Write the harness**

```csharp
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Subagents;

/// <summary>Builds a SubagentRunner over a substituted IAgentRuntime and a controllable clock.</summary>
internal sealed class SubagentRunnerHarness
{
    public IAgentRuntime Runtime { get; } = Substitute.For<IAgentRuntime>();
    public FakeTimeProvider Time { get; } = new();
    public SubagentOptions Options { get; } = new();

    public static SubagentRunnerHarness Create() => new();

    public ISubagentRunner Build() => new SubagentRunner(Runtime, Options, Time, logger: null);

    public static SubagentRunRequest Request(
        AgentId? agentId = null, string task = "do the thing", int depth = 0, SubagentBudget? budget = null) =>
        new()
        {
            AgentId = agentId ?? AgentId.New(),
            Task = task,
            Caller = new StubCaller(),
            Depth = depth,
            Budget = budget ?? SubagentBudget.Default,
        };

    private sealed class StubCaller : ISecurityContext
    {
        public string Id => "schedule:test";
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) { "reader" };
        public IReadOnlyDictionary<string, string> Claims { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
```

`SubagentRunner`'s constructor signature is fixed in Task 7. If the compiler disagrees at that point, change the harness to match the implementation rather than bending the implementation to the harness.

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build tests/Thalos.NET.Tests.Subagents`
Expected: FAIL — `SubagentRunner` does not exist yet. That is correct; Task 7 creates it. Do **not** commit a broken build; this step's only purpose is to confirm the error is the expected missing type and not a packaging mistake.

- [ ] **Step 5: Do not commit yet**

This task's output is committed at the end of Task 7, when the project compiles. Proceed directly to Task 7.

---

### Task 7: Implement `SubagentRunner` — the happy path

**Files:**
- Create: `src/Thalos.NET/Subagents/SubagentRunner.cs`
- Test: `tests/Thalos.NET.Tests.Subagents/SubagentRunnerTests.cs`

**Interfaces:**
- Consumes: `IAgentRuntime`, `SubagentOptions`, `TimeProvider`, `ILogger<SubagentRunner>?`
- Produces: `SubagentRunner(IAgentRuntime runtime, SubagentOptions options, TimeProvider time, ILogger<SubagentRunner>? logger)` implementing `ISubagentRunner`

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using NSubstitute;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

public class SubagentRunnerTests
{
    [Fact]
    public async Task Creates_a_session_runs_one_turn_and_closes_it()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var expected = new AgentTurnResult(
            TurnId.New(), sessionId, "the answer", default, [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(expected));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request());

        result.IsSuccess.Should().BeTrue();
        result.Value.Text.Should().Be("the answer");
        await harness.Runtime.Received(1).CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Closes_the_session_even_when_the_turn_fails()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Failure(AgentError.ProviderError("model exploded")));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        await harness.Runtime.Received(1).CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failure_to_create_the_session_is_returned_unchanged()
    {
        var harness = SubagentRunnerHarness.Create();
        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Failure(AgentError.AgentNotFound(AgentId.New())));

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.AgentNotFound);
        await harness.Runtime.DidNotReceive().RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>());
    }
}
```

The `Result<T, AgentError>.Success(...)` / `.Failure(...)` and `UnitResult<AgentError>.Success()` forms are copied from production code in `PostgresConversationMap`, so they are correct for this version of `ZeroAlloc.Results`. If a compiler error says otherwise, match the existing tests rather than this plan.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/Thalos.NET.Tests.Subagents`
Expected: FAIL to compile — `SubagentRunner` does not exist.

- [ ] **Step 3: Implement the runner**

```csharp
using Microsoft.Extensions.Logging;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

namespace Thalos;

/// <summary>
/// Default <see cref="ISubagentRunner"/>: one detached turn on a fresh session, guarded and always closed.
/// </summary>
/// <remarks>
/// The session is closed in a <c>finally</c> rather than on the success path, because a session left Idle after a
/// failed run is indistinguishable from a live one and would hold its slot until the idle timeout. A close failure is
/// logged and swallowed: the run's own outcome is what the caller asked for, and losing a real result to report a
/// cleanup problem would be the wrong trade.
/// </remarks>
[Singleton(As = typeof(ISubagentRunner))]
public sealed partial class SubagentRunner : ISubagentRunner
{
    private readonly IAgentRuntime _runtime;
    private readonly SubagentOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SubagentRunner>? _logger;

    public SubagentRunner(IAgentRuntime runtime, SubagentOptions options, TimeProvider time, ILogger<SubagentRunner>? logger = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Depth > _options.MaxDepth)
        {
            return Result<AgentTurnResult, AgentError>.Failure(
                AgentError.SubagentDepthExceeded(request.Depth, _options.MaxDepth));
        }

        var created = await _runtime.CreateSessionAsync(request.AgentId, request.Caller, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return Result<AgentTurnResult, AgentError>.Failure(created.Error);
        }

        var sessionId = created.Value;
        try
        {
            return await RunTurnAsync(request, sessionId, ct).ConfigureAwait(false);
        }
        finally
        {
            var closed = await _runtime.CloseSessionAsync(sessionId, request.Caller, ct).ConfigureAwait(false);
            if (closed.IsFailure)
            {
                LogCloseFailed(_logger, sessionId.ToString(), closed.Error.Code);
            }
        }
    }

    private async ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(
        SubagentRunRequest request, SessionId sessionId, CancellationToken ct)
    {
        var turn = await _runtime
            .RunTurnAsync(new AgentTurnRequest(sessionId, request.Task, request.Caller), ct)
            .ConfigureAwait(false);

        return turn;
    }

    [LoggerMessage(EventId = 800, Level = LogLevel.Warning,
        Message = "Closing detached session {SessionId} failed with {ErrorCode}; the run's own result is unaffected")]
    private static partial void LogCloseFailed(ILogger? logger, string sessionId, AgentErrorCode errorCode);
}
```

If `[Singleton]` with `As =` is not how `ZeroAlloc.Inject` is used elsewhere in this repo, copy the attribute usage from an existing `[Singleton]` class in `src/Thalos.NET/` instead. Registration must end up resolvable as `ISubagentRunner`.

`SubagentOptions` is injected directly rather than via `IOptions<ThalosOptions>`; add to `ThalosServiceCollectionExtensions.AddThalos`, after the `ThalosOptions` registration:

```csharp
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<ThalosOptions>>().Value.Subagents);
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test tests/Thalos.NET.Tests.Subagents`
Expected: PASS, 3 tests.

- [ ] **Step 5: Verify the DI wiring resolves**

```csharp
    [Fact]
    public void ISubagentRunner_resolves_from_a_configured_container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThalos(t =>
        {
            t.UseInMemorySessionStore();
            t.UseChatClientProvider(_ => new ScriptedChatClient("ok"));
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISubagentRunner>().Should().NotBeNull();
    }
```

Copy the exact `AddThalos` configuration from an existing wiring test in `tests/Thalos.NET.Tests.Unit` — `UseChatClientProvider`'s signature and `ScriptedChatClient`'s constructor must match the real ones.

Run: `dotnet test tests/Thalos.NET.Tests.Subagents`
Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET tests/Thalos.NET.Tests.Subagents
git commit -m "feat(subagents): add SubagentRunner for detached agent turns"
```

---

### Task 8: Enforce the token budget

**Files:**
- Modify: `src/Thalos.NET/Subagents/SubagentRunner.cs`
- Test: `tests/Thalos.NET.Tests.Subagents/SubagentBudgetTests.cs`

**Interfaces:**
- Consumes: `AgentTurnResult.Usage` of type `TurnUsage`
- Produces: no signature change

- [ ] **Step 1: Read `TurnUsage` to learn its real field names**

Run: `cat src/Thalos.NET.Abstractions/Turns/TurnUsage.cs`

The test below assumes a total-tokens accessor. Use whatever the record actually exposes — if it carries input and output separately, sum them.

- [ ] **Step 2: Write the failing test**

```csharp
using AwesomeAssertions;
using NSubstitute;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

public class SubagentBudgetTests
{
    [Fact]
    public async Task A_turn_that_exceeds_the_token_budget_fails_the_run()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var overBudget = new AgentTurnResult(
            TurnId.New(), sessionId, "long answer", UsageOf(6_000), [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(overBudget));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(5_000, TimeSpan.FromMinutes(10)));
        var result = await harness.Build().RunAsync(request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SubagentBudgetExceeded);
        result.Error.Message.Should().Contain("5000");
    }

    [Fact]
    public async Task A_turn_inside_the_budget_succeeds()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var withinBudget = new AgentTurnResult(
            TurnId.New(), sessionId, "short answer", UsageOf(100), [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(withinBudget));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(5_000, TimeSpan.FromMinutes(10)));
        var result = await harness.Build().RunAsync(request);

        result.IsSuccess.Should().BeTrue();
    }

    // Replace with the real TurnUsage shape found in step 1.
    private static TurnUsage UsageOf(int totalTokens) => new(totalTokens, 0);
}
```

- [ ] **Step 3: Run and verify the over-budget test fails**

Run: `dotnet test tests/Thalos.NET.Tests.Subagents --filter FullyQualifiedName~SubagentBudgetTests`
Expected: the first test FAILS — the run currently succeeds.

- [ ] **Step 4: Enforce the budget**

In `RunTurnAsync`, after the turn returns successfully:

```csharp
        if (turn.IsSuccess && TotalTokens(turn.Value.Usage) > request.Budget.MaxTotalTokens)
        {
            return Result<AgentTurnResult, AgentError>.Failure(
                AgentError.SubagentBudgetExceeded(request.Budget.MaxTotalTokens));
        }

        return turn;
```

and add, using the real field names from step 1:

```csharp
    private static long TotalTokens(TurnUsage usage) => usage.InputTokens + usage.OutputTokens;
```

**This is a post-hoc check, and the plan says so on purpose.** `IAgentRuntime.RunTurnAsync` is buffered — it returns once the whole turn is done — so there is no seam to stop a turn mid-flight from out here. The budget therefore bounds what a *subsequent* step is told it may spend, and turns a runaway turn into a reported failure rather than a silent charge. Stopping mid-turn needs a cap pushed down into the runtime's round-trip loop; that is a larger change and is not in this phase. Do not claim in the commit message that this halts a runaway turn.

- [ ] **Step 5: Run and verify both pass**

Run: `dotnet test tests/Thalos.NET.Tests.Subagents --filter FullyQualifiedName~SubagentBudgetTests`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET tests/Thalos.NET.Tests.Subagents
git commit -m "feat(subagents): fail a detached run that exceeds its token budget"
```

---

### Task 9: Enforce the deadline

**Files:**
- Modify: `src/Thalos.NET/Subagents/SubagentRunner.cs`
- Test: `tests/Thalos.NET.Tests.Subagents/SubagentDeadlineTests.cs`

**Interfaces:** no signature change.

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using NSubstitute;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

public class SubagentDeadlineTests
{
    [Fact]
    public async Task The_turn_is_cancelled_when_the_deadline_passes()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        // the turn observes its token and reports cancellation, as a real provider call would
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   harness.Time.Advance(TimeSpan.FromMinutes(2));
                   var token = call.Arg<CancellationToken>();
                   return token.IsCancellationRequested
                       ? Result<AgentTurnResult, AgentError>.Failure(AgentError.Cancelled())
                       : Result<AgentTurnResult, AgentError>.Success(
                           new AgentTurnResult(TurnId.New(), sessionId, "too late", default, [], TimeSpan.Zero));
               });

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(50_000, TimeSpan.FromMinutes(1)));
        var result = await harness.Build().RunAsync(request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SubagentDeadlineExceeded);
    }
}
```

- [ ] **Step 2: Run and verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Subagents --filter FullyQualifiedName~SubagentDeadlineTests`
Expected: FAIL.

- [ ] **Step 3: Add a deadline-linked cancellation source**

In `RunAsync`, wrap the turn:

```csharp
        using var deadlineSource = new CancellationTokenSource(request.Budget.Deadline, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineSource.Token);
```

Pass `linked.Token` to `RunTurnAsync` instead of `ct`, and translate the outcome:

```csharp
        if (turn.IsFailure && deadlineSource.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Result<AgentTurnResult, AgentError>.Failure(
                AgentError.SubagentDeadlineExceeded(request.Budget.Deadline));
        }
```

The `!ct.IsCancellationRequested` guard matters: a caller cancelling is `Cancelled`, not a deadline breach, and conflating them would report a shutdown as a runaway agent.

`CloseSessionAsync` in the `finally` keeps taking `ct`, **not** `linked.Token` — closing the session is cleanup that must still happen once the deadline has fired, and passing an already-cancelled token would skip exactly the work the `finally` exists to guarantee.

The `CancellationTokenSource(TimeSpan, TimeProvider)` overload exists on net8.0 and net10.0; that is why the clock is injected.

- [ ] **Step 4: Run and verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Subagents`
Expected: PASS, all tests.

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET tests/Thalos.NET.Tests.Subagents
git commit -m "feat(subagents): cancel a detached run that passes its deadline"
```

---

### Task 10: Depth guard test, full suite, and release

**Files:**
- Test: `tests/Thalos.NET.Tests.Subagents/SubagentDepthTests.cs`
- Modify: `CHANGELOG.md` only if the repo does not generate it from release-please

**Interfaces:** none new.

- [ ] **Step 1: Write the depth test**

```csharp
using AwesomeAssertions;
using NSubstitute;

namespace Thalos.Tests.Subagents;

public class SubagentDepthTests
{
    [Fact]
    public async Task A_request_deeper_than_the_configured_maximum_is_refused_before_any_session_is_created()
    {
        var harness = SubagentRunnerHarness.Create();
        harness.Options.MaxDepth = 2;

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(depth: 3));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SubagentDepthExceeded);
        await harness.Runtime.DidNotReceive()
            .CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Depth_equal_to_the_maximum_is_allowed()
    {
        var harness = SubagentRunnerHarness.Create();
        harness.Options.MaxDepth = 2;

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(depth: 2));

        result.Error.Code.Should().NotBe(AgentErrorCode.SubagentDepthExceeded);
    }
}
```

The second test pins the boundary as inclusive — `Depth > MaxDepth` fails, `Depth == MaxDepth` passes. Without it, an off-by-one refactor flips the boundary silently.

- [ ] **Step 2: Run the new suite**

Run: `dotnet test tests/Thalos.NET.Tests.Subagents`
Expected: PASS.

- [ ] **Step 3: Baseline the entire repository, not just the projects touched**

Run: `dotnet test` from the repo root.

Record the total pass/fail per project. **This is the phase-1.4 lesson made mechanical:** a baseline limited to the projects the work touches hid a 126-test failure for an entire plan. If any suite fails, determine whether it failed before this plan started — `git stash` and re-run if necessary — and report it rather than fixing it silently.

- [ ] **Step 4: Run the new suite repeatedly to catch flakes**

Run: `for i in 1 2 3 4 5; do dotnet test tests/Thalos.NET.Tests.Subagents || echo "FAILED on run $i"; done`
Expected: five clean runs. Phase 1.4 found a test failing 4 runs in 7 while reported green; one green run is not evidence.

- [ ] **Step 5: Verify both target frameworks build**

Run: `dotnet build -c Release`
Expected: clean for `net8.0` and `net10.0`, no new analyzer warnings.

- [ ] **Step 6: Commit and push for release**

```bash
git add tests/Thalos.NET.Tests.Subagents
git commit -m "test(subagents): pin the depth guard boundary"
git push
```

release-please opens a release PR. Confirm it contains the `feat(subagents)` commits and proposes **0.5.0** — a `feat` on 0.x bumps the minor, which this repo already configured in `fix(ci): read release-please config so 0.x breaking changes bump minor`. **A green workflow does not mean the commits were counted** — open the PR and read the changelog entries. If a commit is missing, check its body for nested parentheses.

- [ ] **Step 7: Merge the release PR and confirm the package is live**

After merging, confirm `Thalos.NET 0.5.0` appears on nuget.org before starting plan B. Plan B consumes it as a `PackageReference`.

Run: `curl -s https://api.nuget.org/v3-flatcontainer/thalos.net/index.json | tail -c 200`
Expected: `0.5.0` present.

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §2 D6 — two parked 0.4.x defects | Tasks 1, 2 |
| §3 — `ISubagentRunner`, returns `AgentTurnResult` | Tasks 4, 5, 7 |
| §3 — budget load-bearing | Task 8 |
| §3 — deadline load-bearing | Task 9 |
| §3 — depth as insurance | Tasks 5, 10 |
| §3 — no fan-out guard | Deliberately absent; noted in `SubagentRunRequest` docs |
| §3 — reuses `IAgentCatalog`, no new catalogue | Task 7 — `CreateSessionAsync` resolves through the runtime |
| §7 — three appended error codes | Task 3 |
| §7 — `TimeProvider`, never `UtcNow` | Global Constraints; Task 9 |
| §7 — baseline every test project | Task 10 step 3 |
| §7 — run suites repeatedly | Task 10 step 4 |
| §8 — released as 0.5.0 | Task 10 steps 6, 7 |
| §8 — no Saga or Scheduling dependency | Global Constraints; no task adds one |

**Known deviation from the spec, recorded rather than hidden:** §3 implies the budget stops a run. Task 8 implements a **post-hoc** check, because `RunTurnAsync` is buffered and offers no mid-turn seam. The guard converts an overspend into a reported failure and bounds the next step; it does not halt a turn already in flight. Task 8 step 4 says so explicitly and forbids overclaiming it in the commit message. Mid-turn enforcement needs a cap inside the runtime's round-trip loop — out of scope here, and worth recording as a follow-up.

**Type consistency:** `ISubagentRunner.RunAsync` returns `Result<AgentTurnResult, AgentError>` in Tasks 5, 7, 8, 9 and 10 — no `SubagentRunResult` anywhere, matching the amended spec. `SubagentBudget(int MaxTotalTokens, TimeSpan Deadline)` is constructed identically in Tasks 4, 8 and 9. `SubagentRunnerHarness.Request(...)` keeps one signature across Tasks 7–10. `SubagentOptions.MaxDepth` is the only depth setting, read in Tasks 5, 7 and 10.

**Two places where the plan tells the implementer to trust the repo over the plan**, because they were written from a design rather than from the file: `TurnUsage`'s field names (Task 8 step 1) and the `ZeroAlloc.Results` factory spellings (Task 7 step 1). Both say to match existing code if it disagrees.
