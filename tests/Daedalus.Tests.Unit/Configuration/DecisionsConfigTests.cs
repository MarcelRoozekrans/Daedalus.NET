using AwesomeAssertions.Execution;
using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Phase 2.10 task B1: the per-role model menus and the <c>Thalos:Decisions</c> section, bound from the shipped Api
///     configuration and validated at registration by <c>ValidateDecisionsConfig</c>.
/// </summary>
public sealed class DecisionsConfigTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private static (ServiceCollection Services, DaedalusAgentsOptions Options, IConfiguration Configuration, IHostEnvironment Environment) LoadShippedApi()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ApiAppSettingsFileName, optional: false)
            .Build();
        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        return (services, options, configuration, environment);
    }

    /// <summary>
    ///     The shipped menus and defaults. Red: change the implementer's menu in appsettings.json (drop
    ///     claude-haiku-5-5), the reviewer's menu, or any Decisions default in <c>DecisionsConfig</c>.
    /// </summary>
    [Fact]
    public void The_shipped_config_carries_the_menus_and_decision_defaults()
    {
        var (_, options, _, _) = LoadShippedApi();

        using (new AssertionScope())
        {
            options.Agents.Single(a => string.Equals(a.Name, "implementer", StringComparison.Ordinal)).AllowedModels
                .Should().Equal("claude-haiku-5-5", "claude-sonnet-5-5");
            options.Agents.Single(a => string.Equals(a.Name, "reviewer", StringComparison.Ordinal)).AllowedModels
                .Should().Equal("claude-sonnet-5-5", "claude-opus-5-5");
            options.Decisions.ZeroDataRetentionAccount.Should().BeFalse();
            options.Decisions.Model.Should().Be("jev-latest");
            options.Decisions.Timeout.Should().Be(TimeSpan.FromSeconds(5));
            options.Decisions.ModelConfidence.Should().Be(0.6);
            options.Decisions.ToolFamilyProbability.Should().Be(0.5);
            options.Decisions.PinnedLensModels.Should().BeEmpty();
        }
    }

    /// <summary>
    ///     The code defaults, apart from the shipped file that restates them. Red: change any default in <c>DecisionsConfig</c>.
    /// </summary>
    [Fact]
    public void The_code_defaults_match_the_documented_ones()
    {
        var config = new DecisionsConfig();

        using (new AssertionScope())
        {
            config.ZeroDataRetentionAccount.Should().BeFalse();
            config.Model.Should().Be("jev-latest");
            config.Timeout.Should().Be(TimeSpan.FromSeconds(5));
            config.ModelConfidence.Should().Be(0.6);
            config.ToolFamilyProbability.Should().Be(0.5);
            config.PinnedLensModels.Should().BeEmpty();
        }
    }

    /// <summary>Red: make <c>IsEnabled</c> ignore <c>ZeroDataRetentionAccount</c> (return only the key check).</summary>
    [Fact]
    public void Decisions_are_off_without_the_zero_retention_confirmation()
    {
        var config = new DecisionsConfig { ZeroDataRetentionAccount = false };

        config.IsEnabled("sk-or-key").Should().BeFalse();
    }

    /// <summary>Red: make <c>IsEnabled</c> ignore the key (return only <c>ZeroDataRetentionAccount</c>).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Decisions_are_off_without_a_key(string? apiKey)
    {
        var config = new DecisionsConfig { ZeroDataRetentionAccount = true };

        config.IsEnabled(apiKey).Should().BeFalse();
    }

    /// <summary>Red: make <c>IsEnabled</c> always return false.</summary>
    [Fact]
    public void Decisions_are_on_with_the_confirmation_and_a_key()
    {
        new DecisionsConfig { ZeroDataRetentionAccount = true }.IsEnabled("sk-or-key").Should().BeTrue();
    }

    /// <summary>Red: delete the PinnedLensModels check in <c>ValidateDecisionsConfig</c>.</summary>
    [Fact]
    public void A_pinned_lens_model_outside_the_reviewers_allowed_models_is_refused_at_startup()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Decisions.PinnedLensModels["falsifiability"] = "claude-haiku-5-5";

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PinnedLensModels*falsifiability*claude-haiku-5-5*");
    }

    /// <summary>Red: make the check reject every pinned model, or compare against the wrong agent's menu.</summary>
    [Fact]
    public void A_pinned_lens_model_inside_the_reviewers_allowed_models_boots()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Decisions.PinnedLensModels["falsifiability"] = "claude-opus-5-5";

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    /// <summary>
    ///     Red: delete the matching check in <c>ValidateDecisionsConfig</c>, or loosen its bound. Red for the NaN rows:
    ///     write the bound as <c>is &lt; 0 or &gt; 1</c>, which NaN passes. Red for the duplicate-after-trim row: compare
    ///     the entries untrimmed. Red for the whitespace row: test the entry with <c>IsNullOrEmpty</c>.
    /// </summary>
    [Theory]
    [InlineData("blank-model", "*Decisions:Model*blank*")]
    [InlineData("zero-timeout", "*Decisions:Timeout*greater than zero*")]
    [InlineData("negative-timeout", "*Decisions:Timeout*greater than zero*")]
    [InlineData("confidence-above-one", "*Decisions:ModelConfidence*between 0 and 1*")]
    [InlineData("confidence-below-zero", "*Decisions:ModelConfidence*between 0 and 1*")]
    [InlineData("probability-above-one", "*Decisions:ToolFamilyProbability*between 0 and 1*")]
    [InlineData("probability-below-zero", "*Decisions:ToolFamilyProbability*between 0 and 1*")]
    [InlineData("confidence-nan", "*Decisions:ModelConfidence*between 0 and 1*")]
    [InlineData("probability-nan", "*Decisions:ToolFamilyProbability*between 0 and 1*")]
    [InlineData("whitespace-allowed-model", "*AllowedModels*blank entry*")]
    [InlineData("duplicate-after-trim", "*AllowedModels*'claude-sonnet-5-5' more than once*")]
    [InlineData("blank-allowed-model", "*AllowedModels*blank entry*")]
    [InlineData("duplicate-allowed-model", "*AllowedModels*'claude-sonnet-5-5' more than once*")]
    public void An_invalid_decisions_value_is_refused_at_startup(string rule, string expectedMessage)
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        var implementer = options.Agents.Single(a => string.Equals(a.Name, "implementer", StringComparison.Ordinal));
        switch (rule)
        {
            case "blank-model":
                options.Decisions.Model = " ";
                break;
            case "zero-timeout":
                options.Decisions.Timeout = TimeSpan.Zero;
                break;
            case "negative-timeout":
                options.Decisions.Timeout = TimeSpan.FromSeconds(-1);
                break;
            case "confidence-above-one":
                options.Decisions.ModelConfidence = 1.01;
                break;
            case "confidence-below-zero":
                options.Decisions.ModelConfidence = -0.01;
                break;
            case "probability-above-one":
                options.Decisions.ToolFamilyProbability = 1.01;
                break;
            case "probability-below-zero":
                options.Decisions.ToolFamilyProbability = -0.01;
                break;
            case "confidence-nan":
                options.Decisions.ModelConfidence = double.NaN;
                break;
            case "probability-nan":
                options.Decisions.ToolFamilyProbability = double.NaN;
                break;
            case "whitespace-allowed-model":
                implementer.AllowedModels.Add(" \t");
                break;
            case "duplicate-after-trim":
                implementer.AllowedModels.Add(" claude-sonnet-5-5 ");
                break;
            case "blank-allowed-model":
                implementer.AllowedModels.Add("");
                break;
            case "duplicate-allowed-model":
                implementer.AllowedModels.Add("claude-sonnet-5-5");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, "no such rule");
        }

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedMessage);
    }

    /// <summary>The boundary values 0 and 1 are valid. Red: make a bound exclusive.</summary>
    [Fact]
    public void The_probability_bounds_zero_and_one_boot()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Decisions.ModelConfidence = 1;
        options.Decisions.ToolFamilyProbability = 0;

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }

    /// <summary>Red: restore the empty-menu message, or drop the null-reviewer branch.</summary>
    [Fact]
    public void A_pinned_lens_model_without_a_reviewer_agent_says_so()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Agents.Remove(options.Agents.Single(a => string.Equals(a.Name, "reviewer", StringComparison.Ordinal)));
        options.Decisions.PinnedLensModels["falsifiability"] = "claude-opus-5-5";

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no agent named reviewer is configured*");
    }

    /// <summary>
    ///     Configuration whitespace never reaches Thalos: an entry is trimmed before the duplicate and pin checks, and the
    ///     trimmed value is what the envelope carries. Red: drop the trim; the entry keeps its spaces. Red: trim the
    ///     allowed models but not the pinned values; the pin is refused as outside the reviewer's menu.
    /// </summary>
    [Fact]
    public void Allowed_models_and_pinned_lens_models_are_trimmed_at_startup()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        var reviewer = options.Agents.Single(a => string.Equals(a.Name, "reviewer", StringComparison.Ordinal));
        reviewer.AllowedModels[0] = "  claude-sonnet-5-5\t";
        options.Decisions.PinnedLensModels["falsifiability"] = " claude-opus-5-5 ";

        services.AddDaedalusAgents(options, configuration, environment);

        using (new AssertionScope())
        {
            reviewer.AllowedModels.Should().Equal("claude-sonnet-5-5", "claude-opus-5-5");
            options.Decisions.PinnedLensModels["falsifiability"].Should().Be("claude-opus-5-5");
        }
    }

    /// <summary>
    ///     Ruling 2: each agent's menu reaches the Thalos agent definition, so Thalos itself refuses a turn's model outside
    ///     it. Chartered agents carry it on their envelope, configured agents on their definition. Red: drop
    ///     <c>AllowedModels</c> from <c>ToEnvelope</c>; the envelope assertions fail. Red: drop it from <c>ToDefinition</c>;
    ///     the scout assertion fails.
    /// </summary>
    [Fact]
    public void Each_agents_allowed_models_reach_its_thalos_definition()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Agents.Single(a => string.Equals(a.Name, "scout", StringComparison.Ordinal)).AllowedModels.Add("claude-haiku-5-5");

        services.AddDaedalusAgents(options, configuration, environment);
        using var provider = services.BuildServiceProvider();

        var envelopes = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Thalos.Skills.Charters.CharterOptions>>().Value.Envelopes;
        var definitions = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Thalos.ThalosOptions>>().Value.Agents;
        using (new AssertionScope())
        {
            envelopes.Single(e => string.Equals(e.Name, "implementer", StringComparison.Ordinal)).AllowedModels
                .Should().Equal("claude-haiku-5-5", "claude-sonnet-5-5");
            envelopes.Single(e => string.Equals(e.Name, "reviewer", StringComparison.Ordinal)).AllowedModels
                .Should().Equal("claude-sonnet-5-5", "claude-opus-5-5");
            definitions.Single(d => string.Equals(d.Name, "scout", StringComparison.Ordinal)).AllowedModels
                .Should().Equal("claude-haiku-5-5");
        }
    }

    /// <summary>
    ///     Each agent's tool patterns reach its Thalos definition, which is what Thalos checks a decided tool filter against.
    ///     Chartered agents carry them on their envelope, configured agents on their definition. Each agent is given a list
    ///     of its own, so a mapping that mixed them up would fail too.
    ///     Red: set <c>Tools</c> to empty in <c>ToEnvelope</c> (it is required, so it cannot be left out); the implementer and reviewer assertions fail.
    ///     Red: drop <c>Tools</c> from <c>ToDefinition</c>; the scout definition holds the default "*", so its assertion fails.
    /// </summary>
    [Fact]
    public void Each_agents_tools_reach_its_thalos_definition()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        SetTools(options, "implementer", "workspace__*", "roslyn__*");
        SetTools(options, "reviewer", "workspace__*", "daedalus__*", "memory__*");
        SetTools(options, "scout", "workspace__*", "issues__*");

        services.AddDaedalusAgents(options, configuration, environment);
        using var provider = services.BuildServiceProvider();

        var envelopes = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Thalos.Skills.Charters.CharterOptions>>().Value.Envelopes;
        var definitions = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Thalos.ThalosOptions>>().Value.Agents;
        using (new AssertionScope())
        {
            envelopes.Single(e => string.Equals(e.Name, "implementer", StringComparison.Ordinal)).Tools
                .Should().Equal("workspace__*", "roslyn__*");
            envelopes.Single(e => string.Equals(e.Name, "reviewer", StringComparison.Ordinal)).Tools
                .Should().Equal("workspace__*", "daedalus__*", "memory__*");
            definitions.Single(d => string.Equals(d.Name, "scout", StringComparison.Ordinal)).Tools
                .Should().Equal("workspace__*", "issues__*");
        }
    }

    /// <summary>
    ///     A configured agent with no tool patterns gets every tool, the one default <c>ToDefinition</c> applies.
    ///     Red: drop the <c>["*"]</c> default from <c>ToDefinition</c>; the definition's list is empty.
    /// </summary>
    [Fact]
    public void A_configured_agent_with_no_tools_gets_every_tool_on_its_thalos_definition()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        SetTools(options, "scout");

        services.AddDaedalusAgents(options, configuration, environment);
        using var provider = services.BuildServiceProvider();

        var definitions = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Thalos.ThalosOptions>>().Value.Agents;
        definitions.Single(d => string.Equals(d.Name, "scout", StringComparison.Ordinal)).Tools.Should().Equal("*");
    }

    private static void SetTools(DaedalusAgentsOptions options, string agent, params string[] patterns)
    {
        var tools = options.Agents.Single(a => string.Equals(a.Name, agent, StringComparison.Ordinal)).Tools;
        tools.Clear();
        foreach (var pattern in patterns)
        {
            tools.Add(pattern);
        }
    }

    /// <summary>
    ///     A pin applies to whichever agent runs the review node, so with the squad off it is checked against the fallback
    ///     agent, the Daedalus Architect, whose menu is empty and whose default is <c>Thalos:Anthropic:DefaultModel</c>. Red:
    ///     check against <c>reviewer</c> whatever the squad setting; the refused row boots. Red: leave the default model out
    ///     of the menu; the default row is refused.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5-5", false)]
    [InlineData("claude-sonnet-5", true)]
    public void With_the_squad_off_a_pin_is_checked_against_the_fallback_agent(string model, bool boots)
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Squad.Enabled = false;
        options.Decisions.PinnedLensModels["falsifiability"] = model;

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        if (boots)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<InvalidOperationException>().WithMessage($"*falsifiability*{model}*Daedalus Architect*squad*");
        }
    }

    /// <summary>
    ///     A pin key must be a lens name exactly, as the lens runner looks it up ordinally: a typo or another case would be
    ///     accepted and never apply. Red: drop the key check; every row boots. Red: compare the key ignoring case; the
    ///     capitalised row boots.
    /// </summary>
    [Theory]
    [InlineData("security")]
    [InlineData("Falsifiability")]
    [InlineData("falsifiabilty")]
    public void A_pin_key_that_is_not_a_lens_name_is_refused_at_startup(string key)
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Decisions.PinnedLensModels[key] = "claude-opus-5-5";

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*PinnedLensModels:{key}*not a review lens*correctness, falsifiability, mechanism*");
    }
}
