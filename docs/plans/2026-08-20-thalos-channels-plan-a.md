# Phase 1.4 Channels — Plan A (Thalos.NET repo) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `Thalos.NET.Channels` and `Thalos.NET.Channels.Telegram` to the Thalos.NET library, giving the framework a working inbound channel seam with two independent implementations, and release them as Thalos.NET 0.4.0.

**Architecture:** `IChannelSource` is added to `Thalos.NET.Abstractions` as the inbound counterpart to the already-released `IChannelAdapter`. A single `ChannelPump` hosted service consumes any number of sources, resolves each inbound message to a Thalos session via `IConversationMap`, runs the turn through `IAgentRuntime.RunTurnStreamingAsync`, and renders the event stream back through the matching `IChannelAdapter` — coalescing text deltas at a per-channel cadence. Telegram lives in its own package so the seam costs no transport dependency.

**Tech Stack:** .NET (`net8.0;net10.0` multi-target), Microsoft.Agents.AI 1.18, ZeroAlloc.Results / .ValueObjects / .Validation / .Telemetry / .Inject / .Authorization, System.Text.Json source generation, xUnit + NSubstitute + AwesomeAssertions, `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/plans/2026-08-20-thalos-channels-design.md` (in the Daedalus repo; this plan argues from it — read both).

**Repo:** `C:\Projects\Prive\Thalos.NET`. All paths in this plan are relative to that repo root.

## Global Constraints

- **Target frameworks:** every new `src/` project multi-targets `net8.0;net10.0` (inherited from `Directory.Build.props` — do not override).
- **`ZeroAlloc.Rest` is forbidden here.** It is `net10.0`-only and would break the `net8.0` target. The Telegram client is hand-rolled over `HttpClient`.
- **No breaking changes to `Thalos.NET.Abstractions`.** 0.3.0 is on nuget.org. `IChannelAdapter` is not reshaped; everything new is additive.
- **`Thalos.NET.Channels` takes no third-party dependency.** Only `Thalos.NET`, `Microsoft.Extensions.*` and `ZeroAlloc.*`, matching `Thalos.NET.Skills`.
- **`Thalos.NET.Channels` must never reference `Thalos.NET.Channels.Telegram`.** Enforced by an architecture test (Task 19).
- **Global usings are configured in `tests/Directory.Build.props`** — test files carry no `using Xunit;`, `using AwesomeAssertions;` or `using NSubstitute;`.
- **Logging is `[LoggerMessage]` source-generated** with unique `EventId`s. Skills used the 5xx range; channels use the **6xx** range.
- **Every public type and member carries XML documentation.** The build treats missing docs as warnings and the repo builds with 0 warnings.
- **Conventional commits**, scope `channels` (e.g. `feat(channels): ...`).

---

### Task 1: Inbound port in Abstractions

**Files:**
- Create: `src/Thalos.NET.Abstractions/Ports/IChannelSource.cs`
- Create: `src/Thalos.NET.Abstractions/Channels/InboundMessage.cs`
- Modify: `src/Thalos.NET.Abstractions/Ids.cs`
- Modify: `tests/Thalos.NET.Tests.Unit/Abstractions/PortsShapeTests.cs:17`
- Test: `tests/Thalos.NET.Tests.Unit/Abstractions/InboundMessageTests.cs`

**Interfaces:**
- Consumes: `SessionId`, `AgentId` (existing `[TypedId]`s), `ISecurityContext` (ZeroAlloc.Authorization).
- Produces: `ConversationId` (string-backed `[TypedId]`), `InboundMessage`, `IChannelSource`. Every later task depends on these.

- [ ] **Step 1: Write the failing test**

`tests/Thalos.NET.Tests.Unit/Abstractions/InboundMessageTests.cs`:

```csharp
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Abstractions;

public sealed class InboundMessageTests
{
    [Fact]
    public void ConversationId_round_trips_a_string()
    {
        var id = new ConversationId("123456789");
        id.Value.Should().Be("123456789");
        id.ToString().Should().Be("123456789");
    }

    [Fact]
    public void InboundMessage_carries_the_caller_the_channel_supplied()
    {
        var caller = new AnonymousSecurityContext();
        var message = new InboundMessage("telegram", new ConversationId("42"), "hello", caller, "17");

        message.ChannelId.Should().Be("telegram");
        message.ConversationId.Value.Should().Be("42");
        message.Text.Should().Be("hello");
        message.Caller.Should().BeSameAs(caller);
        message.ExternalMessageId.Should().Be("17");
    }

    [Fact]
    public void ExternalMessageId_is_optional()
    {
        var message = new InboundMessage("console", new ConversationId("console"), "hi", new AnonymousSecurityContext(), null);
        message.ExternalMessageId.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Unit -f net10.0 --filter FullyQualifiedName~InboundMessageTests`
Expected: FAIL — build error, `ConversationId` and `InboundMessage` do not exist.

- [ ] **Step 3: Add the typed id**

In `src/Thalos.NET.Abstractions/Ids.cs`, beside the existing declarations, add:

```csharp
/// <summary>Identifies one external conversation within a channel (a Telegram chat id, a console session). Opaque to Thalos.</summary>
[TypedId(typeof(string))]
public readonly partial record struct ConversationId;
```

- [ ] **Step 4: Add the inbound message**

`src/Thalos.NET.Abstractions/Channels/InboundMessage.cs`:

```csharp
using ZeroAlloc.Authorization;

namespace Thalos;

/// <summary>
/// One message arriving from a channel. <paramref name="Caller"/> is supplied by the channel — Thalos never infers an
/// identity — and <paramref name="ExternalMessageId"/> is the transport's own id for the message, where it has one.
/// </summary>
public sealed record InboundMessage(
    string ChannelId,
    ConversationId ConversationId,
    string Text,
    ISecurityContext Caller,
    string? ExternalMessageId);
```

- [ ] **Step 5: Add the port**

`src/Thalos.NET.Abstractions/Ports/IChannelSource.cs`:

```csharp
namespace Thalos;

/// <summary>
/// A source of inbound messages for one channel — the counterpart to <see cref="IChannelAdapter"/>. Implementations
/// stream until <paramref name="ct"/> is cancelled and are responsible for their own authentication and filtering:
/// a message that reaches the pump has already been accepted by the channel.
/// </summary>
public interface IChannelSource
{
    /// <summary>Stable identifier of the channel; must match the <see cref="IChannelAdapter.ChannelId"/> that answers it.</summary>
    string ChannelId { get; }

    /// <summary>Streams inbound messages until cancelled.</summary>
    IAsyncEnumerable<InboundMessage> ReadAsync(CancellationToken ct);
}
```

- [ ] **Step 6: Update the ports shape pin**

In `tests/Thalos.NET.Tests.Unit/Abstractions/PortsShapeTests.cs:17`, add `"IChannelSource"` to the expected port-name list. This test exists to make new ports a deliberate act — adding the name is the deliberate act.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/Thalos.NET.Tests.Unit -f net10.0 --filter "FullyQualifiedName~InboundMessageTests|FullyQualifiedName~PortsShapeTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Thalos.NET.Abstractions tests/Thalos.NET.Tests.Unit
git commit -m "feat(channels): add IChannelSource, InboundMessage and ConversationId"
```

---

### Task 2: Package scaffold

**Files:**
- Create: `src/Thalos.NET.Channels/Thalos.NET.Channels.csproj`
- Create: `src/Thalos.NET.Channels/AssemblyInfo.cs`
- Create: `tests/Thalos.NET.Tests.Channels/Thalos.NET.Tests.Channels.csproj`
- Create: `tests/Thalos.NET.Tests.Channels/ScaffoldTests.cs`
- Modify: `Thalos.NET.slnx`

**Interfaces:**
- Produces: the `Thalos.Channels` root namespace and the two project files every later task builds into.

- [ ] **Step 1: Create the library project**

`src/Thalos.NET.Channels/Thalos.NET.Channels.csproj` — modelled on `Thalos.NET.Skills.csproj`; note there is no `<TargetFrameworks>` (inherited):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Thalos.Channels</RootNamespace>
    <PackageId>Thalos.NET.Channels</PackageId>
    <Description>Channel hosting for Thalos.NET: a ChannelPump that binds inbound IChannelSource messages to agent sessions, command handling, delta coalescing, and an in-box console channel.</Description>
    <PackageTags>agents;channels;chat;cli;microsoft-agent-framework;zeroalloc</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Thalos.NET\Thalos.NET.csproj" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
    <PackageReference Include="ZeroAlloc.Validation" />
    <PackageReference Include="ZeroAlloc.Validation.Generator" PrivateAssets="all" />
    <PackageReference Include="ZeroAlloc.Telemetry" />
    <PackageReference Include="ZeroAlloc.Inject" />
    <PackageReference Include="ZeroAlloc.Inject.Generator" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Thalos.NET.Tests.Channels" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the test project**

`tests/Thalos.NET.Tests.Channels/Thalos.NET.Tests.Channels.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Thalos.NET.Channels\Thalos.NET.Channels.csproj" />
    <ProjectReference Include="..\..\src\Thalos.NET.Testing\Thalos.NET.Testing.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write a scaffold test that proves both projects build and link**

`tests/Thalos.NET.Tests.Channels/ScaffoldTests.cs`:

```csharp
namespace Thalos.Tests.Channels;

public sealed class ScaffoldTests
{
    [Fact]
    public void Channels_assembly_is_loaded_and_multi_targets()
    {
        typeof(Thalos.Channels.ChannelsMarker).Assembly.GetName().Name.Should().Be("Thalos.NET.Channels");
    }
}
```

`src/Thalos.NET.Channels/AssemblyInfo.cs`:

```csharp
namespace Thalos.Channels;

/// <summary>Anchors the assembly for tests and for <c>ZeroAlloc.Inject</c> discovery.</summary>
internal static class ChannelsMarker;
```

Note: the marker is `internal` and reachable from the test project only because of the `InternalsVisibleTo` in Step 1.

- [ ] **Step 4: Add both projects to the solution**

```bash
dotnet sln Thalos.NET.slnx add src/Thalos.NET.Channels/Thalos.NET.Channels.csproj
dotnet sln Thalos.NET.slnx add tests/Thalos.NET.Tests.Channels/Thalos.NET.Tests.Channels.csproj
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0`
Expected: PASS, 1 test. Then `dotnet build -f net8.0 src/Thalos.NET.Channels` — expected: 0 warnings, proving the net8.0 target is honest.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels Thalos.NET.slnx
git commit -m "feat(channels): scaffold Thalos.NET.Channels and its test project"
```

---

### Task 3: Channel options and validation

**Files:**
- Create: `src/Thalos.NET.Channels/ChannelOptions.cs`
- Test: `tests/Thalos.NET.Tests.Channels/ChannelOptionsTests.cs`

**Interfaces:**
- Produces: `ChannelOptions { SectionName, Enabled, DefaultAgent, IdleTimeout, FlushInterval }`, and `ChannelOptions.Describe(o)` returning the first violation or null. Tasks 6, 8 and 11 read these.

- [ ] **Step 1: Write the failing test**

```csharp
using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class ChannelOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = "daedalus" }).Should().BeNull();
    }

    [Fact]
    public void Section_name_is_the_documented_one()
    {
        ChannelOptions.SectionName.Should().Be("Thalos:Channels");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultAgent_must_be_present(string? agent)
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = agent! })
            .Should().Contain("DefaultAgent");
    }

    [Fact]
    public void IdleTimeout_must_be_positive()
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = "a", IdleTimeout = TimeSpan.Zero })
            .Should().Contain("IdleTimeout");
    }

    [Fact]
    public void FlushInterval_may_be_zero_because_the_console_flushes_every_delta()
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = "a", FlushInterval = TimeSpan.Zero })
            .Should().BeNull();
    }

    [Fact]
    public void FlushInterval_must_not_be_negative()
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = "a", FlushInterval = TimeSpan.FromSeconds(-1) })
            .Should().Contain("FlushInterval");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelOptionsTests`
Expected: FAIL — `ChannelOptions` does not exist.

- [ ] **Step 3: Implement**

`src/Thalos.NET.Channels/ChannelOptions.cs`:

```csharp
using System.Globalization;

namespace Thalos.Channels;

/// <summary>Options for channel hosting, bound from <see cref="SectionName"/>.</summary>
public sealed class ChannelOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Thalos:Channels";

    /// <summary>Runtime switch. When false the pump starts and immediately idles, so hosts can bind it from late configuration.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Agent used when a conversation is bound implicitly or by a bare <c>/new</c>.</summary>
    public string DefaultAgent { get; set; } = string.Empty;

    /// <summary>How long a conversation may sit idle before the next message rolls it onto a fresh session.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Minimum spacing between outbound renders of a running turn. Zero means render every delta, which is what the
    /// console does; Telegram sets a second to stay inside its per-chat rate budget.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The first violation as text, or null when the options are valid.</summary>
    public static string? Describe(ChannelOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);

        if (string.IsNullOrWhiteSpace(o.DefaultAgent))
        {
            return "DefaultAgent must not be blank.";
        }

        // Plain interpolation, NOT string.Create(CultureInfo.InvariantCulture, …): TimeSpan formats
        // culture-invariantly, so the wrapper is redundant and Meziantou's MA0185 rejects it as an error.
        // Skills uses string.Create because it interpolates a double, which is culture-sensitive.
        if (o.IdleTimeout <= TimeSpan.Zero)
        {
            return $"IdleTimeout must be greater than zero (was {o.IdleTimeout}).";
        }

        if (o.FlushInterval < TimeSpan.Zero)
        {
            return $"FlushInterval must not be negative (was {o.FlushInterval}).";
        }

        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelOptionsTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "feat(channels): add ChannelOptions with validation"
```

---

### Task 4: The conversation map

**Files:**
- Create: `src/Thalos.NET.Channels/IConversationMap.cs`
- Create: `src/Thalos.NET.Channels/ConversationBinding.cs`
- Create: `src/Thalos.NET.Channels/InMemoryConversationMap.cs`
- Test: `tests/Thalos.NET.Tests.Channels/InMemoryConversationMapTests.cs`

**Interfaces:**
- Consumes: `ConversationId`, `SessionId`, `AgentId`, `AgentError`, `Result<T, AgentError>` / `UnitResult<AgentError>`.
- Produces: `IConversationMap.GetAsync/BindAsync/UnbindAsync` and `ConversationBinding(ChannelId, ConversationId, SessionId, AgentId, LastActivityAt)`. Task 8 and Daedalus's `PostgresConversationMap` (plan B) implement against these exact signatures.

- [ ] **Step 1: Write the failing test**

```csharp
using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class InMemoryConversationMapTests
{
    // AgentId is ULID-backed, so there is no AgentId("daedalus") — generate one and assert against it by value.
    private static ConversationBinding Binding(string conversation = "42") =>
        new("telegram", new ConversationId(conversation), SessionId.New(), AgentId.New(), DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Unknown_conversation_returns_null_not_an_error()
    {
        var map = new InMemoryConversationMap();
        var result = await map.GetAsync("telegram", new ConversationId("nope"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Bind_then_get_round_trips()
    {
        var map = new InMemoryConversationMap();
        var binding = Binding();

        (await map.BindAsync(binding, default)).IsSuccess.Should().BeTrue();
        var found = await map.GetAsync("telegram", new ConversationId("42"), default);

        found.Value.Should().NotBeNull();
        found.Value!.SessionId.Should().Be(binding.SessionId);
        // AgentId is a ULID-backed [TypedId], not a string — assert against the id the helper generated.
        found.Value.AgentId.Should().Be(binding.AgentId);
    }

    [Fact]
    public async Task Bind_replaces_an_existing_binding_for_the_same_conversation()
    {
        var map = new InMemoryConversationMap();
        await map.BindAsync(Binding(), default);
        var second = Binding();
        await map.BindAsync(second, default);

        var found = await map.GetAsync("telegram", new ConversationId("42"), default);
        found.Value!.SessionId.Should().Be(second.SessionId);
    }

    [Fact]
    public async Task Bindings_are_scoped_by_channel()
    {
        var map = new InMemoryConversationMap();
        await map.BindAsync(Binding(), default);

        var otherChannel = await map.GetAsync("console", new ConversationId("42"), default);
        otherChannel.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetBySession_finds_the_conversation_an_adapter_must_answer()
    {
        var map = new InMemoryConversationMap();
        var binding = Binding();
        await map.BindAsync(binding, default);

        var found = await map.GetBySessionAsync(binding.SessionId, default);
        found.Value!.ConversationId.Value.Should().Be("42");
    }

    [Fact]
    public async Task GetBySession_returns_null_for_a_session_no_conversation_is_serving()
    {
        var map = new InMemoryConversationMap();
        (await map.GetBySessionAsync(SessionId.New(), default)).Value.Should().BeNull();
    }

    [Fact]
    public async Task Unbind_removes_it_and_is_idempotent()
    {
        var map = new InMemoryConversationMap();
        await map.BindAsync(Binding(), default);

        (await map.UnbindAsync("telegram", new ConversationId("42"), default)).IsSuccess.Should().BeTrue();
        (await map.GetAsync("telegram", new ConversationId("42"), default)).Value.Should().BeNull();
        (await map.UnbindAsync("telegram", new ConversationId("42"), default)).IsSuccess.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~InMemoryConversationMapTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement the record and port**

`src/Thalos.NET.Channels/ConversationBinding.cs`:

```csharp
namespace Thalos.Channels;

/// <summary>Binds one external conversation to the Thalos session currently serving it.</summary>
public sealed record ConversationBinding(
    string ChannelId,
    ConversationId ConversationId,
    SessionId SessionId,
    AgentId AgentId,
    DateTimeOffset LastActivityAt);
```

`src/Thalos.NET.Channels/IConversationMap.cs`:

```csharp
using ZeroAlloc.Results;

namespace Thalos.Channels;

/// <summary>
/// Stores which Thalos session is serving which external conversation. Implementations are singletons and must be
/// safe for concurrent use. A conversation that has never been bound is <c>null</c>, not an error — an unbound
/// conversation is the normal state of a first message.
/// </summary>
public interface IConversationMap
{
    /// <summary>The binding for <paramref name="conversationId"/> on <paramref name="channelId"/>, or null when unbound.</summary>
    ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(string channelId, ConversationId conversationId, CancellationToken ct);

    /// <summary>
    /// The binding currently serving <paramref name="sessionId"/>, or null when none is. Outbound adapters need this:
    /// <see cref="IChannelAdapter.DeliverAsync"/> is handed a <see cref="SessionId"/> but must address a conversation.
    /// </summary>
    ValueTask<Result<ConversationBinding?, AgentError>> GetBySessionAsync(SessionId sessionId, CancellationToken ct);

    /// <summary>Creates or replaces the binding.</summary>
    ValueTask<UnitResult<AgentError>> BindAsync(ConversationBinding binding, CancellationToken ct);

    /// <summary>Removes the binding. Removing an absent binding succeeds.</summary>
    ValueTask<UnitResult<AgentError>> UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct);
}
```

- [ ] **Step 4: Implement the in-memory map**

`src/Thalos.NET.Channels/InMemoryConversationMap.cs`:

```csharp
using System.Collections.Concurrent;
using ZeroAlloc.Results;

namespace Thalos.Channels;

/// <summary>In-process <see cref="IConversationMap"/>. The default, and all the console channel ever needs.</summary>
public sealed class InMemoryConversationMap : IConversationMap
{
    private readonly ConcurrentDictionary<(string Channel, string Conversation), ConversationBinding> _bindings = new();

    /// <inheritdoc />
    public ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(string channelId, ConversationId conversationId, CancellationToken ct)
    {
        _bindings.TryGetValue((channelId, conversationId.Value), out var binding);
        return new(Result<ConversationBinding?, AgentError>.Success(binding));
    }

    /// <inheritdoc />
    public ValueTask<Result<ConversationBinding?, AgentError>> GetBySessionAsync(SessionId sessionId, CancellationToken ct)
    {
        // Linear over the live conversations: there is one per chat, and a host has a handful.
        foreach (var binding in _bindings.Values)
        {
            if (binding.SessionId == sessionId)
            {
                return new(Result<ConversationBinding?, AgentError>.Success(binding));
            }
        }

        return new(Result<ConversationBinding?, AgentError>.Success(null));
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> BindAsync(ConversationBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _bindings[(binding.ChannelId, binding.ConversationId.Value)] = binding;
        return new(UnitResult<AgentError>.Success());
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct)
    {
        _bindings.TryRemove((channelId, conversationId.Value), out _);
        return new(UnitResult<AgentError>.Success());
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~InMemoryConversationMapTests`
Expected: PASS, 7 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "feat(channels): add IConversationMap and the in-memory implementation"
```

---

### Task 5: Command parsing

**Files:**
- Create: `src/Thalos.NET.Channels/ChannelCommand.cs`
- Test: `tests/Thalos.NET.Tests.Channels/ChannelCommandTests.cs`

**Interfaces:**
- Produces: `ChannelCommandKind` enum (`None`, `New`, `End`, `Status`, `Agents`, `Cancel`, `Help`, `Unknown`) and `ChannelCommand.Parse(string text)` returning `ChannelCommand(Kind, Argument)`. Task 9 dispatches on these.

- [ ] **Step 1: Write the failing test**

```csharp
using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class ChannelCommandTests
{
    [Theory]
    [InlineData("/new", ChannelCommandKind.New)]
    [InlineData("/end", ChannelCommandKind.End)]
    [InlineData("/status", ChannelCommandKind.Status)]
    [InlineData("/agents", ChannelCommandKind.Agents)]
    [InlineData("/cancel", ChannelCommandKind.Cancel)]
    [InlineData("/help", ChannelCommandKind.Help)]
    public void Known_commands_parse(string text, ChannelCommandKind expected)
    {
        ChannelCommand.Parse(text).Kind.Should().Be(expected);
    }

    [Fact]
    public void Commands_are_case_insensitive_and_tolerate_surrounding_space()
    {
        ChannelCommand.Parse("  /NEW  ").Kind.Should().Be(ChannelCommandKind.New);
    }

    [Fact]
    public void New_captures_its_argument()
    {
        var command = ChannelCommand.Parse("/new reviewer");
        command.Kind.Should().Be(ChannelCommandKind.New);
        command.Argument.Should().Be("reviewer");
    }

    [Fact]
    public void New_without_an_argument_has_a_null_argument()
    {
        ChannelCommand.Parse("/new").Argument.Should().BeNull();
    }

    [Fact]
    public void Telegram_bot_suffix_is_stripped()
    {
        // Telegram appends @botname to commands sent in any chat where the bot is not the only recipient.
        ChannelCommand.Parse("/new@daedalus_bot reviewer").Kind.Should().Be(ChannelCommandKind.New);
        ChannelCommand.Parse("/new@daedalus_bot reviewer").Argument.Should().Be("reviewer");
    }

    [Fact]
    public void Plain_text_is_not_a_command()
    {
        ChannelCommand.Parse("what changed in the auth layer?").Kind.Should().Be(ChannelCommandKind.None);
    }

    [Fact]
    public void A_slash_prefixed_word_that_is_not_a_command_is_Unknown_not_text()
    {
        // Treating it as text would silently send "/reboot" to the model as a prompt.
        ChannelCommand.Parse("/reboot").Kind.Should().Be(ChannelCommandKind.Unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_text_is_not_a_command(string? text)
    {
        ChannelCommand.Parse(text!).Kind.Should().Be(ChannelCommandKind.None);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelCommandTests`
Expected: FAIL — `ChannelCommand` does not exist.

- [ ] **Step 3: Implement**

`src/Thalos.NET.Channels/ChannelCommand.cs`:

```csharp
namespace Thalos.Channels;

/// <summary>What an inbound message asked the pump to do.</summary>
public enum ChannelCommandKind
{
    /// <summary>Not a command — run it as a turn.</summary>
    None = 0,

    /// <summary>Start a fresh session, optionally naming an agent.</summary>
    New,

    /// <summary>Close the bound session.</summary>
    End,

    /// <summary>Report the bound session.</summary>
    Status,

    /// <summary>List available agents.</summary>
    Agents,

    /// <summary>Abort the in-flight turn.</summary>
    Cancel,

    /// <summary>List the commands.</summary>
    Help,

    /// <summary>Slash-prefixed but not recognised.</summary>
    Unknown,
}

/// <summary>A parsed channel command. <see cref="Argument"/> is the remainder of the line, or null when there is none.</summary>
public sealed record ChannelCommand(ChannelCommandKind Kind, string? Argument)
{
    private static readonly ChannelCommand NotACommand = new(ChannelCommandKind.None, null);

    /// <summary>
    /// Parses <paramref name="text"/>. Anything not starting with <c>/</c> is <see cref="ChannelCommandKind.None"/>;
    /// a slash-prefixed word that is not recognised is <see cref="ChannelCommandKind.Unknown"/> rather than text, so
    /// a mistyped command is never forwarded to the model as a prompt.
    /// </summary>
    public static ChannelCommand Parse(string text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '/')
        {
            return NotACommand;
        }

        var separator = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var word = separator < 0 ? trimmed[1..] : trimmed[1..separator];
        var argument = separator < 0 ? null : trimmed[(separator + 1)..].Trim();

        // Telegram appends @botname to commands; strip it before matching.
        var at = word.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            word = word[..at];
        }

        var kind = word.ToLowerInvariant() switch
        {
            "new" => ChannelCommandKind.New,
            "end" => ChannelCommandKind.End,
            "status" => ChannelCommandKind.Status,
            "agents" => ChannelCommandKind.Agents,
            "cancel" => ChannelCommandKind.Cancel,
            "help" => ChannelCommandKind.Help,
            _ => ChannelCommandKind.Unknown,
        };

        return new ChannelCommand(kind, string.IsNullOrEmpty(argument) ? null : argument);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelCommandTests`
Expected: PASS, 14 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "feat(channels): parse channel commands"
```

---

### Task 6: Delta coalescing

**Files:**
- Create: `src/Thalos.NET.Channels/DeltaCoalescer.cs`
- Test: `tests/Thalos.NET.Tests.Channels/DeltaCoalescerTests.cs`

**Interfaces:**
- Consumes: `ChannelOptions.FlushInterval`, `TimeProvider`.
- Produces: `DeltaCoalescer(TimeSpan flushInterval, TimeProvider clock)` with `bool TryAppend(string delta, out string? render)`, `void SetActivity(string? activity)`, `string Flush()` and `string Text { get; }`. Task 7 drives it.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Time.Testing;
using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class DeltaCoalescerTests
{
    private static (DeltaCoalescer Coalescer, FakeTimeProvider Clock) Build(double seconds = 1)
    {
        var clock = new FakeTimeProvider();
        return (new DeltaCoalescer(TimeSpan.FromSeconds(seconds), clock), clock);
    }

    [Fact]
    public void First_delta_renders_immediately_so_the_user_sees_life()
    {
        var (coalescer, _) = Build();
        coalescer.TryAppend("Hel", out var render).Should().BeTrue();
        render.Should().Be("Hel");
    }

    [Fact]
    public void Deltas_inside_the_interval_are_accumulated_but_not_rendered()
    {
        var (coalescer, _) = Build();
        coalescer.TryAppend("Hel", out _);
        coalescer.TryAppend("lo", out var render).Should().BeFalse();
        render.Should().BeNull();
        coalescer.Text.Should().Be("Hello");
    }

    [Fact]
    public void A_delta_after_the_interval_renders_everything_accumulated()
    {
        var (coalescer, clock) = Build();
        coalescer.TryAppend("Hel", out _);
        coalescer.TryAppend("lo", out _);
        clock.Advance(TimeSpan.FromSeconds(1.1));

        coalescer.TryAppend(" world", out var render).Should().BeTrue();
        render.Should().Be("Hello world");
    }

    [Fact]
    public void A_zero_interval_renders_every_delta()
    {
        var (coalescer, _) = Build(seconds: 0);
        coalescer.TryAppend("a", out _).Should().BeTrue();
        coalescer.TryAppend("b", out var render).Should().BeTrue();
        render.Should().Be("ab");
    }

    [Fact]
    public void Identical_consecutive_renders_are_suppressed()
    {
        // Telegram rejects an unchanged editMessageText with 400 "message is not modified".
        var (coalescer, clock) = Build();
        coalescer.TryAppend("a", out _);
        clock.Advance(TimeSpan.FromSeconds(2));
        coalescer.TryAppend(string.Empty, out var render).Should().BeFalse();
        render.Should().BeNull();
    }

    [Fact]
    public void Changing_the_activity_line_forces_a_render_even_mid_interval()
    {
        var (coalescer, _) = Build();
        coalescer.TryAppend("thinking", out _);
        coalescer.SetActivity("roslyn__find_callers");

        coalescer.TryAppend(string.Empty, out var render).Should().BeTrue();
        render.Should().Be("▸ roslyn__find_callers\nthinking");
    }

    [Fact]
    public void Flush_drops_the_activity_line_and_returns_the_final_text()
    {
        var (coalescer, _) = Build();
        coalescer.TryAppend("answer", out _);
        coalescer.SetActivity("some_tool");

        coalescer.Flush().Should().Be("answer");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~DeltaCoalescerTests`
Expected: FAIL — `DeltaCoalescer` does not exist.

- [ ] **Step 3: Implement**

`src/Thalos.NET.Channels/DeltaCoalescer.cs`:

```csharp
using System.Text;

namespace Thalos.Channels;

/// <summary>
/// Accumulates streamed text and decides when a channel should re-render it. Rate-limited transports set a positive
/// flush interval; the console sets zero and renders every delta. Renders that would repeat the previous one are
/// suppressed, because an unchanged edit is an error on Telegram rather than a no-op.
/// </summary>
/// <remarks>Not thread-safe: one coalescer serves one turn, driven by one loop.</remarks>
public sealed class DeltaCoalescer(TimeSpan flushInterval, TimeProvider clock)
{
    private readonly StringBuilder _text = new();
    private readonly TimeSpan _flushInterval = flushInterval;
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private string? _activity;
    private string? _lastRender;
    private long _lastRenderStamp = long.MinValue;

    /// <summary>Everything accumulated so far, without the activity line.</summary>
    public string Text => _text.ToString();

    /// <summary>Sets (or with null clears) the activity line shown above the text; the change forces the next render.</summary>
    public void SetActivity(string? activity)
    {
        _activity = activity;
        _lastRenderStamp = long.MinValue; // force the next TryAppend to render
    }

    /// <summary>
    /// Appends <paramref name="delta"/> and reports whether the channel should render now. When it returns true,
    /// <paramref name="render"/> is the full body to display; when false there is nothing new worth sending.
    /// </summary>
    public bool TryAppend(string delta, out string? render)
    {
        if (!string.IsNullOrEmpty(delta))
        {
            _text.Append(delta);
        }

        var now = _clock.GetTimestamp();
        var due = _lastRenderStamp == long.MinValue
                  || _flushInterval <= TimeSpan.Zero
                  || _clock.GetElapsedTime(_lastRenderStamp, now) >= _flushInterval;

        if (!due)
        {
            render = null;
            return false;
        }

        var candidate = Compose();
        if (string.Equals(candidate, _lastRender, StringComparison.Ordinal))
        {
            render = null;
            return false;
        }

        _lastRender = candidate;
        _lastRenderStamp = now;
        render = candidate;
        return true;
    }

    /// <summary>The final body for the turn: accumulated text with no activity line.</summary>
    public string Flush()
    {
        _activity = null;
        _lastRender = _text.ToString();
        return _lastRender;
    }

    private string Compose() =>
        _activity is null ? _text.ToString() : string.Concat("▸ ", _activity, "\n", _text.ToString());
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~DeltaCoalescerTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "feat(channels): coalesce text deltas on a per-channel cadence"
```

---

### Task 7: The pump runs a turn

**Files:**
- Create: `src/Thalos.NET.Channels/ChannelPump.cs`
- Create: `tests/Thalos.NET.Tests.Channels/Fakes/FakeChannel.cs`
- Test: `tests/Thalos.NET.Tests.Channels/ChannelPumpTurnTests.cs`

**Interfaces:**
- Consumes: `IAgentRuntime`, `IChannelSource`, `IChannelAdapter`, `IConversationMap`, `ChannelOptions`, `DeltaCoalescer`.
- Produces: `ChannelPump : BackgroundService` and the test double `FakeChannel` (implements both `IChannelSource` and `IChannelAdapter`, exposes `Delivered`). Tasks 8 and 9 extend the same class and reuse the fake.

- [ ] **Step 1: Write the test double**

`tests/Thalos.NET.Tests.Channels/Fakes/FakeChannel.cs`:

```csharp
using System.Threading.Channels;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Channels.Fakes;

/// <summary>A channel that is both source and adapter, so a test can push a message in and read what came out.</summary>
public sealed class FakeChannel : IChannelSource, IChannelAdapter
{
    private readonly Channel<InboundMessage> _inbound = Channel.CreateUnbounded<InboundMessage>();

    public string ChannelId => "fake";

    public List<AgentEvent> Delivered { get; } = [];

    public ConversationId Conversation { get; } = new("c1");

    public ISecurityContext Caller { get; } = new AnonymousSecurityContext();

    public void Send(string text) =>
        _inbound.Writer.TryWrite(new InboundMessage(ChannelId, Conversation, text, Caller, null));

    public void Complete() => _inbound.Writer.TryComplete();

    public IAsyncEnumerable<InboundMessage> ReadAsync(CancellationToken ct) => _inbound.Reader.ReadAllAsync(ct);

    public ValueTask DeliverAsync(SessionId sessionId, AgentEvent agentEvent, CancellationToken ct)
    {
        lock (Delivered)
        {
            Delivered.Add(agentEvent);
        }

        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Write the failing test**

`tests/Thalos.NET.Tests.Channels/ChannelPumpTurnTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using NSubstitute;
using Thalos.Channels;
using Thalos.Tests.Channels.Fakes;
using ZeroAlloc.Results;

namespace Thalos.Tests.Channels;

public sealed class ChannelPumpTurnTests
{
    [Fact]
    public async Task A_text_message_runs_a_turn_and_delivers_its_terminal_event()
    {
        var channel = new FakeChannel();
        var sessionId = SessionId.New();
        var turnId = TurnId.New();

        var runtime = Substitute.For<IAgentRuntime>();
        runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<SessionId, AgentError>>(Result<SessionId, AgentError>.Success(sessionId)));
        runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(Stream(sessionId, turnId));

        using var pump = Build(channel, runtime);
        channel.Send("what changed?");
        await pump.StartAsync(default);
        await WaitForTerminal(channel);
        channel.Complete();
        await pump.StopAsync(default);

        channel.Delivered.OfType<TurnCompletedEvent>().Should().ContainSingle();
        await runtime.Received(1).RunTurnStreamingAsync(
            Arg.Is<AgentTurnRequest>(r => r.Text == "what changed?" && r.SessionId == sessionId),
            Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<AgentEvent> Stream(SessionId sessionId, TurnId turnId)
    {
        yield return new TextDeltaEvent(sessionId, turnId, "it ");
        yield return new TextDeltaEvent(sessionId, turnId, "changed");
        yield return new TurnCompletedEvent(sessionId, turnId,
            new AgentTurnResult(turnId, sessionId, "it changed", default, [], TimeSpan.Zero));
        await Task.CompletedTask;
    }

    private static ChannelPump Build(FakeChannel channel, IAgentRuntime runtime)
    {
        // AgentId is a ULID; configuration and /new name agents by AgentDefinition.Name, so the pump needs a catalogue
        // to resolve "daedalus" into an id. Substituting the catalogue is what makes that resolution testable.
        var definition = new AgentDefinition { Id = AgentId.New(), Name = "daedalus", Instructions = "test" };
        var catalog = Substitute.For<IAgentCatalog>();
        catalog.Agents.Returns([definition]);

        return new ChannelPump([channel], [channel], runtime, catalog, new InMemoryConversationMap(),
            Options.Create(new ChannelOptions { DefaultAgent = "daedalus", FlushInterval = TimeSpan.Zero }),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChannelPump>.Instance);
    }

    private static async Task WaitForTerminal(FakeChannel channel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (channel.Delivered)
            {
                if (channel.Delivered.Any(e => e is TurnCompletedEvent or TurnFailedEvent))
                {
                    return;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("no terminal event was delivered");
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelPumpTurnTests`
Expected: FAIL — `ChannelPump` does not exist.

- [ ] **Step 4: Implement the pump's turn path**

`src/Thalos.NET.Channels/ChannelPump.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Thalos.Channels;

/// <summary>
/// Hosts every registered <see cref="IChannelSource"/>: reads inbound messages, binds them to agent sessions and
/// renders each turn back through the <see cref="IChannelAdapter"/> whose <c>ChannelId</c> matches. One reader loop
/// per source; messages within a conversation are handled in order.
/// </summary>
public sealed partial class ChannelPump(
    IEnumerable<IChannelSource> sources,
    IEnumerable<IChannelAdapter> adapters,
    IAgentRuntime runtime,
    IAgentCatalog catalog,
    IConversationMap conversations,
    IOptions<ChannelOptions> options,
    TimeProvider clock,
    ILogger<ChannelPump> logger) : BackgroundService
{
    private readonly IReadOnlyList<IChannelSource> _sources = [.. sources];
    private readonly Dictionary<string, IChannelAdapter> _adapters =
        adapters.ToDictionary(a => a.ChannelId, StringComparer.Ordinal);

    private readonly IAgentRuntime _runtime = runtime;
    private readonly IAgentCatalog _catalog = catalog;
    private readonly IConversationMap _conversations = conversations;
    private readonly ChannelOptions _options = options.Value;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<ChannelPump> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        await Task.WhenAll(_sources.Select(s => PumpAsync(s, stoppingToken))).ConfigureAwait(false);
    }

    private async Task PumpAsync(IChannelSource source, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(source.ChannelId, out var adapter))
        {
            LogNoAdapter(_logger, source.ChannelId);
            return;
        }

        try
        {
            await foreach (var message in source.ReadAsync(ct).ConfigureAwait(false))
            {
                await HandleAsync(message, adapter, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task HandleAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        // Task 9 replaces this with command dispatch; for now every message is a turn.
        var binding = await ResolveAsync(message, ct).ConfigureAwait(false);
        if (binding is null)
        {
            return;
        }

        await RunTurnAsync(message, binding, adapter, ct).ConfigureAwait(false);
    }

    private async Task<ConversationBinding?> ResolveAsync(InboundMessage message, CancellationToken ct)
    {
        // Task 8 replaces this with the four lifecycle edges.
        var existing = await _conversations.GetAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
        if (existing.IsSuccess && existing.Value is { } bound)
        {
            return bound;
        }

        if (ResolveAgent(_options.DefaultAgent) is not { } definition)
        {
            LogUnknownAgent(_logger, _options.DefaultAgent);
            return null;
        }

        var created = await _runtime.CreateSessionAsync(definition.Id, message.Caller, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            LogSessionFailed(_logger, message.ChannelId, created.Error.Code);
            return null;
        }

        var binding = new ConversationBinding(
            message.ChannelId, message.ConversationId, created.Value,
            definition.Id, _clock.GetUtcNow());

        await _conversations.BindAsync(binding, ct).ConfigureAwait(false);
        return binding;
    }

    /// <summary>
    /// Resolves a configured or operator-typed agent NAME to its definition. <c>AgentId</c> is a ULID, so configuration
    /// and chat commands name agents by <see cref="AgentDefinition.Name"/>; the catalogue's own TryGet only indexes by id.
    /// Case-insensitive because the name is typed by a human, often on a phone.
    /// </summary>
    private AgentDefinition? ResolveAgent(string name)
    {
        foreach (var definition in _catalog.Agents)
        {
            if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return null;
    }

    private async Task RunTurnAsync(InboundMessage message, ConversationBinding binding, IChannelAdapter adapter, CancellationToken ct)
    {
        var coalescer = new DeltaCoalescer(_options.FlushInterval, _clock);
        var request = new AgentTurnRequest(binding.SessionId, message.Text, message.Caller);

        await foreach (var agentEvent in _runtime.RunTurnStreamingAsync(request, ct).ConfigureAwait(false))
        {
            switch (agentEvent)
            {
                case TextDeltaEvent delta:
                    if (coalescer.TryAppend(delta.Text, out var render) && render is not null)
                    {
                        await adapter.DeliverAsync(binding.SessionId,
                            new TextDeltaEvent(delta.SessionId, delta.TurnId, render), ct).ConfigureAwait(false);
                    }

                    break;

                case ToolCallStartedEvent started:
                    coalescer.SetActivity(started.ToolName);
                    if (coalescer.TryAppend(string.Empty, out var toolRender) && toolRender is not null)
                    {
                        await adapter.DeliverAsync(binding.SessionId,
                            new TextDeltaEvent(started.SessionId, started.TurnId, toolRender), ct).ConfigureAwait(false);
                    }

                    break;

                case ToolCallFinishedEvent:
                    coalescer.SetActivity(null);
                    break;

                default:
                    // Terminal and informational events pass through untouched; the adapter decides how to show them.
                    await adapter.DeliverAsync(binding.SessionId, agentEvent, ct).ConfigureAwait(false);
                    break;
            }
        }

        await _conversations.BindAsync(binding with { LastActivityAt = _clock.GetUtcNow() }, ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 601, Level = LogLevel.Information, Message = "Channels are disabled; the pump is idle")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 602, Level = LogLevel.Error, Message = "Channel {ChannelId} has a source but no adapter; it will not be pumped")]
    private static partial void LogNoAdapter(ILogger logger, string channelId);

    [LoggerMessage(EventId = 603, Level = LogLevel.Error, Message = "Channel {ChannelId} could not create a session: {Code}")]
    private static partial void LogSessionFailed(ILogger logger, string channelId, AgentErrorCode code);

    [LoggerMessage(EventId = 604, Level = LogLevel.Error, Message = "No agent is registered under the name {Name}; check Thalos:Channels:DefaultAgent against the agent catalogue")]
    private static partial void LogUnknownAgent(ILogger logger, string name);
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelPumpTurnTests`
Expected: PASS, 1 test.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "feat(channels): run turns through the channel pump"
```

---

### Task 8: The four lifecycle edges

**Files:**
- Modify: `src/Thalos.NET.Channels/ChannelPump.cs` (replace `ResolveAsync`)
- Test: `tests/Thalos.NET.Tests.Channels/ChannelPumpLifecycleTests.cs`

**Interfaces:**
- Consumes: everything from Task 7.
- Produces: `ChannelPump.ResolveAsync` honouring implicit bind, idle rollover, dead-session rebind and busy rejection; plus `ChannelNotices` (static strings) so Task 9 and the adapters share exact copy.

- [ ] **Step 1: Write the failing test**

`tests/Thalos.NET.Tests.Channels/ChannelPumpLifecycleTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Thalos.Channels;
using Thalos.Tests.Channels.Fakes;
using ZeroAlloc.Results;

namespace Thalos.Tests.Channels;

public sealed class ChannelPumpLifecycleTests
{
    [Fact]
    public async Task An_unbound_conversation_is_bound_implicitly_to_the_default_agent()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("hello");

        var binding = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value;
        binding.Should().NotBeNull();
        // AgentId is a ULID, so assert against the catalogue's definition id — not against the name "daedalus".
        binding!.AgentId.Should().Be(h.DefaultAgent.Id);
    }

    [Fact]
    public async Task An_idle_conversation_rolls_onto_a_new_session_and_says_so()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("first");
        var first = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;

        h.Clock.Advance(TimeSpan.FromHours(13));
        await h.SendAndSettle("second");

        var second = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;
        second.Should().NotBe(first);
        h.Notices().Should().Contain(n => n.Contains("idle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_bound_session_the_runtime_no_longer_knows_is_rebound_and_announced()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("first");

        h.NextTurnFails(AgentErrorCode.SessionNotFound);
        await h.SendAndSettle("second");

        // The binding is cleared and the operator is asked to resend — the message is NOT silently swallowed,
        // and it is NOT auto-retried either, because a retry against a runtime that just rejected the session
        // is how a rebind loop starts.
        h.Notices().Should().Contain(n => n.Contains("Send that message again", StringComparison.Ordinal));
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull();
    }

    [Fact]
    public async Task A_busy_session_is_told_to_cancel_rather_than_queued()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("first");

        h.NextTurnFails(AgentErrorCode.SessionBusy);
        await h.SendAndSettle("second");

        h.Notices().Should().Contain(n => n.Contains("/cancel", StringComparison.Ordinal));
    }
}
```

`PumpHarness` is a test helper created in Step 2.

- [ ] **Step 2: Write the harness**

`tests/Thalos.NET.Tests.Channels/Fakes/PumpHarness.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Thalos.Channels;
using ZeroAlloc.Results;

namespace Thalos.Tests.Channels.Fakes;

/// <summary>Wires a pump over a <see cref="FakeChannel"/> with a substituted runtime and a controllable clock.</summary>
public sealed class PumpHarness : IDisposable
{
    private AgentErrorCode? _nextFailure;

    public FakeChannel Channel { get; } = new();

    public InMemoryConversationMap Map { get; } = new();

    public FakeTimeProvider Clock { get; } = new();

    public IAgentRuntime Runtime { get; } = Substitute.For<IAgentRuntime>();

    /// <summary>The agent the pump resolves "daedalus" to. AgentId is a ULID; the NAME is what config and /new carry.</summary>
    public AgentDefinition DefaultAgent { get; } = new()
    {
        Id = AgentId.New(),
        Name = "daedalus",
        Instructions = "test",
    };

    /// <summary>A second agent so /new &lt;name&gt; has something to switch to.</summary>
    public AgentDefinition OtherAgent { get; } = new()
    {
        Id = AgentId.New(),
        Name = "reviewer",
        Instructions = "test",
    };

    public IAgentCatalog Catalog { get; } = Substitute.For<IAgentCatalog>();

    public ChannelPump Pump { get; }

    public PumpHarness()
    {
        Catalog.Agents.Returns([DefaultAgent, OtherAgent]);
        Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<SessionId, AgentError>>(Result<SessionId, AgentError>.Success(SessionId.New())));

        Runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Emit(call.Arg<AgentTurnRequest>()));

        Pump = new ChannelPump([Channel], [Channel], Runtime, Catalog, Map,
            Options.Create(new ChannelOptions { DefaultAgent = "daedalus", FlushInterval = TimeSpan.Zero }),
            Clock, NullLogger<ChannelPump>.Instance);
    }

    /// <summary>Makes the next turn fail with <paramref name="code"/> instead of completing.</summary>
    public void NextTurnFails(AgentErrorCode code) => _nextFailure = code;

    /// <summary>Every plain-text body the pump delivered, in order.</summary>
    public IReadOnlyList<string> Notices()
    {
        lock (Channel.Delivered)
        {
            return [.. Channel.Delivered.OfType<TextDeltaEvent>().Select(e => e.Text)];
        }
    }

    public async Task SendAndSettle(string text)
    {
        Channel.Send(text);
        await Pump.StartAsync(default);
        await Task.Delay(50);
    }

    private async IAsyncEnumerable<AgentEvent> Emit(AgentTurnRequest request)
    {
        var turnId = TurnId.New();
        if (_nextFailure is { } code)
        {
            _nextFailure = null;
            yield return new TurnFailedEvent(request.SessionId, turnId, new AgentError(code, code.ToString()));
            yield break;
        }

        yield return new TextDeltaEvent(request.SessionId, turnId, "ok");
        yield return new TurnCompletedEvent(request.SessionId, turnId,
            new AgentTurnResult(turnId, request.SessionId, "ok", default, [], TimeSpan.Zero));
        await Task.CompletedTask;
    }

    public void Dispose() => Pump.Dispose();
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelPumpLifecycleTests`
Expected: FAIL — idle rollover and rebind are not implemented; `ChannelNotices` does not exist.

- [ ] **Step 4: Add the shared notice copy**

`src/Thalos.NET.Channels/ChannelNotices.cs`:

```csharp
namespace Thalos.Channels;

/// <summary>Operator-facing copy. Centralised so every channel says the same thing and the tests can assert on it.</summary>
public static class ChannelNotices
{
    /// <summary>Shown when an idle conversation rolls onto a fresh session.</summary>
    public const string IdleRollover = "That conversation was idle, so I started a new session. Earlier context is gone.";

    /// <summary>
    /// Shown when the bound session no longer exists. The binding is cleared so the next message starts a fresh
    /// session; this message is not auto-retried, so the copy must ask for it back rather than implying it ran.
    /// </summary>
    public const string Rebound = "That session had already ended, so I cleared it. Send that message again and I will start a new session.";

    /// <summary>Shown when a turn is already running.</summary>
    public const string Busy = "Still working on the previous message — /cancel to stop it.";

    /// <summary>Shown when <c>Thalos:Channels:DefaultAgent</c> names an agent the catalogue does not have.</summary>
    public const string UnknownDefaultAgent = "I am misconfigured: the default agent does not exist. /agents lists what is registered.";
}
```

- [ ] **Step 5: Replace `ResolveAsync` and handle the busy/dead cases**

In `src/Thalos.NET.Channels/ChannelPump.cs`, replace `ResolveAsync` with:

```csharp
    private async Task<ConversationBinding?> ResolveAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        var existing = await _conversations.GetAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
        var bound = existing.IsSuccess ? existing.Value : null;

        if (bound is not null && _clock.GetUtcNow() - bound.LastActivityAt <= _options.IdleTimeout)
        {
            return bound;
        }

        var notice = bound is null ? null : ChannelNotices.IdleRollover;
        if (ResolveAgent(_options.DefaultAgent) is not { } definition)
        {
            LogUnknownAgent(_logger, _options.DefaultAgent);
            await NotifyAsync(adapter, SessionId.New(), ChannelNotices.UnknownDefaultAgent, ct).ConfigureAwait(false);
            return null;
        }

        return await CreateAndBindAsync(message, adapter, definition.Id, notice, ct).ConfigureAwait(false);
    }

    private async Task<ConversationBinding?> CreateAndBindAsync(
        InboundMessage message, IChannelAdapter adapter, AgentId agentId, string? notice, CancellationToken ct)
    {
        var created = await _runtime.CreateSessionAsync(agentId, message.Caller, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            LogSessionFailed(_logger, message.ChannelId, created.Error.Code);
            return null;
        }

        var binding = new ConversationBinding(
            message.ChannelId, message.ConversationId, created.Value, agentId, _clock.GetUtcNow());

        await _conversations.BindAsync(binding, ct).ConfigureAwait(false);

        if (notice is not null)
        {
            await NotifyAsync(adapter, binding.SessionId, notice, ct).ConfigureAwait(false);
        }

        return binding;
    }

    private static async Task NotifyAsync(IChannelAdapter adapter, SessionId sessionId, string text, CancellationToken ct) =>
        await adapter.DeliverAsync(sessionId, new TextDeltaEvent(sessionId, TurnId.New(), text), ct).ConfigureAwait(false);
```

Then, in `RunTurnAsync`, handle the two terminal failures that mean "the binding was wrong" rather than "the turn failed". Replace the `default:` arm with:

```csharp
                case TurnFailedEvent failed when failed.Error.Code == AgentErrorCode.SessionBusy:
                    await NotifyAsync(adapter, binding.SessionId, ChannelNotices.Busy, ct).ConfigureAwait(false);
                    break;

                case TurnFailedEvent failed when failed.Error.Code is AgentErrorCode.SessionNotFound or AgentErrorCode.SessionClosed:
                    // The binding pointed at a session the runtime no longer has — rebind and tell the operator.
                    await _conversations.UnbindAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
                    await NotifyAsync(adapter, binding.SessionId, ChannelNotices.Rebound, ct).ConfigureAwait(false);
                    break;

                default:
                    await adapter.DeliverAsync(binding.SessionId, agentEvent, ct).ConfigureAwait(false);
                    break;
```

Update `HandleAsync` to pass `adapter` into `ResolveAsync`, and `RunTurnAsync` to accept `message` so it can unbind.

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelPumpLifecycleTests`
Expected: PASS, 4 tests. Then run the whole project — Task 7's test must still pass.

- [ ] **Step 7: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "feat(channels): handle implicit bind, idle rollover, rebind and busy sessions"
```

---

### Task 9: Command dispatch

**Files:**
- Modify: `src/Thalos.NET.Channels/ChannelPump.cs` (`HandleAsync`)
- Modify: `src/Thalos.NET.Channels/ChannelNotices.cs`
- Test: `tests/Thalos.NET.Tests.Channels/ChannelPumpCommandTests.cs`

**Interfaces:**
- Consumes: `ChannelCommand.Parse` (Task 5), `IAgentCatalog` (existing port, for `/agents`), `ChannelNotices`.
- Produces: command handling inside `HandleAsync`; a per-conversation `CancellationTokenSource` registry backing `/cancel`.

- [ ] **Step 1: Write the failing test**

```csharp
using Thalos.Channels;
using Thalos.Tests.Channels.Fakes;

namespace Thalos.Tests.Channels;

public sealed class ChannelPumpCommandTests
{
    [Fact]
    public async Task Slash_new_closes_the_old_session_and_binds_a_fresh_one()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("hello");
        var first = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;

        await h.SendAndSettle("/new");
        var second = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;

        second.Should().NotBe(first);
        await h.Runtime.Received(1).CloseSessionAsync(first, Arg.Any<ZeroAlloc.Authorization.ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Slash_new_with_an_argument_resolves_that_agent_by_name()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/new reviewer");

        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.AgentId.Should().Be(h.OtherAgent.Id);
    }

    [Fact]
    public async Task Slash_new_with_an_unknown_agent_name_is_refused_and_leaves_the_binding_alone()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("hello");
        var before = (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId;

        await h.SendAndSettle("/new nosuchagent");

        h.Notices().Should().Contain(n => n.Contains("/agents", StringComparison.Ordinal));
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value!.SessionId.Should().Be(before);
    }

    [Fact]
    public async Task Slash_agents_lists_agent_names_not_ids()
    {
        // AgentId renders as a 26-char ULID; showing that to a human would be useless.
        var h = new PumpHarness();
        await h.SendAndSettle("/agents");

        h.Notices().Should().Contain(n => n.Contains("daedalus", StringComparison.Ordinal) && n.Contains("reviewer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slash_end_unbinds_the_conversation()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("hello");
        await h.SendAndSettle("/end");

        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull();
    }

    [Fact]
    public async Task Slash_status_on_an_unbound_conversation_says_so_rather_than_creating_one()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/status");

        h.Notices().Should().Contain(n => n.Contains("No active session", StringComparison.Ordinal));
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_command_is_refused_and_never_sent_to_the_model()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/reboot");

        h.Notices().Should().Contain(n => n.Contains("/help", StringComparison.Ordinal));
        await h.Runtime.DidNotReceive().RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Slash_help_lists_the_commands_without_touching_the_runtime()
    {
        var h = new PumpHarness();
        await h.SendAndSettle("/help");

        h.Notices().Should().Contain(n => n.Contains("/new", StringComparison.Ordinal) && n.Contains("/cancel", StringComparison.Ordinal));
        await h.Runtime.DidNotReceive().RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelPumpCommandTests`
Expected: FAIL — commands are currently sent to the model as turns.

- [ ] **Step 3: Extend the notices**

Add to `ChannelNotices`:

```csharp
    /// <summary>Shown for /status when nothing is bound.</summary>
    public const string NoSession = "No active session. Send a message, or /new to start one.";

    /// <summary>Shown for a slash-prefixed word that is not a command.</summary>
    public const string UnknownCommand = "I do not know that command. /help lists the ones I do.";

    /// <summary>The /help body.</summary>
    public const string Help =
        "/new [agent] — start a fresh session\n" +
        "/end — close the current session\n" +
        "/status — what session am I in\n" +
        "/agents — list available agents\n" +
        "/cancel — stop the running turn\n" +
        "/help — this list";

    /// <summary>Shown for /cancel when no turn is running.</summary>
    public const string NothingToCancel = "Nothing is running.";
```

- [ ] **Step 4: Dispatch commands in `HandleAsync`**

Replace `HandleAsync` in `ChannelPump.cs`:

```csharp
    private readonly Dictionary<(string, string), CancellationTokenSource> _running = [];

    private async Task HandleAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        var command = ChannelCommand.Parse(message.Text);
        var key = (message.ChannelId, message.ConversationId.Value);

        switch (command.Kind)
        {
            case ChannelCommandKind.Help:
                await NotifyAsync(adapter, SessionId.New(), ChannelNotices.Help, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.Unknown:
                await NotifyAsync(adapter, SessionId.New(), ChannelNotices.UnknownCommand, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.Cancel:
                CancelRunning(key, adapter, ct);
                return;

            case ChannelCommandKind.New:
                await StartNewAsync(message, adapter, command.Argument, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.End:
                await EndAsync(message, adapter, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.Status:
                await StatusAsync(message, adapter, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.Agents:
                await AgentsAsync(adapter, ct).ConfigureAwait(false);
                return;

            case ChannelCommandKind.None:
            default:
                break;
        }

        var binding = await ResolveAsync(message, adapter, ct).ConfigureAwait(false);
        if (binding is null)
        {
            return;
        }

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_running)
        {
            _running[key] = turnCts;
        }

        try
        {
            await RunTurnAsync(message, binding, adapter, turnCts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_running)
            {
                _running.Remove(key);
            }
        }
    }
```

Implement the four helpers:

- **`StartNewAsync`** — resolve the agent NAME with `ResolveAgent(command.Argument ?? _options.DefaultAgent)`. If it does not resolve, send `ChannelNotices.UnknownAgent` and **return without touching the existing binding** — a typo must not destroy the session the operator is in. Otherwise close any bound session (`_runtime.CloseSessionAsync`), then `CreateAndBindAsync` with the resolved `definition.Id` and no notice.
- **`EndAsync`** — close and unbind.
- **`StatusAsync`** — report the binding, resolving its `AgentId` back to a name via `_catalog.TryGet(binding.AgentId, out var def)` so the operator sees `daedalus`, not a ULID. Report `ChannelNotices.NoSession` when unbound, and do NOT create a session.
- **`AgentsAsync`** — render `_catalog.Agents` by **`Name`** (optionally with `Description`). Never render `AgentId`: it is a 26-character ULID and means nothing to a human.
- **`CancelRunning`** — cancel the registered CTS, or send `ChannelNotices.NothingToCancel`.

Add to `ChannelNotices`:

```csharp
    /// <summary>Shown when /new names an agent the catalogue does not have.</summary>
    public const string UnknownAgent = "I do not have an agent by that name. /agents lists the ones I do.";
```

`IAgentCatalog` is already a constructor parameter and is already in `PumpHarness` — both were added in Task 7 for default-agent resolution.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0`
Expected: PASS, all tests including Tasks 7 and 8.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "feat(channels): dispatch channel commands in the pump"
```

---

### Task 10: The console channel and the configured principal

**Files:**
- Create: `src/Thalos.NET.Channels/ConfiguredSecurityContext.cs`
- Create: `src/Thalos.NET.Channels/Console/ConsoleChannelSource.cs`
- Create: `src/Thalos.NET.Channels/Console/ConsoleChannelAdapter.cs`
- Test: `tests/Thalos.NET.Tests.Channels/ConsoleChannelTests.cs`
- Test: `tests/Thalos.NET.Tests.Channels/ConfiguredSecurityContextTests.cs`

**Interfaces:**
- Consumes: `IChannelSource`, `IChannelAdapter`, `ISecurityContext`.
- Produces: `ConsoleChannelSource(TextReader, ISecurityContext)` and `ConsoleChannelAdapter(TextWriter)`, both with `ChannelId == "console"`. Both take their streams by injection so tests do not touch `System.Console`. Also produces `ConfiguredSecurityContext(string id, IEnumerable<string> roles)` — **Task 16 and Daedalus (plan B) both consume this; it lives here, not in the Telegram package and not in Daedalus, because every channel that is not HTTP has to manufacture a caller.**

- [ ] **Step 0: Write the principal and its test**

`tests/Thalos.NET.Tests.Channels/ConfiguredSecurityContextTests.cs`:

```csharp
using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class ConfiguredSecurityContextTests
{
    [Fact]
    public void Id_and_roles_come_from_configuration()
    {
        var ctx = new ConfiguredSecurityContext("telegram:marcel", ["admin"]);
        ctx.Id.Should().Be("telegram:marcel");
        ctx.Roles.Should().BeEquivalentTo(["admin"]);
    }

    [Fact]
    public void An_empty_role_set_is_the_read_only_default_and_is_never_null()
    {
        var ctx = new ConfiguredSecurityContext("telegram:marcel", []);
        ctx.Roles.Should().BeEmpty();
        ctx.Claims.Should().NotBeNull();
    }

    [Fact]
    public void Roles_compare_ordinally_so_Developer_does_not_satisfy_developer()
    {
        // DeveloperPolicy does a plain Contains; a case-insensitive set here would silently grant the mutating tools.
        new ConfiguredSecurityContext("x", ["Developer"]).Roles.Contains("developer").Should().BeFalse();
    }

    [Fact]
    public void A_blank_id_is_rejected_because_session_ownership_is_keyed_on_it()
    {
        var act = () => new ConfiguredSecurityContext("  ", []);
        act.Should().Throw<ArgumentException>();
    }
}
```

Implement `ConfiguredSecurityContext : ISecurityContext` with `Id`, `Roles` (a `HashSet<string>` built with `StringComparer.Ordinal`) and an empty `Claims` dictionary. Throw `ArgumentException` on a blank id.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using Thalos.Channels.Console;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Channels;

public sealed class ConsoleChannelTests
{
    [Fact]
    public async Task Source_yields_one_message_per_line_and_stops_at_end_of_input()
    {
        var source = new ConsoleChannelSource(new StringReader("first\nsecond\n"), new AnonymousSecurityContext());

        var messages = new List<InboundMessage>();
        await foreach (var m in source.ReadAsync(default))
        {
            messages.Add(m);
        }

        messages.Select(m => m.Text).Should().Equal("first", "second");
        messages.Should().OnlyContain(m => m.ChannelId == "console");
    }

    [Fact]
    public async Task Source_skips_blank_lines_so_a_stray_return_does_not_run_an_empty_turn()
    {
        var source = new ConsoleChannelSource(new StringReader("\n  \nreal\n"), new AnonymousSecurityContext());

        var messages = new List<InboundMessage>();
        await foreach (var m in source.ReadAsync(default))
        {
            messages.Add(m);
        }

        messages.Should().ContainSingle().Which.Text.Should().Be("real");
    }

    [Fact]
    public async Task Adapter_appends_only_what_is_new_because_the_console_cannot_edit_in_place()
    {
        var output = new StringWriter();
        var adapter = new ConsoleChannelAdapter(output);
        var sessionId = SessionId.New();
        var turnId = TurnId.New();

        await adapter.DeliverAsync(sessionId, new TextDeltaEvent(sessionId, turnId, "Hello"), default);
        await adapter.DeliverAsync(sessionId, new TextDeltaEvent(sessionId, turnId, "Hello world"), default);

        output.ToString().Should().Be("Hello world");
    }

    [Fact]
    public async Task Adapter_writes_a_newline_when_the_turn_completes()
    {
        var output = new StringWriter();
        var adapter = new ConsoleChannelAdapter(output);
        var sessionId = SessionId.New();
        var turnId = TurnId.New();

        await adapter.DeliverAsync(sessionId, new TextDeltaEvent(sessionId, turnId, "done"), default);
        await adapter.DeliverAsync(sessionId, new TurnCompletedEvent(sessionId, turnId,
            new AgentTurnResult(turnId, sessionId, "done", default, [], TimeSpan.Zero)), default);

        output.ToString().Should().Be("done\n");
    }
}
```

Note the third test: the pump sends **cumulative** renders, so a console adapter that wrote each render whole would repeat itself. It must diff against what it already printed.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ConsoleChannelTests`
Expected: FAIL — the console types do not exist.

- [ ] **Step 3: Implement the source**

`src/Thalos.NET.Channels/Console/ConsoleChannelSource.cs`:

```csharp
using System.Runtime.CompilerServices;
using ZeroAlloc.Authorization;

namespace Thalos.Channels.Console;

/// <summary>Reads one message per line. The reader is injected so hosts pass <c>System.Console.In</c> and tests pass a string.</summary>
public sealed class ConsoleChannelSource(TextReader reader, ISecurityContext caller) : IChannelSource
{
    /// <summary>The console conversation — there is only ever one.</summary>
    public static readonly ConversationId Conversation = new("console");

    /// <inheritdoc />
    public string ChannelId => "console";

    /// <inheritdoc />
    public async IAsyncEnumerable<InboundMessage> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return new InboundMessage(ChannelId, Conversation, line.Trim(), caller, null);
        }
    }
}
```

- [ ] **Step 4: Implement the adapter**

`src/Thalos.NET.Channels/Console/ConsoleChannelAdapter.cs`:

```csharp
namespace Thalos.Channels.Console;

/// <summary>
/// Writes turn output to a <see cref="TextWriter"/>. The pump renders cumulatively, so this adapter prints only the
/// suffix it has not printed yet — a terminal cannot edit what it already emitted.
/// </summary>
public sealed class ConsoleChannelAdapter(TextWriter writer) : IChannelAdapter
{
    private string _printed = string.Empty;

    /// <inheritdoc />
    public string ChannelId => "console";

    /// <inheritdoc />
    public async ValueTask DeliverAsync(SessionId sessionId, AgentEvent agentEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        switch (agentEvent)
        {
            case TextDeltaEvent delta:
                if (delta.Text.StartsWith(_printed, StringComparison.Ordinal))
                {
                    await writer.WriteAsync(delta.Text[_printed.Length..]).ConfigureAwait(false);
                }
                else
                {
                    // A notice or a re-render that is not an extension of what we printed: start a fresh line.
                    await writer.WriteAsync("\n" + delta.Text).ConfigureAwait(false);
                }

                _printed = delta.Text;
                break;

            case TurnCompletedEvent or TurnFailedEvent:
                await writer.WriteAsync('\n').ConfigureAwait(false);
                _printed = string.Empty;
                break;

            default:
                break;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ConsoleChannelTests`
Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "feat(channels): add the in-box console channel"
```

---

### Task 11: Registration

**Files:**
- Create: `src/Thalos.NET.Channels/ChannelThalosBuilderExtensions.cs`
- Test: `tests/Thalos.NET.Tests.Channels/ChannelDependencyInjectionTests.cs`

**Interfaces:**
- Produces: `ThalosBuilder.UseChannels(Action<ChannelOptions>?)`, `UseChannels(IConfiguration)`, `UseConversationMap<TMap>()`, `AddConsoleChannel()`. Plan B calls `UseChannels(configuration).UseConversationMap<PostgresConversationMap>()`.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class ChannelDependencyInjectionTests
{
    [Fact]
    public void UseChannels_registers_the_pump_and_the_default_map()
    {
        var services = new ServiceCollection();
        services.AddThalos(b => b.UseChannels(o => o.DefaultAgent = "daedalus"));

        var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().OfType<ChannelPump>().Should().ContainSingle();
        provider.GetRequiredService<IConversationMap>().Should().BeOfType<InMemoryConversationMap>();
    }

    [Fact]
    public void UseChannels_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddThalos(b => b.UseChannels(o => o.DefaultAgent = "a").UseChannels(o => o.DefaultAgent = "b"));

        var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().OfType<ChannelPump>().Should().ContainSingle();
    }

    [Fact]
    public void UseConversationMap_replaces_the_default_whichever_order_it_is_called_in()
    {
        var services = new ServiceCollection();
        services.AddThalos(b => b.UseConversationMap<InMemoryConversationMap>().UseChannels(o => o.DefaultAgent = "a"));

        services.BuildServiceProvider().GetRequiredService<IConversationMap>().Should().BeOfType<InMemoryConversationMap>();
    }

    [Fact]
    public void Invalid_options_fail_validation_rather_than_starting_a_broken_pump()
    {
        var services = new ServiceCollection();
        services.AddThalos(b => b.UseChannels(o => o.DefaultAgent = "   "));

        var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ChannelOptions>>().Value;
        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage("*DefaultAgent*");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0 --filter FullyQualifiedName~ChannelDependencyInjectionTests`
Expected: FAIL — `UseChannels` does not exist.

- [ ] **Step 3: Implement**

`src/Thalos.NET.Channels/ChannelThalosBuilderExtensions.cs`, following `SkillThalosBuilderExtensions` exactly — `AddOptions<ChannelOptions>()`, `PostConfigure` nothing, `ValidateOnStart()`, a `TryAddEnumerable(IValidateOptions<ChannelOptions>, ChannelOptionsValidator)` wrapping `ChannelOptions.Describe`, `TryAddSingleton(TimeProvider.System)`, `TryAddSingleton<IConversationMap, InMemoryConversationMap>()`, and `TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ChannelPump>())`. `UseConversationMap<TMap>` uses `services.Replace(ServiceDescriptor.Singleton<IConversationMap, TMap>())`. `AddConsoleChannel()` registers `ConsoleChannelSource` over `System.Console.In` and `ConsoleChannelAdapter` over `System.Console.Out` as `TryAddEnumerable` entries for `IChannelSource` and `IChannelAdapter`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels -f net10.0`
Expected: PASS, whole project.

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET.Channels tests/Thalos.NET.Tests.Channels
git commit -m "feat(channels): register channels on the Thalos builder"
```

---

### Task 12: Telegram package scaffold, DTOs and JSON context

**Files:**
- Create: `src/Thalos.NET.Channels.Telegram/Thalos.NET.Channels.Telegram.csproj`
- Create: `src/Thalos.NET.Channels.Telegram/TelegramDtos.cs`
- Create: `src/Thalos.NET.Channels.Telegram/TelegramJsonContext.cs`
- Create: `tests/Thalos.NET.Tests.Channels.Telegram/Thalos.NET.Tests.Channels.Telegram.csproj`
- Test: `tests/Thalos.NET.Tests.Channels.Telegram/TelegramDtoTests.cs`
- Modify: `Thalos.NET.slnx`

**Interfaces:**
- Produces: `TelegramUpdate`, `TelegramMessage`, `TelegramChat`, `TelegramUser`, `TelegramResponse<T>`, `TelegramResponseParameters`, and `TelegramJsonContext` (a `JsonSerializerContext`). Tasks 13, 16 and 17 deserialize with these.

- [ ] **Step 1: Create both projects and add them to the solution**

The library csproj mirrors Task 2's but with `<RootNamespace>Thalos.Channels.Telegram</RootNamespace>`, `<PackageId>Thalos.NET.Channels.Telegram</PackageId>`, a `ProjectReference` to `..\Thalos.NET.Channels\Thalos.NET.Channels.csproj`, and `InternalsVisibleTo Include="Thalos.NET.Tests.Channels.Telegram"`. It needs no `Microsoft.Extensions.Http` — the client takes an `HttpClient` directly.

- [ ] **Step 2: Write the failing test**

```csharp
using System.Text.Json;
using Thalos.Channels.Telegram;

namespace Thalos.Tests.Channels.Telegram;

public sealed class TelegramDtoTests
{
    [Fact]
    public void An_ok_response_deserializes_with_the_source_generated_context()
    {
        const string json = """
        {"ok":true,"result":[{"update_id":11,"message":{"message_id":5,"text":"hi",
        "chat":{"id":42,"type":"private"},"from":{"id":7,"is_bot":false}}}]}
        """;

        var response = JsonSerializer.Deserialize(json, TelegramJsonContext.Default.TelegramResponseUpdateArray);

        response!.Ok.Should().BeTrue();
        response.Result.Should().ContainSingle();
        var update = response.Result![0];
        update.UpdateId.Should().Be(11);
        update.Message!.Text.Should().Be("hi");
        update.Message.Chat.Id.Should().Be(42);
        update.Message.Chat.Type.Should().Be("private");
        update.Message.From!.Id.Should().Be(7);
    }

    [Fact]
    public void A_429_response_exposes_retry_after()
    {
        const string json = """
        {"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":7}}
        """;

        var response = JsonSerializer.Deserialize(json, TelegramJsonContext.Default.TelegramResponseUpdateArray);

        response!.Ok.Should().BeFalse();
        response.ErrorCode.Should().Be(429);
        response.Parameters!.RetryAfter.Should().Be(7);
    }

    [Fact]
    public void A_message_without_text_deserializes_with_a_null_text()
    {
        // Photos, stickers and joins all arrive as messages with no text; the source must skip them, not crash.
        const string json = """
        {"ok":true,"result":[{"update_id":1,"message":{"message_id":2,"chat":{"id":42,"type":"private"}}}]}
        """;

        var response = JsonSerializer.Deserialize(json, TelegramJsonContext.Default.TelegramResponseUpdateArray);
        response!.Result![0].Message!.Text.Should().BeNull();
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0`
Expected: FAIL — the DTOs do not exist.

- [ ] **Step 4: Implement the DTOs**

`src/Thalos.NET.Channels.Telegram/TelegramDtos.cs` — records with `[JsonPropertyName]` for every snake_case field: `TelegramResponse<T>(bool Ok, T? Result, int? ErrorCode, string? Description, TelegramResponseParameters? Parameters)`, `TelegramResponseParameters(int? RetryAfter)`, `TelegramUpdate(long UpdateId, TelegramMessage? Message)`, `TelegramMessage(long MessageId, string? Text, TelegramChat Chat, TelegramUser? From)`, `TelegramChat(long Id, string Type)`, `TelegramUser(long Id, bool IsBot)`.

- [ ] **Step 5: Implement the JSON context**

```csharp
using System.Text.Json.Serialization;

namespace Thalos.Channels.Telegram;

/// <summary>Source-generated serialization for the Bot API payloads this package sends and receives.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TelegramResponse<TelegramUpdate[]>), TypeInfoPropertyName = "TelegramResponseUpdateArray")]
[JsonSerializable(typeof(TelegramResponse<TelegramMessage>), TypeInfoPropertyName = "TelegramResponseMessage")]
[JsonSerializable(typeof(TelegramResponse<bool>), TypeInfoPropertyName = "TelegramResponseBool")]
internal sealed partial class TelegramJsonContext : JsonSerializerContext;
```

Change `internal` to `public` if the test project cannot see it through `InternalsVisibleTo` on the chosen TFM.

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0`
Expected: PASS, 3 tests. Also `dotnet build -f net8.0 src/Thalos.NET.Channels.Telegram` — 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Thalos.NET.Channels.Telegram tests/Thalos.NET.Tests.Channels.Telegram Thalos.NET.slnx
git commit -m "feat(channels): scaffold the Telegram package with source-generated DTOs"
```

---

### Task 13: The Bot API client

**Files:**
- Create: `src/Thalos.NET.Channels.Telegram/TelegramBotClient.cs`
- Create: `src/Thalos.NET.Channels.Telegram/TelegramApiException.cs`
- Test: `tests/Thalos.NET.Tests.Channels.Telegram/TelegramBotClientTests.cs`
- Test helper: `tests/Thalos.NET.Tests.Channels.Telegram/Fakes/StubHandler.cs`

**Interfaces:**
- Produces: `TelegramBotClient(HttpClient, string token, TimeProvider)` with `GetUpdatesAsync(long offset, int timeoutSeconds, ct)`, `SendMessageAsync(long chatId, string text, string? parseMode, ct)`, `EditMessageTextAsync(long chatId, long messageId, string text, string? parseMode, ct)`, `SendChatActionAsync(long chatId, string action, ct)`. `TelegramApiException` carries `ErrorCode`, `Description`, `RetryAfter`.

- [ ] **Step 1: Write the stub handler**

```csharp
namespace Thalos.Tests.Channels.Telegram.Fakes;

/// <summary>Answers each request from a queue of canned responses and records what was asked.</summary>
public sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!.PathAndQuery);
        if (request.Content is not null)
        {
            Requests.Add(await request.Content.ReadAsStringAsync(ct));
        }

        return _responses.Count > 0 ? _responses.Dequeue() : Json("""{"ok":true,"result":[]}""");
    }

    public static HttpResponseMessage Json(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
```

- [ ] **Step 2: Write the failing test**

```csharp
using Thalos.Channels.Telegram;
using Thalos.Tests.Channels.Telegram.Fakes;

namespace Thalos.Tests.Channels.Telegram;

public sealed class TelegramBotClientTests
{
    private static TelegramBotClient Build(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, "TOKEN", TimeProvider.System);

    [Fact]
    public async Task The_token_is_in_the_path_and_never_in_a_query_string()
    {
        var handler = new StubHandler(StubHandler.Json("""{"ok":true,"result":[]}"""));
        await Build(handler).GetUpdatesAsync(0, 50, default);

        handler.Requests[0].Should().StartWith("/botTOKEN/getUpdates");
    }

    [Fact]
    public async Task GetUpdates_returns_the_parsed_updates()
    {
        var handler = new StubHandler(StubHandler.Json("""
        {"ok":true,"result":[{"update_id":9,"message":{"message_id":1,"text":"hi","chat":{"id":42,"type":"private"}}}]}
        """));

        var updates = await Build(handler).GetUpdatesAsync(0, 50, default);
        updates.Should().ContainSingle().Which.UpdateId.Should().Be(9);
    }

    [Fact]
    public async Task A_429_throws_with_retry_after_so_the_caller_can_honour_it()
    {
        var handler = new StubHandler(StubHandler.Json(
            """{"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":7}}""",
            System.Net.HttpStatusCode.TooManyRequests));

        var act = async () => await Build(handler).SendMessageAsync(42, "hi", null, default);

        (await act.Should().ThrowAsync<TelegramApiException>())
            .Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_400_parse_failure_throws_with_the_error_code_so_the_adapter_can_retry_as_plain_text()
    {
        var handler = new StubHandler(StubHandler.Json(
            """{"ok":false,"error_code":400,"description":"Bad Request: can't parse entities"}""",
            System.Net.HttpStatusCode.BadRequest));

        var act = async () => await Build(handler).SendMessageAsync(42, "*broken", "MarkdownV2", default);

        (await act.Should().ThrowAsync<TelegramApiException>())
            .Which.ErrorCode.Should().Be(400);
    }

    [Fact]
    public async Task A_400_not_modified_is_swallowed_because_an_unchanged_edit_is_not_a_failure()
    {
        var handler = new StubHandler(StubHandler.Json(
            """{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""",
            System.Net.HttpStatusCode.BadRequest));

        var act = async () => await Build(handler).EditMessageTextAsync(42, 1, "same", null, default);
        await act.Should().NotThrowAsync();
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~TelegramBotClientTests`
Expected: FAIL — the client does not exist.

- [ ] **Step 4: Implement**

Implement `TelegramApiException(int errorCode, string? description, TimeSpan? retryAfter)` and `TelegramBotClient`. Every call posts JSON to `/bot{token}/{method}`, deserializes `TelegramResponse<T>` with `TelegramJsonContext`, and:

- `Ok == true` → return `Result`.
- `error_code == 400` and `Description` contains `"message is not modified"` → return without throwing (edit methods only).
- otherwise → throw `TelegramApiException` with `RetryAfter` from `parameters.retry_after` when present.

The token must be interpolated into the path, never logged; `TelegramApiException.Message` must not echo the token.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~TelegramBotClientTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET.Channels.Telegram tests/Thalos.NET.Tests.Channels.Telegram
git commit -m "feat(channels): add the Telegram Bot API client"
```

---

### Task 14: MarkdownV2 escaping

**Files:**
- Create: `src/Thalos.NET.Channels.Telegram/MarkdownV2Escaper.cs`
- Test: `tests/Thalos.NET.Tests.Channels.Telegram/MarkdownV2EscaperTests.cs`

**Interfaces:**
- Produces: `MarkdownV2Escaper.Escape(string text)` — escapes every reserved character outside fenced code blocks, and escapes backslash and backtick inside them, per the Bot API's own rules.

- [ ] **Step 1: Write the failing test**

```csharp
using Thalos.Channels.Telegram;

namespace Thalos.Tests.Channels.Telegram;

public sealed class MarkdownV2EscaperTests
{
    [Theory]
    [InlineData("_", "\\_")]
    [InlineData("*", "\\*")]
    [InlineData("[", "\\[")]
    [InlineData("]", "\\]")]
    [InlineData("(", "\\(")]
    [InlineData(")", "\\)")]
    [InlineData("~", "\\~")]
    [InlineData(">", "\\>")]
    [InlineData("#", "\\#")]
    [InlineData("+", "\\+")]
    [InlineData("-", "\\-")]
    [InlineData("=", "\\=")]
    [InlineData("|", "\\|")]
    [InlineData("{", "\\{")]
    [InlineData("}", "\\}")]
    [InlineData(".", "\\.")]
    [InlineData("!", "\\!")]
    public void Every_reserved_character_is_escaped(string input, string expected)
    {
        MarkdownV2Escaper.Escape(input).Should().Be(expected);
    }

    [Fact]
    public void Prose_with_punctuation_survives()
    {
        MarkdownV2Escaper.Escape("Done. Check HttpSecurityContextFactory.TryCreate!")
            .Should().Be("Done\\. Check HttpSecurityContextFactory\\.TryCreate\\!");
    }

    [Fact]
    public void A_fenced_code_block_keeps_its_fence_and_its_contents_unescaped()
    {
        const string input = "before\n```csharp\nvar x = a.b(1);\n```\nafter";
        var escaped = MarkdownV2Escaper.Escape(input);

        escaped.Should().Contain("```csharp\nvar x = a.b(1);\n```");
        escaped.Should().StartWith("before");
        escaped.Should().EndWith("after");
    }

    [Fact]
    public void An_unclosed_fence_is_closed_so_the_message_still_parses()
    {
        // An agent whose output was truncated mid-fence would otherwise produce a 400 for the whole message.
        var escaped = MarkdownV2Escaper.Escape("text\n```csharp\nvar x = 1;");
        escaped.Should().EndWith("```");
    }

    [Fact]
    public void A_backslash_inside_a_code_block_is_escaped_because_Telegram_requires_it()
    {
        MarkdownV2Escaper.Escape("```\nC:\\temp\n```").Should().Contain("C:\\\\temp");
    }

    [Fact]
    public void Empty_input_is_returned_unchanged()
    {
        MarkdownV2Escaper.Escape(string.Empty).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~MarkdownV2EscaperTests`
Expected: FAIL — `MarkdownV2Escaper` does not exist.

- [ ] **Step 3: Implement**

Scan the input once, tracking whether the cursor is inside a ``` fence. Outside a fence, prefix each of ``_*[]()~`>#+-=|{}.!`` with a backslash. Inside a fence, escape only `\` and `` ` ``. Track fence depth; if the string ends inside a fence, append a closing ``` before returning.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~MarkdownV2EscaperTests`
Expected: PASS, 22 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET.Channels.Telegram tests/Thalos.NET.Tests.Channels.Telegram
git commit -m "feat(channels): escape MarkdownV2 safely, fences included"
```

---

### Task 15: Message splitting

**Files:**
- Create: `src/Thalos.NET.Channels.Telegram/MessageSplitter.cs`
- Test: `tests/Thalos.NET.Tests.Channels.Telegram/MessageSplitterTests.cs`

**Interfaces:**
- Produces: `MessageSplitter.Split(string text, int limit = 4096)` returning `IReadOnlyList<string>`, each chunk within `limit`.

- [ ] **Step 1: Write the failing test**

```csharp
using Thalos.Channels.Telegram;

namespace Thalos.Tests.Channels.Telegram;

public sealed class MessageSplitterTests
{
    [Fact]
    public void Short_text_is_one_chunk()
    {
        MessageSplitter.Split("hello").Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public void Splitting_prefers_a_paragraph_boundary()
    {
        var text = new string('a', 30) + "\n\n" + new string('b', 30);
        var chunks = MessageSplitter.Split(text, limit: 40);

        chunks.Should().HaveCount(2);
        chunks[0].Should().Be(new string('a', 30));
        chunks[1].Should().Be(new string('b', 30));
    }

    [Fact]
    public void Falls_back_to_a_line_boundary_when_there_is_no_paragraph_break()
    {
        var text = new string('a', 30) + "\n" + new string('b', 30);
        var chunks = MessageSplitter.Split(text, limit: 40);

        chunks.Should().HaveCount(2);
        chunks[0].Should().Be(new string('a', 30));
    }

    [Fact]
    public void A_single_unbroken_run_longer_than_the_limit_is_hard_split_rather_than_dropped()
    {
        var chunks = MessageSplitter.Split(new string('x', 100), limit: 40);

        chunks.Should().HaveCount(3);
        chunks.Sum(c => c.Length).Should().Be(100);
        chunks.Should().OnlyContain(c => c.Length <= 40);
    }

    [Fact]
    public void Every_chunk_respects_the_limit()
    {
        var text = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"line {i}"));
        MessageSplitter.Split(text, limit: 200).Should().OnlyContain(c => c.Length <= 200);
    }

    [Fact]
    public void Empty_text_yields_no_chunks_so_nothing_is_sent()
    {
        MessageSplitter.Split(string.Empty).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~MessageSplitterTests`
Expected: FAIL — `MessageSplitter` does not exist.

- [ ] **Step 3: Implement**

Greedy: while the remainder exceeds `limit`, look for the last `\n\n` within `limit`; failing that the last `\n`; failing that cut at exactly `limit`. Trim the separator from the boundary, never from inside a chunk.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~MessageSplitterTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET.Channels.Telegram tests/Thalos.NET.Tests.Channels.Telegram
git commit -m "feat(channels): split long Telegram messages on natural boundaries"
```

---

### Task 16: The Telegram source

**Files:**
- Create: `src/Thalos.NET.Channels.Telegram/TelegramOptions.cs`
- Create: `src/Thalos.NET.Channels.Telegram/TelegramChannelSource.cs`
- Test: `tests/Thalos.NET.Tests.Channels.Telegram/TelegramChannelSourceTests.cs`

**Interfaces:**
- Consumes: `TelegramBotClient`, `IChannelSource`, `ISecurityContext`.
- Produces: `TelegramOptions { SectionName, Enabled, BotToken, AllowedUserIds, PrincipalId, Roles, PollTimeoutSeconds }` and `TelegramChannelSource`, `ChannelId == "telegram"`.

- [ ] **Step 1: Write the failing test**

```csharp
using Thalos.Channels.Telegram;
using Thalos.Tests.Channels.Telegram.Fakes;

namespace Thalos.Tests.Channels.Telegram;

public sealed class TelegramChannelSourceTests
{
    private static TelegramChannelSource Build(StubHandler handler, params long[] allowed)
    {
        var client = new TelegramBotClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, "T", TimeProvider.System);

        return new TelegramChannelSource(client, new TelegramOptions
        {
            BotToken = "T",
            AllowedUserIds = [.. allowed],
            PrincipalId = "telegram:test",
            Roles = [],
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramChannelSource>.Instance);
    }

    private static string Update(long updateId, long userId, string chatType = "private", string? text = "hi") =>
        $$"""
        {"ok":true,"result":[{"update_id":{{updateId}},"message":{"message_id":1,
        {{(text is null ? "" : $"\"text\":\"{text}\",")}}
        "chat":{"id":42,"type":"{{chatType}}"},"from":{"id":{{userId}},"is_bot":false}}}]}
        """;

    private static async Task<List<InboundMessage>> Drain(TelegramChannelSource source, int expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var messages = new List<InboundMessage>();
        try
        {
            await foreach (var m in source.ReadAsync(cts.Token))
            {
                messages.Add(m);
                if (messages.Count >= expected)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return messages;
    }

    [Fact]
    public async Task An_allow_listed_private_message_becomes_an_inbound_message()
    {
        var source = Build(new StubHandler(StubHandler.Json(Update(1, userId: 7))), allowed: 7);
        var messages = await Drain(source, 1);

        messages.Should().ContainSingle();
        messages[0].ChannelId.Should().Be("telegram");
        messages[0].ConversationId.Value.Should().Be("42");
        messages[0].Caller.Id.Should().Be("telegram:test");
    }

    [Fact]
    public async Task A_user_who_is_not_allow_listed_is_dropped_and_never_answered()
    {
        var source = Build(new StubHandler(StubHandler.Json(Update(1, userId: 999))), allowed: 7);
        (await Drain(source, 1)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_group_chat_is_dropped_even_when_the_sender_is_allow_listed()
    {
        var source = Build(new StubHandler(StubHandler.Json(Update(1, userId: 7, chatType: "group"))), allowed: 7);
        (await Drain(source, 1)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_with_no_text_is_skipped_rather_than_crashing_the_loop()
    {
        var source = Build(new StubHandler(StubHandler.Json(Update(1, userId: 7, text: null))), allowed: 7);
        (await Drain(source, 1)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_offset_advances_past_the_highest_update_seen()
    {
        var handler = new StubHandler(
            StubHandler.Json(Update(11, userId: 7)),
            StubHandler.Json("""{"ok":true,"result":[]}"""));

        var source = Build(handler, allowed: 7);
        await Drain(source, 1);

        // The second getUpdates must ask for 12 — proving the ack happened before the message was yielded.
        handler.Requests.Should().Contain(r => r.Contains("offset=12", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~TelegramChannelSourceTests`
Expected: FAIL — the source does not exist.

- [ ] **Step 3: Implement the options**

`TelegramOptions` with `SectionName = "Thalos:Channels:Telegram"`, `Enabled = true`, `BotToken = string.Empty`, `AllowedUserIds = []` (`IList<long>`), `PrincipalId = string.Empty`, `Roles = []` (`IList<string>`), `PollTimeoutSeconds = 50`, and a `Describe` that rejects a blank token, a blank principal id and an empty allow-list. **An empty allow-list must be a validation failure, not a permissive default** — the one misconfiguration that would expose the agent to anyone who finds the bot.

- [ ] **Step 4: Implement the source**

Loop: `GetUpdatesAsync(offset, PollTimeoutSeconds, ct)`; set `offset = max(update_id) + 1` **before** yielding anything (C10); then for each update apply the three gates — `message?.Text` non-blank, `chat.type == "private"`, `from.id` in `AllowedUserIds` — and yield an `InboundMessage` whose `Caller` is a `ConfiguredSecurityContext(PrincipalId, Roles)` — **the one from `Thalos.NET.Channels` built in Task 10; do not define a second copy here.** Catch `TelegramApiException` with `RetryAfter` and delay accordingly; catch other transport failures, log, and back off with a cap, never rethrowing into the pump.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~TelegramChannelSourceTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Thalos.NET.Channels.Telegram tests/Thalos.NET.Tests.Channels.Telegram
git commit -m "feat(channels): poll Telegram updates behind three admission gates"
```

---

### Task 17: The Telegram adapter

**Files:**
- Create: `src/Thalos.NET.Channels.Telegram/TelegramChannelAdapter.cs`
- Test: `tests/Thalos.NET.Tests.Channels.Telegram/TelegramChannelAdapterTests.cs`

**Interfaces:**
- Consumes: `TelegramBotClient`, `MarkdownV2Escaper`, `MessageSplitter`.
- Produces: `TelegramChannelAdapter`, `ChannelId == "telegram"`, which sends a placeholder on the first render of a turn and edits it thereafter.

- [ ] **Step 1: Write the failing test**

Cover: (a) the first `TextDeltaEvent` of a turn calls `sendMessage` and later ones call `editMessageText` against the returned `message_id`; (b) a `400` parse failure on a MarkdownV2 send is retried once with no `parse_mode` and the same text; (c) a `TurnCompletedEvent` produces a final edit and resets state so the next turn sends a fresh message; (d) a body longer than 4096 sends the first chunk and then additional messages; (e) `TurnFailedEvent` renders the error code.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~TelegramChannelAdapterTests`
Expected: FAIL — the adapter does not exist.

- [ ] **Step 3: Implement**

Keep per-session state `(long ChatId, long MessageId)?`. On the first render of a turn, `SendMessageAsync` with `parse_mode: "MarkdownV2"` over `MarkdownV2Escaper.Escape(text)` and remember the returned id; subsequently `EditMessageTextAsync`. On `TelegramApiException` with `ErrorCode == 400`, retry the same call once with `parseMode: null` and the **unescaped** text. On terminal events, do the final edit and clear the state. Split with `MessageSplitter` and send overflow chunks as new messages.

`IChannelAdapter.DeliverAsync` receives a `SessionId`, not a `ConversationId`, so the adapter resolves the chat by injecting `IConversationMap` and calling **`GetBySessionAsync(sessionId, ct)`** (added in Task 4 for exactly this). The binding's `ConversationId` is the chat id as a string; parse it with `long.Parse(..., CultureInfo.InvariantCulture)`. Do **not** keep a private `SessionId → chatId` dictionary in the adapter — that is a second source of truth that goes stale the moment a session is rebound. When `GetBySessionAsync` returns null the conversation has been unbound mid-turn: log and drop the delivery rather than throwing into the pump.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0`
Expected: PASS, whole project.

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET.Channels.Telegram tests/Thalos.NET.Tests.Channels.Telegram
git commit -m "feat(channels): render turns into one edited Telegram message"
```

---

### Task 18: Telegram registration

**Files:**
- Create: `src/Thalos.NET.Channels.Telegram/TelegramThalosBuilderExtensions.cs`
- Test: `tests/Thalos.NET.Tests.Channels.Telegram/TelegramDependencyInjectionTests.cs`

**Interfaces:**
- Produces: `ThalosBuilder.AddTelegramChannel(Action<TelegramOptions>?)` and `AddTelegramChannel(IConfiguration)`. Plan B calls the configuration overload.

- [ ] **Step 1: Write the failing test**

Assert that `AddTelegramChannel` registers exactly one `IChannelSource` and one `IChannelAdapter` with `ChannelId == "telegram"`, that it is idempotent, that a blank `BotToken` throws `OptionsValidationException` on resolve, and that an **empty `AllowedUserIds` also throws** — the check that keeps a misconfiguration from opening the bot to everyone.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0 --filter FullyQualifiedName~TelegramDependencyInjectionTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

Mirror `ChannelThalosBuilderExtensions`: options + validator + `ValidateOnStart`, a named `HttpClient` for `https://api.telegram.org/`, `TelegramBotClient` as a singleton, and `TryAddEnumerable` registrations for the source and adapter.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Thalos.NET.Tests.Channels.Telegram -f net10.0`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Thalos.NET.Channels.Telegram tests/Thalos.NET.Tests.Channels.Telegram
git commit -m "feat(channels): register the Telegram channel on the Thalos builder"
```

---

### Task 19: Architecture tests

**Files:**
- Modify: `tests/Thalos.NET.Tests.Architecture/` (add `ChannelArchitectureTests.cs`)

- [ ] **Step 1: Write the failing test**

Three rules, each matching how the existing architecture tests are written in that project:

1. `Thalos.NET.Channels` does not reference `Thalos.NET.Channels.Telegram` — the isolation the package split exists to provide.
2. `Thalos.NET.Channels` declares no **direct** `PackageReference` outside `Microsoft.Extensions.*` and `ZeroAlloc.*`. State the rule over direct references, not the transitive closure — `Thalos.NET` itself pulls in `Microsoft.Agents.AI`, so a transitive rule would fail on a dependency this package is supposed to have.
3. Every public type in both new packages carries XML documentation.

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `dotnet test tests/Thalos.NET.Tests.Architecture -f net10.0 --filter FullyQualifiedName~ChannelArchitectureTests`
Expected: PASS if the packages were built correctly; a FAIL here is a real finding, not a test bug — fix the dependency rather than the rule.

- [ ] **Step 3: Commit**

```bash
git add tests/Thalos.NET.Tests.Architecture
git commit -m "test(channels): pin the channel package boundaries"
```

---

### Task 20: Documentation and the 0.4.0 release

**Files:**
- Create: `src/Thalos.NET.Channels/README.md`
- Create: `src/Thalos.NET.Channels.Telegram/README.md`
- Modify: `README.md` (package table)
- Modify: `docs/` (channel guide, matching how Skills documented itself)

- [ ] **Step 1: Write the package READMEs**

Each must open with the quick start a consumer needs: `UseChannels(configuration)`, `AddConsoleChannel()`, `AddTelegramChannel(configuration)`, the full configuration block, and an explicit **operational note** recording the two accepted limitations: `getUpdates` refuses concurrent pollers so the Telegram source is single-instance, and delivery is at-most-once by design so a crash mid-turn drops that message.

- [ ] **Step 2: Run the full suite on both target frameworks**

```bash
dotnet build -c Release
dotnet test -c Release
```

Expected: 0 warnings, all tests pass on `net8.0` and `net10.0`. Do not proceed on a partial pass.

- [ ] **Step 3: Verify the packages pack**

```bash
dotnet pack -c Release src/Thalos.NET.Channels
dotnet pack -c Release src/Thalos.NET.Channels.Telegram
```

Expected: both `.nupkg` files produced with the documented description and tags.

- [ ] **Step 4: Commit and open the release PR**

```bash
git add .
git commit -m "docs(channels): document the channel packages"
```

Then open a PR. **release-please owns the version bump and the tag** — per `docs/planning/CONVENTIONS.md` this project does not tag milestones by hand. Merging the `feat(channels)` commits produces the 0.4.0 release PR; merging that publishes to nuget.org.

- [ ] **Step 5: Confirm 0.4.0 is live before starting plan B**

```bash
dotnet package search Thalos.NET.Channels --exact-match
```

Expected: 0.4.0 listed. Plan B cannot start until both new packages resolve from nuget.org.

---

## Self-Review

**Spec coverage.** §3 ports → Task 1. §4 packages → Tasks 2, 12. §5 sessions and commands → Tasks 4, 5, 8, 9. §6 Telegram adapter → Tasks 13–17. §7 console adapter → Task 10. §8 identity gates → Task 16 (the three gates and `ConfiguredSecurityContext`); the *Daedalus* principal wiring is plan B. §9 outbox → plan B (Daedalus owns the EF Core store). §10 testing → every task, plus Task 19. §11 delivery → Task 20.

**Gap found and accepted:** the spec's §9 outbox has no task here because `ZeroAlloc.Outbox.EfCore` binds to `ApplicationDbContext`, which lives in Daedalus. `Thalos.NET.Channels` deliberately stays persistence-free. Plan B carries it.

**Type consistency.** `IConversationMap` is spelled identically in Tasks 4, 8, 11 and 17. `ConversationBinding`'s five members are used unchanged in Tasks 4, 8 and 9. `DeltaCoalescer`'s `TryAppend`/`SetActivity`/`Flush` in Task 6 match the calls in Task 7. `ChannelCommandKind` members in Task 5 match the switch arms in Task 9. `TelegramJsonContext.Default.TelegramResponseUpdateArray` in Task 12 matches its use in Task 13.

**One deliberate deviation from strict TDD**, flagged so a reviewer does not read it as an oversight: Tasks 7, 8 and 9 build one class in three passes, so Task 7 ships two methods explicitly marked as replaced by later tasks. The alternative — one enormous task — would fail the right-sizing rule, and each of the three ends with its own green suite.
