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

    /// <summary>
    ///     Phase 2.10: a decision may pick any <c>AllowedModels</c> entry, so each needs a price or its spend lands in
    ///     ExcludedCostDto. Reads the bound options, not the catalog: the menu is not in the Thalos envelope until 0.17.
    ///     Red: delete claude-opus-5-5 from ModelPricing:Models, or set its input or output price to 0.
    /// </summary>
    [Fact]
    public void Every_allowed_model_has_a_price()
    {
        var pricing = RealPricing();
        var options = new Daedalus.Agents.DaedalusAgentsOptions();
        LoadApiConfiguration().GetSection(Daedalus.Agents.DaedalusAgentsOptions.SectionName).Bind(options);

        var allowed = options.Agents.SelectMany(a => a.AllowedModels).ToList();

        allowed.Should().NotBeEmpty("the shipped implementer and reviewer declare menus");
        allowed.Should().OnlyContain(m => pricing.Models.ContainsKey(m));
        allowed.Should().OnlyContain(m => pricing.Models[m].InputTokenPricePerMillion > 0 && pricing.Models[m].OutputTokenPricePerMillion > 0,
            "a zero price would count the spend as free");
    }

    /// <summary>
    ///     The three 5.5 prices exactly. Red: change any of the six numbers in appsettings.json, for example Opus input 4.00 to 5.00.
    /// </summary>
    [Theory]
    [InlineData("claude-haiku-5-5", 0.10, 0.50)]
    [InlineData("claude-sonnet-5-5", 2.00, 10.00)]
    [InlineData("claude-opus-5-5", 4.00, 20.00)]
    public void The_5_5_models_carry_their_published_prices(string model, double input, double output)
    {
        var price = RealPricing().Models[model];

        price.InputTokenPricePerMillion.Should().Be((decimal)input);
        price.OutputTokenPricePerMillion.Should().Be((decimal)output);
    }

    /// <summary>
    ///     Issue #360: the shipped table must pass the host's start-up validation, and a cache price left unset derives from the
    ///     input price at Anthropic's 0.1 and 1.25 times, so a model cannot silently price cache reads above plain input.
    ///     Red: set a negative CacheReadTokenPricePerMillion on any model in appsettings.json; validation fails.
    ///     Red: change the 0.1 or 1.25 in <c>ModelPricing</c>; the derivation assertions fail.
    /// </summary>
    [Fact]
    public void The_shipped_prices_pass_validation_and_derive_cache_prices_from_input()
    {
        var pricing = RealPricing();

        new ModelPricingConfigurationValidator().Validate(null, pricing).Succeeded.Should().BeTrue();
        pricing.Models.Should().NotBeEmpty();
        foreach (var price in pricing.Models.Values)
        {
            price.EffectiveCacheReadTokenPricePerMillion.Should().Be((price.CacheReadTokenPricePerMillion ?? price.InputTokenPricePerMillion * 0.1m));
            price.EffectiveCacheReadTokenPricePerMillion.Should().BeLessThan(price.InputTokenPricePerMillion);
            price.EffectiveCacheWriteTokenPricePerMillion.Should().BeGreaterThan(price.InputTokenPricePerMillion);
        }
    }
}
