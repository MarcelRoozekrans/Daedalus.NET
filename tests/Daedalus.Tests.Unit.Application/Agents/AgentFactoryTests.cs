using System.Collections.Concurrent;
using Daedalus.Agents;
using Daedalus.Agents.Llm;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Infrastructure.Agents;
using Daedalus.Infrastructure.Configuration;
using Daedalus.Infrastructure.Extensions;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Anthropic;

namespace Daedalus.Tests.Unit.Application.Agents;

/// <summary>
///     Phase 3.3, Task 18: <c>AgentFactory</c> lives in <c>Daedalus.Agents</c> and asks Thalos's
///     <see cref="IChatClientProvider"/> for its chat client instead of building an Anthropic SDK client itself.
/// </summary>
public sealed class AgentFactoryTests
{
    private const string Ulid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    private static Dictionary<string, string?> Settings(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Anthropic:DefaultModel"] = "claude-from-thalos",
            ["Thalos:Agents:0:Id"] = Ulid,
            ["Thalos:Agents:0:Name"] = "Daedalus Assistant",
            ["Thalos:Agents:0:Description"] = "Knows the codebase",
            ["Thalos:Agents:0:Instructions"] = "You are helpful.",
            ["Thalos:Squad:FallbackAgentName"] = "Daedalus Assistant",
            ["Thalos:Workflow:TurnBudgets:Default"] = "875000",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return values;
    }

    private static AgentFactory Factory(
        IChatClientProvider provider,
        CapturingLoggerProvider? logs = null,
        McpToolBuilder? tools = null,
        McpIntegrationOptions? mcp = null,
        string model = "claude-test-model",
        int maxTokens = 1234)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (logs is not null)
            {
                b.AddProvider(logs);
            }
        });
        var sp = services.BuildServiceProvider();
        return new AgentFactory(
            provider,
            Options.Create(new ClaudeConfiguration { Model = model, MaxTokens = maxTokens }),
            tools ?? new McpToolBuilder(sp, NullLogger<McpToolBuilder>.Instance),
            mcp ?? new McpIntegrationOptions { Enabled = false },
            sp.GetRequiredService<ILogger<AgentFactory>>());
    }

    /// <summary>
    ///     The registered factory is the one from <c>AddDaedalusAgents</c>, and it resolves over the registered provider.
    ///     Red: drop the <c>AddScoped</c> of <c>IAgentFactory</c> in <c>AddDaedalusAgents</c>; resolution throws.
    /// </summary>
    [Fact]
    public async Task The_agent_factory_resolves_from_AddDaedalusAgents()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(Path.GetTempPath());
        environment.EnvironmentName.Returns("Development");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddSingleton(Substitute.For<IPullRequestFactory>());
        services.AddExternalServices(configuration);
        services.AddAgentFrameworkServices(configuration);
        services.AddDaedalusAgents(configuration, environment);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var factory = scope.ServiceProvider.GetRequiredService<IAgentFactory>();

        // Red: register another IAgentFactory after AddDaedalusAgents; the resolved type is not AgentFactory.
        factory.Should().BeOfType<AgentFactory>();
        // Red: drop thalos.UseAnthropic from AddDaedalusAgents; no provider is registered and the test fails at the resolve above.
        provider.GetRequiredService<IChatClientProvider>().Should().BeOfType<AnthropicChatClientProvider>();
    }

    /// <summary>
    ///     Red: build an <c>AnthropicChatClientProvider</c> directly in <c>AgentFactory</c> instead of asking the injected
    ///     provider; the fake sees no agent definition and no request.
    /// </summary>
    [Fact]
    public async Task The_configured_model_and_max_tokens_reach_the_call()
    {
        var client = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")));
        var provider = new FakeProvider(client);
        var logs = new CapturingLoggerProvider();
        var factory = Factory(provider, logs, model: "claude-from-thalos", maxTokens: 777);

        var result = await factory.InvokeAsync("ping", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var request = client.Requests.Should().ContainSingle().Which;
        // Red: send options.ModelId = "other-model"; this fails.
        request.Options!.ModelId.Should().Be("claude-from-thalos");
        // Red: send options.MaxOutputTokens = 1; this fails.
        request.Options.MaxOutputTokens.Should().Be(777);
        // Red: build the AgentDefinition with Model = null; the provider is asked for the wrong model.
        provider.Agents.Should().ContainSingle().Which.Model.Should().Be("claude-from-thalos");
        // Red: build the AgentDefinition without MaxOutputTokens; the provider is asked for the wrong limit.
        provider.Agents.Single().MaxOutputTokens.Should().Be(777);
        // Red: log a different model or drop LogInvocationStarting; no line ends in the model.
        logs.Lines.Should().ContainSingle(l => l.StartsWith("Agent invocation starting", StringComparison.Ordinal))
            .Which.Should().EndWith("Model=claude-from-thalos");
    }

    /// <summary>
    ///     One local tool is attached, the client asks for it once, and the reply after the tool result is the answer.
    ///     Red: drop <c>UseFunctionInvocation</c> from <c>CreateChatClient</c>; the tool call is never run and the result is
    ///     the empty first reply.
    ///     Red: stop passing the built tools in <c>ChatOptions.Tools</c>; the tool is not found and is not invoked.
    /// </summary>
    [Fact]
    public async Task Mcp_tools_are_attached_and_invoked()
    {
        var memory = Substitute.For<ILearningsMemory>();
        memory.RecallAsync("needle", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RecalledLearning>>.Success([]));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(memory);
        services.AddSingleton(Substitute.For<IFailurePatternDatabase>());
        await using var sp = services.BuildServiceProvider();
        await using var tools = new McpToolBuilder(sp, NullLogger<McpToolBuilder>.Instance);
        var mcp = new McpIntegrationOptions
        {
            Enabled = true,
            Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal) { ["daedalus"] = new() { Type = "local" } },
        };
        var client = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "search_learnings", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "needle" })])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "the answer")));
        var factory = Factory(new FakeProvider(client), tools: tools, mcp: mcp);

        var result = await factory.InvokeAsync("find it", CancellationToken.None);

        // Red: drop UseFunctionInvocation; the first reply has no text and the result is a failure.
        result.IsSuccess.Should().BeTrue();
        result.Value.Response.Should().Be("the answer");
        // Red: stop passing the tools in ChatOptions; the first request carries no tool.
        client.Requests[0].Options!.Tools.Should().Contain(t => t.Name == "search_learnings");
        // Red: drop UseFunctionInvocation; the memory is never asked.
        await memory.Received(1).RecallAsync("needle", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The Anthropic provider's client reports <c>InputTokenCount</c> as uncached plus cache write plus cache read, so a
    ///     10, 20, 30 and 5 usage arrives as 60 in and 5 out; the factory passes both through. The provider's HTTP transport
    ///     is not injectable from outside Thalos, so the summing itself is Thalos's contract and the response here is the
    ///     one its client builds.
    /// </summary>
    [Fact]
    public async Task Usage_input_and_output_reach_LlmInvocationResult()
    {
        const long uncached = 10, cacheWrite = 20, cacheRead = 30, output = 5;
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = uncached + cacheWrite + cacheRead,
                OutputTokenCount = output,
            },
        };
        var factory = Factory(new FakeProvider(new ScriptedChatClient(response)));

        var result = await factory.InvokeAsync("hi", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Red: map InputTokens from OutputTokenCount; this fails with 5.
        result.Value.InputTokens.Should().Be(60);
        // Red: map OutputTokens from InputTokenCount; this fails with 60.
        result.Value.OutputTokens.Should().Be(5);
    }

    /// <summary>
    ///     The provider throws on first use when no key is set; the factory's existing catch turns it into a failure that
    ///     names the Thalos key.
    /// </summary>
    [Fact]
    public async Task A_missing_api_key_is_a_failure_result_not_an_exception()
    {
        var factory = Factory(NoKeyProvider());

        var result = await factory.InvokeAsync("hi", CancellationToken.None);

        // Red: create the client outside the try block; the exception escapes and the call throws.
        result.IsFailure.Should().BeTrue();
        // Red: have the catch report a fixed "Anthropic API key is not configured"; the key is not named.
        result.Error.Should().Contain("Thalos:Anthropic:ApiKey");
    }

    /// <summary>
    ///     The retired <c>ExternalServices:Llm:Claude:ApiKey</c> is not read: with it set and no Thalos key the call fails.
    /// </summary>
    [Fact]
    public async Task A_section_ApiKey_is_no_longer_read()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings(("ExternalServices:Llm:Claude:ApiKey", "section-key")))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalServices(configuration);
        services.AddAgentFrameworkServices(configuration);
        await using var sp = services.BuildServiceProvider();
        var claude = sp.GetRequiredService<IOptions<ClaudeConfiguration>>().Value;
        // Red: add an ApiKey property to ClaudeConfiguration again; this assertion fails.
        claude.GetType().GetProperty("ApiKey").Should().BeNull("ClaudeConfiguration has no ApiKey any more");
        var factory = Factory(NoKeyProvider(), model: claude.Model!);

        var result = await factory.InvokeAsync("hi", CancellationToken.None);

        // Red: create the client outside the try block; the exception escapes and the call throws.
        result.IsFailure.Should().BeTrue();
        // Red: have the catch report a fixed text without the exception message; the Thalos key is not named.
        result.Error.Should().Contain("Thalos:Anthropic:ApiKey");
    }

    /// <summary>
    ///     The caller owns the client, so the factory disposes it. Red: build the client without <c>using</c>; Disposed stays 0.
    /// </summary>
    [Fact]
    public async Task The_chat_client_is_disposed_after_the_call()
    {
        var client = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")));
        var factory = Factory(new FakeProvider(client));

        await factory.InvokeAsync("ping", CancellationToken.None);

        // Red: drop the using on the chat client in InvokeAsync; the count stays 0.
        client.Disposed.Should().Be(1);
    }

    /// <summary>
    ///     A provider that fails the way <c>AnthropicChatClientProvider</c> does with no key: on <c>CreateChatClient</c>, with
    ///     its message. The real one also reads ANTHROPIC_API_KEY, which a test must not depend on being unset.
    /// </summary>
    private static ThrowingProvider NoKeyProvider() => new();

    private sealed class ThrowingProvider : IChatClientProvider
    {
        public string Name => "throwing";

        public string DefaultModel => "none";

        public IChatClient CreateChatClient(AgentDefinition agent) => throw new InvalidOperationException(
            "Anthropic API key missing. Set Thalos:Anthropic:ApiKey or the ANTHROPIC_API_KEY environment variable.");
    }

    private sealed class FakeProvider(IChatClient client) : IChatClientProvider
    {
        public List<AgentDefinition> Agents { get; } = [];

        public string Name => "fake";

        public string DefaultModel => "fake-default";

        public IChatClient CreateChatClient(AgentDefinition agent)
        {
            Agents.Add(agent);
            return client;
        }
    }

    private sealed record Request(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

    private sealed class ScriptedChatClient(params ChatResponse[] replies) : IChatClient
    {
        private readonly Queue<ChatResponse> _replies = new(replies);

        public List<Request> Requests { get; } = [];

        public int Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(new Request([.. messages], options));
            return Task.FromResult(_replies.Dequeue());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed++;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception));
        }
    }
}
