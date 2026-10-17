using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Configuration;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Phase 2.15: guards the shipped <c>Thalos:Workflow:TurnBudgets</c> section of both hosts' <c>appsettings.json</c>.
///     Reads the real files, linked as <c>Daedalus.Api.appsettings.json</c> and <c>Daedalus.Cli.appsettings.json</c>, the
///     way <see cref="CostAnalyticsPricingDriftTests"/> and <see cref="ScheduledRunsConfigurationDriftTests"/> do, so a
///     shipped ceiling that loses a role, falls below what that role was measured to spend, or drifts between the hosts
///     fails the build rather than a run.
/// </summary>
public sealed class TurnBudgetDriftTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private const string CliAppSettingsFileName = "Daedalus.Cli.appsettings.json";

    /// <summary>
    ///     The largest total-token turn measured per squad role, sampled from the run records on 2026-10-17. A ceiling below
    ///     its role's measured maximum would fail a turn the system has already been seen to need. How the sample is taken and
    ///     when to retake it: <c>docs/development-guide.md</c>, "Token budget". Raise these when a new sample measures more.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> MeasuredMaximum = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["implementer"] = 501_172,
        ["reviewer"] = 392_852,
    };

    private static IConfiguration Load(string fileName) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: false)
            .Build();

    private static DaedalusAgentsOptions Bind(string fileName)
    {
        var options = new DaedalusAgentsOptions();
        Load(fileName).GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    ///     Every agent the shipped manufacture process runs through the squad, resolved with the Api's own squad setting and
    ///     declared in its <c>Thalos:Agents</c>, has a <c>TurnBudgets:Agents</c> entry of its own rather than falling to the
    ///     default by omission.
    ///     <para>Red: delete the reviewer entry from <c>Daedalus.Api/appsettings.json</c>.</para>
    /// </summary>
    [Fact]
    public void Every_squad_role_has_a_ceiling()
    {
        var options = Bind(ApiAppSettingsFileName);
        options.Squad.Enabled.Should().BeTrue("the Api runs manufacturing through the squad, so its roles resolve to their own agents");

        var path = Path.Combine(AppContext.BaseDirectory, "processes", "manufacture.yaml");
        var definition = ProcessLoader.Load(File.ReadAllText(path));
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : null);

        var resolver = new SquadAgentResolver(options.Squad);
        var squadAgents = definition.Value.Nodes.Values
            .Where(n => n.Agent is not null)
            .Select(n => resolver.Resolve(n.Agent!))
            .Where(name => options.Agents.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Not vacuous: an assertion over no roles would prove nothing.
        squadAgents.Should().Contain(["implementer", "reviewer"]);
        options.Workflow.TurnBudgets.Agents.Keys.Should().Contain(squadAgents,
            "each squad role's ceiling is sized from its own measured turns, not left to the default by omission");
    }

    /// <summary>
    ///     No shipped ceiling sits below the largest turn its role has been measured to spend.
    ///     <para>Red: set the reviewer to 300000 in <c>Daedalus.Api/appsettings.json</c>.</para>
    /// </summary>
    [Fact]
    public void No_ceiling_sits_below_its_measured_maximum()
    {
        var budgets = Bind(ApiAppSettingsFileName).Workflow.TurnBudgets;

        foreach (var (role, measured) in MeasuredMaximum)
        {
            budgets.Agents.Should().ContainKey(role);
            budgets.Agents[role].Should().BeGreaterThanOrEqualTo(measured,
                $"the {role} has been measured spending {measured} tokens in one turn, so a lower ceiling fails turns it needs");
        }
    }

    /// <summary>
    ///     Both hosts ship the same turn budgets, the way <see cref="ScheduledRunsConfigurationDriftTests"/> pins
    ///     <c>DetachedRuns</c>: the Cli validates them at boot even with the engine off.
    ///     <para>Red: change the Cli's implementer value in <c>Daedalus.Cli/appsettings.json</c>.</para>
    /// </summary>
    [Fact]
    public void Both_hosts_ship_the_same_turn_budgets()
    {
        static Dictionary<string, string?> Flatten(string fileName) =>
            Load(fileName).GetSection(TurnBudgetsConfig.SectionName)
                .AsEnumerable(makePathsRelative: true)
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var api = Flatten(ApiAppSettingsFileName);
        var cli = Flatten(CliAppSettingsFileName);

        api.Should().ContainKey("Default", "Daedalus.Api/appsettings.json must configure the turn budgets");
        cli.Should().BeEquivalentTo(api, "both hosts must ship identical Thalos:Workflow:TurnBudgets sections");
    }
}
