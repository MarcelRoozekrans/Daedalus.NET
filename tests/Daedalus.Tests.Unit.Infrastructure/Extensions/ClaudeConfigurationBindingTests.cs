using Daedalus.Infrastructure.Configuration;
using Daedalus.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            ["ExternalServices:Llm:Claude:Model"] = "claude-test-model",
            ["ExternalServices:Llm:Claude:MaxTokens"] = "1234",
        });

        var claude = provider.GetRequiredService<IOptions<ClaudeConfiguration>>().Value;

        claude.Model.Should().Be("claude-test-model");
        claude.MaxTokens.Should().Be(1234);
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
}
