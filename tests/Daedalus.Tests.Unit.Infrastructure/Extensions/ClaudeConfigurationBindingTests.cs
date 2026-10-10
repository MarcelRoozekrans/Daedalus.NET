using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Configuration;
using Daedalus.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Unit.Infrastructure.Extensions;

/// <summary>
///     Phase 2.8: <c>AgentFactory</c> reads the typed <see cref="ClaudeConfiguration"/>, bound from
///     <c>ExternalServices:Llm:Claude</c>, instead of the raw section. The dead LLM and platform options are gone.
/// </summary>
public sealed class ClaudeConfigurationBindingTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
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
        await using var provider = Build([]);
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IAgentFactory>();

        act.Should().NotThrow();
    }
}
