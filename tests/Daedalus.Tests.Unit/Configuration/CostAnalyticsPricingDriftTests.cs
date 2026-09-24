using Daedalus.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using static Daedalus.Tests.Unit.Configuration.ChartersTestSupport;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Guards the shipped model configuration against the one gap the pricing fix left open: a model that is
///     <em>configured</em> but not <em>priced</em>. Loads the real <c>Daedalus.Api/appsettings.json</c> (linked
///     as <c>Daedalus.Api.appsettings.json</c> — see <see cref="ApiThalosConfigurationTests"/>'s remarks) rather
///     than a constructed configuration, so it goes red the moment someone adds an agent on a model, or changes
///     <c>Thalos:Anthropic:DefaultModel</c>, that has no <c>ModelPricing:Models</c> entry.
/// </summary>
/// <remarks>
///     <b>What this guards is not what it was written to guard.</b> The original summary said it guarded
///     <c>CostAnalyticsService.EstimateCostAsync</c> "against pricing an unpriced model as another model's
///     rate" — the silent first-dictionary-entry substitution — and commit <c>bffa4c9</c>, in this same phase,
///     deleted that substitution. An unpriced model now fails loudly: <c>EstimateCostAsync</c> returns a
///     <c>Failure</c> naming the model, and <c>PriceGroups</c> folds its tokens into <c>ExcludedCostDto</c>
///     instead of pricing them. So mispricing is no longer reachable, and the test's subject moved with the
///     fix: the remaining risk is that a model this host actually calls is absent from the pricing table, in
///     which case its spend is silently <em>excluded</em> from every total rather than silently relabelled.
///     Quietly missing from a cost figure is a smaller defect than quietly wrong, and this is what keeps it
///     from happening at all.
/// </remarks>
/// <remarks>
///     <b>Fix round 1: reads the composed, synced agent catalog, not raw configuration.</b> Since phase 2.4
///     task B2, <c>implementer</c>/<c>reviewer</c> carry no <c>Model</c> key in <c>Thalos:Agents</c> at all —
///     the reviewer's <c>claude-opus-5</c> comes from <c>roles/reviewer.md</c> (a charter) instead. Resolving
///     <c>a.Model ?? defaultModel</c> off <c>DaedalusAgentsOptions</c> directly, as this test used to, therefore
///     saw <see langword="null"/> for the reviewer and silently substituted <c>defaultModel</c> (already
///     priced) — the guard stopped observing <c>claude-opus-5</c> at all and would not have gone red had its
///     pricing entry been deleted. Building through <see cref="ChartersTestSupport.BuildWithApiConfigurationAndSyncedChartersAsync"/>
///     and reading <see cref="IAgentCatalog"/> instead resolves every agent's <em>effective</em> model exactly
///     as the runtime does, chartered or not.
/// </remarks>
public sealed class CostAnalyticsPricingDriftTests
{
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

    [Fact]
    public async Task Every_model_configured_on_an_agent_has_a_price()
    {
        var pricing = RealPricing();
        var defaultModel = RealAppsettingsDefaultModel();

        await using var sp = await BuildWithApiConfigurationAndSyncedChartersAsync();

        // Effective model per agent in the real, composed catalog - AgentDefinition.Model resolved against
        // Thalos:Anthropic:DefaultModel for whichever agent omits it, exactly as the runtime resolves which
        // model a call actually goes to. A raw null is never appended here: if this used a.Model unresolved
        // instead, an agent with no Model would put a null into the sequence and
        // pricing.Models.ContainsKey(null) throws - the test would error rather than check the model that
        // agent actually calls.
        var configured = sp.GetRequiredService<IAgentCatalog>().Agents
            .Select(a => a.Model ?? defaultModel)
            .Append(defaultModel)
            .ToList();

        // Falsifiability guard against a vacuous pass: an OnlyContain over an empty sequence is
        // trivially true and would prove nothing. This codebase has shipped that shape before.
        configured.Should().NotBeEmpty("the real appsettings.json must declare at least the default model");

        configured.Should().OnlyContain(m => pricing.Models.ContainsKey(m),
            "otherwise adding an agent on a new model leaves its spend folded into ExcludedCostDto and missing " +
            "from every total, which is quiet in a different way than the relabelling bffa4c9 removed");
    }
}
