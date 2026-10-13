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
        options.Decisions.PinnedLensModels["security"] = "claude-haiku-5-5";

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PinnedLensModels*security*claude-haiku-5-5*");
    }

    /// <summary>Red: make the check reject every pinned model, or compare against the wrong agent's menu.</summary>
    [Fact]
    public void A_pinned_lens_model_inside_the_reviewers_allowed_models_boots()
    {
        var (services, options, configuration, environment) = LoadShippedApi();
        options.Decisions.PinnedLensModels["security"] = "claude-opus-5-5";

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().NotThrow();
    }
}
