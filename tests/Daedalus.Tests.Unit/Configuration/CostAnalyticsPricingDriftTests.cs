using Daedalus.Agents;
using Daedalus.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Guards <c>CostAnalyticsService.EstimateCostAsync</c> against pricing an unpriced model as another model's
///     rate. Loads the real <c>Daedalus.Api/appsettings.json</c> (linked as <c>Daedalus.Api.appsettings.json</c> —
///     see <see cref="ApiThalosConfigurationTests"/>'s remarks) rather than a constructed configuration, so the test
///     goes red the moment someone adds an agent on a model, or changes <c>Thalos:Anthropic:DefaultModel</c>, that
///     has no <c>ModelPricing:Models</c> entry.
/// </summary>
public sealed class CostAnalyticsPricingDriftTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private static IConfiguration LoadApiConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ApiAppSettingsFileName, optional: false)
            .Build();

    private static ModelPricingConfiguration RealPricing()
    {
        var pricing = new ModelPricingConfiguration();
        LoadApiConfiguration().GetSection(ModelPricingConfiguration.SectionName).Bind(pricing);
        return pricing;
    }

    private static string RealAppsettingsDefaultModel() =>
        LoadApiConfiguration()["Thalos:Anthropic:DefaultModel"]
        ?? throw new InvalidOperationException(
            "Thalos:Anthropic:DefaultModel is not set in Daedalus.Api/appsettings.json.");

    /// <summary>
    ///     Effective model id per agent declared under <c>Thalos:Agents</c> — <see cref="AgentConfig.Model"/>
    ///     resolved against <c>Thalos:Anthropic:DefaultModel</c> for agents that omit it, exactly as the runtime
    ///     resolves which model a call actually goes to. A raw <c>null</c> is never returned here: leaving nulls in
    ///     the sequence would let an agent with no <c>Model</c> silently skip the pricing check entirely, rather
    ///     than being checked against the default model it actually inherits.
    /// </summary>
    private static IEnumerable<string> RealAppsettingsAgentModels()
    {
        var configuration = LoadApiConfiguration();
        var defaultModel = RealAppsettingsDefaultModel();

        var agentsOptions = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(agentsOptions);

        return agentsOptions.Agents.Select(a => a.Model ?? defaultModel);
    }

    [Fact]
    public void Every_model_configured_on_an_agent_has_a_price()
    {
        var pricing = RealPricing();
        var configured = RealAppsettingsAgentModels().Append(RealAppsettingsDefaultModel()).ToList();

        // Falsifiability guard against a vacuous pass: an OnlyContain over an empty sequence is
        // trivially true and would prove nothing. This codebase has shipped that shape before.
        configured.Should().NotBeEmpty("the real appsettings.json must declare at least the default model");

        configured.Should().OnlyContain(m => pricing.Models.ContainsKey(m),
            "otherwise adding an agent on a new model silently reports its cost as another model's");
    }
}
