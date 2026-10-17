using System.Collections.Concurrent;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Configuration;
using Daedalus.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Unit.Infrastructure.Extensions;

/// <summary>
///     Phase 2.8: <c>AgentFactory</c> reads the typed <see cref="ClaudeConfiguration"/>, bound from
///     <c>ExternalServices:Llm:Claude</c>, instead of the raw section. The dead LLM and platform options are gone.
///     <para>
///         #385: the model is no longer a constant. With no <c>ExternalServices:Llm:Claude:Model</c> it is
///         <c>Thalos:Anthropic:DefaultModel</c>, so one setting decides the model of every Anthropic call.
///     </para>
/// </summary>
public sealed class ClaudeConfigurationBindingTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings, ILoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            if (logs is not null)
            {
                builder.AddProvider(logs);
            }
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.AddExternalServices(configuration);
        services.AddAgentFrameworkServices(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Red: drop the <c>Configure&lt;ClaudeConfiguration&gt;</c> registration, or bind another section; the
    ///     options keep their defaults.
    /// </summary>
    [Fact]
    public void The_claude_section_binds_to_the_typed_options()
    {
        using var provider = Build(new(StringComparer.Ordinal)
        {
            ["ExternalServices:Llm:Claude:ApiKey"] = "a-key",
            ["ExternalServices:Llm:Claude:Model"] = "claude-test-model",
            ["ExternalServices:Llm:Claude:MaxTokens"] = "1234",
        });

        var claude = provider.GetRequiredService<IOptions<ClaudeConfiguration>>().Value;

        claude.ApiKey.Should().Be("a-key");
        claude.Model.Should().Be("claude-test-model");
        claude.MaxTokens.Should().Be(1234);
    }

    /// <summary>Red: give <c>AgentFactory</c> a constructor dependency these registrations do not provide; it cannot be resolved.</summary>
    [Fact]
    public async Task The_agent_factory_resolves_from_these_registrations()
    {
        await using var provider = Build(new(StringComparer.Ordinal) { ["Thalos:Anthropic:DefaultModel"] = "claude-from-thalos" });
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IAgentFactory>();

        act.Should().NotThrow();
    }

    /// <summary>
    ///     With no Claude model, the Thalos default model is the one used.
    ///     Red: default <c>ClaudeConfiguration.Model</c> to a constant again; the model is that constant.
    /// </summary>
    [Fact]
    public void With_no_claude_model_the_thalos_default_model_is_used()
    {
        using var provider = Build(new(StringComparer.Ordinal)
        {
            ["Thalos:Anthropic:DefaultModel"] = "claude-from-thalos",
        });

        var claude = provider.GetRequiredService<IOptions<ClaudeConfiguration>>().Value;

        claude.Model.Should().Be("claude-from-thalos");
    }

    /// <summary>
    ///     A Claude model of its own wins over the Thalos default.
    ///     Red: always take <c>Thalos:Anthropic:DefaultModel</c> in the post-configure step; the model is the Thalos one.
    /// </summary>
    [Fact]
    public void A_claude_model_of_its_own_wins_over_the_thalos_default()
    {
        using var provider = Build(new(StringComparer.Ordinal)
        {
            ["Thalos:Anthropic:DefaultModel"] = "claude-from-thalos",
            ["ExternalServices:Llm:Claude:Model"] = "claude-of-its-own",
        });

        var claude = provider.GetRequiredService<IOptions<ClaudeConfiguration>>().Value;

        claude.Model.Should().Be("claude-of-its-own");
    }

    /// <summary>
    ///     With no model anywhere, reading the options fails and names both settings, instead of calling a model nobody chose.
    ///     Red: drop the validation; the options resolve with a blank model.
    ///     Red: name only one setting in the failure; the message misses the other.
    /// </summary>
    [Fact]
    public void With_no_model_anywhere_the_options_fail_naming_both_settings()
    {
        using var provider = Build([]);

        var act = () => provider.GetRequiredService<IOptions<ClaudeConfiguration>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Thalos:Anthropic:DefaultModel").And.Contain("ExternalServices:Llm:Claude:Model");
    }

    /// <summary>
    ///     The resolved model is the one the Claude call is made with: the factory logs it as it starts the call. The
    ///     token is cancelled up front, so the call stops before it reaches Anthropic.
    ///     Red: give <c>AgentFactory</c> a model of its own when the options carry one; the call names that model.
    /// </summary>
    [Fact]
    public async Task The_configured_model_reaches_the_claude_call()
    {
        var logs = new CapturingLoggerProvider();
        await using var provider = Build(new(StringComparer.Ordinal)
        {
            ["Thalos:Anthropic:DefaultModel"] = "claude-from-thalos",
            ["ExternalServices:Llm:Claude:ApiKey"] = "a-key",
        }, logs);
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IAgentFactory>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await factory.InvokeAsync("Write a PRD.", cancelled.Token);

        result.IsFailure.Should().BeTrue("the token was cancelled before the call");
        logs.Lines.Should().ContainSingle(l => l.StartsWith("Agent invocation starting", StringComparison.Ordinal))
            .Which.Should().EndWith("Model=claude-from-thalos");
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
