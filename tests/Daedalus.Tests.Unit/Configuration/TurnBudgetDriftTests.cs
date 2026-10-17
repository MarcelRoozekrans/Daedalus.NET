using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Configuration;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Phase 2.15: guards the shipped <c>Thalos:Workflow:TurnBudgets</c> section of both hosts' <c>appsettings.json</c>.
///     Reads the real files, linked as <c>Daedalus.Api.appsettings.json</c> and <c>Daedalus.Cli.appsettings.json</c>, the
///     way <see cref="CostAnalyticsPricingDriftTests"/> and <see cref="ScheduledRunsConfigurationDriftTests"/> do, so a
///     shipped ceiling that loses a role, moves away from what that role was measured to spend, or drifts between the
///     hosts fails the build rather than a run.
/// </summary>
public sealed class TurnBudgetDriftTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private const string CliAppSettingsFileName = "Daedalus.Cli.appsettings.json";

    /// <summary>
    ///     The largest total-token turn measured per squad role, over 87 completed turns: 74 on <c>daedalus-sandbox</c> on
    ///     2026-10-14/15, and 13 on <c>MarcelRoozekrans/Daedalus.NET</c> on 2026-10-17. Every shipped ceiling is the sizing
    ///     rule applied to these, so a ceiling cannot move without a new sample. How the sample is taken and when to retake
    ///     it: <c>docs/development-guide.md</c>, "Token budget". Change these, and the ceilings with them, when a new sample
    ///     measures more.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> MeasuredMaximum = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["implementer"] = 1_918_692,
        ["reviewer"] = 1_917_540,
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
    ///     Each shipped <c>Agents</c> entry is the sizing rule applied to its role's measured maximum: 1.5 x the maximum,
    ///     rounded up to the next 25,000. <c>Default</c> is the largest entry. The rule's 2 x p90 term is left out: over
    ///     the same sample it is well below 1.5 x max for both roles, as the development guide's table shows.
    ///     <para>Red: set the reviewer to 3000000 in <c>Daedalus.Api/appsettings.json</c>; the reviewer assertion fails.</para>
    ///     <para>Red: set <c>Default</c> to 3000000; the default assertion fails.</para>
    ///     <para>Red: add a <c>scout</c> entry; the key assertion fails.</para>
    /// </summary>
    [Fact]
    public void Every_ceiling_is_the_sizing_rule_applied_to_its_measured_maximum()
    {
        var budgets = Bind(ApiAppSettingsFileName).Workflow.TurnBudgets;

        budgets.Agents.Keys.Should().BeEquivalentTo(MeasuredMaximum.Keys, "every ceiling is sized from its own measured turns");
        foreach (var (role, measured) in MeasuredMaximum)
        {
            budgets.Agents[role].Should().Be(
                Sized(measured), $"the {role}'s ceiling is 1.5 x its measured maximum of {measured}, rounded up");
        }

        budgets.Default.Should().Be(MeasuredMaximum.Values.Max(Sized), "the default is the largest role ceiling");
    }

    /// <summary>1.5 x <paramref name="measured"/>, rounded up to the next 25,000.</summary>
    private static int Sized(int measured)
    {
        const long Step = 25_000;
        var scaled = (long)Math.Ceiling(measured * 1.5);
        return (int)((scaled + Step - 1) / Step * Step);
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
