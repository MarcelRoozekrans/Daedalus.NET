using Daedalus.Agents;
using Daedalus.Agents.Decisions;
using Minos;

namespace Daedalus.Tests.Unit.Decisions;

public class NodeSelectionMapperTests
{
    private static readonly string[] Tools =
        ["workspace__*", "daedalus__*", "roslyn__*", "sandbox__*", "memory__*", "skills__*", "context7__*", "issues__*"];

    private static readonly RoleMenu Menu = new("default-model", ["cheap", "middle", "strong"], Tools);

    private static async Task<NodeQuestions> AnswerAsync(
        Demand demand, double confidence = 0.9, double roslyn = 0.9, double sandbox = 0.9, double memory = 0.9,
        double skills = 0.9, double context7 = 0.9, double issues = 0.9)
    {
        using var client = FakeDecisionClient.Answering(demand, confidence, roslyn, sandbox, memory, skills, context7, issues);
        var result = await client.EvaluateAsync<NodeQuestions>("state", CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    /// <summary>Red: map Routine to AllowedModels[1] and this fails.</summary>
    [Fact]
    public async Task Routine_with_confidence_picks_the_cheapest_allowed_model()
    {
        var selection = NodeSelectionMapper.Map(await AnswerAsync(Demand.Routine), Menu, new DecisionsConfig(), true);

        selection.Model.Should().Be("cheap");
        selection.Level.Should().Be("Routine");
        selection.Fallback.Should().BeNull();
    }

    /// <summary>Red: map Hard to allowed[1], or change Moderate to take the last entry, and the middle/last asserts fail.</summary>
    [Fact]
    public async Task Hard_picks_the_most_capable_allowed_model()
    {
        var config = new DecisionsConfig();

        NodeSelectionMapper.Map(await AnswerAsync(Demand.Hard), Menu, config, true).Model.Should().Be("strong");
        NodeSelectionMapper.Map(await AnswerAsync(Demand.Moderate), Menu, config, true).Model.Should().Be("middle");
    }

    /// <summary>Red: drop the confidence check in PickModel, or flip its comparison, and this fails.</summary>
    [Fact]
    public async Task Low_confidence_keeps_the_default_model()
    {
        var selection = NodeSelectionMapper.Map(await AnswerAsync(Demand.Hard, confidence: 0.59), Menu, new DecisionsConfig(), true);

        selection.Model.Should().Be("default-model");
        selection.Fallback.Should().Be("low-confidence");
    }

    /// <summary>Red: remove the empty-list guard and allowed[^1] throws; an unguarded fallback label fails the Fallback assert.</summary>
    [Fact]
    public async Task An_empty_menu_keeps_the_default_model()
    {
        var menu = Menu with { AllowedModels = [] };

        var selection = NodeSelectionMapper.Map(await AnswerAsync(Demand.Hard), menu, new DecisionsConfig(), true);

        selection.Model.Should().Be("default-model");
        selection.Fallback.Should().Be("no-allowed-models");
    }

    /// <summary>Red: compare with &gt; instead of &gt;= (the 0.5 case), or reorder the kept list, and this fails.</summary>
    [Fact]
    public async Task A_family_answered_no_is_removed_and_order_is_kept()
    {
        var answers = await AnswerAsync(Demand.Moderate, roslyn: 0.2, memory: 0.49, issues: 0.5);

        var selection = NodeSelectionMapper.Map(answers, Menu, new DecisionsConfig(), true);

        selection.ToolFilter.Should().Equal("workspace__*", "daedalus__*", "sandbox__*", "skills__*", "context7__*", "issues__*");
        selection.KeptFamilies.Should().Be(6);
    }

    /// <summary>Red: drop the never-removable check in IsKept and workspace and daedalus disappear.</summary>
    [Fact]
    public async Task Workspace_and_daedalus_survive_a_no_for_every_family()
    {
        var answers = await AnswerAsync(Demand.Moderate, 0.9, 0, 0, 0, 0, 0, 0);

        var selection = NodeSelectionMapper.Map(answers, Menu, new DecisionsConfig(), true);

        selection.ToolFilter.Should().Equal("workspace__*", "daedalus__*");
        selection.KeptFamilies.Should().Be(2);
    }

    /// <summary>Red: ignore narrowTools in PickTools and the filter is non-null; return an empty list for "nothing removed" and the all-yes case fails.</summary>
    [Fact]
    public async Task No_narrowing_after_a_rejection_gives_a_null_filter()
    {
        var noEverything = await AnswerAsync(Demand.Moderate, 0.9, 0, 0, 0, 0, 0, 0);
        var yesEverything = await AnswerAsync(Demand.Moderate);
        var config = new DecisionsConfig();

        NodeSelectionMapper.Map(noEverything, Menu, config, false).ToolFilter.Should().BeNull();
        NodeSelectionMapper.Map(noEverything, Menu, config, false).KeptFamilies.Should().Be(8);
        NodeSelectionMapper.Map(yesEverything, Menu, config, true).ToolFilter.Should().BeNull();
    }

    /// <summary>Red: change the Default level label or drop the fallback and this fails.</summary>
    [Fact]
    public void Default_is_the_default_model_and_the_full_tool_list()
    {
        var selection = NodeSelectionMapper.Default(Menu, "timeout");

        selection.Should().Be(new NodeSelection("default-model", null, "default", 8, "timeout"));
    }

    /// <summary>Red: change / 2 to (Count - 1) / 2 and Moderate picks the first entry.</summary>
    [Fact]
    public async Task Moderate_on_a_two_model_menu_picks_the_stronger_model()
    {
        var menu = Menu with { AllowedModels = ["cheap", "strong"] };

        NodeSelectionMapper.Map(await AnswerAsync(Demand.Moderate), menu, new DecisionsConfig(), true).Model.Should().Be("strong");
    }

    /// <summary>Red: change _ =&gt; true to false and the unknown family is removed.</summary>
    [Fact]
    public async Task A_family_the_questions_do_not_cover_is_kept()
    {
        var menu = Menu with { Tools = [.. Tools, "jira__*"] };
        var answers = await AnswerAsync(Demand.Moderate, 0.9, 0, 0, 0, 0, 0, 0);

        NodeSelectionMapper.Map(answers, menu, new DecisionsConfig(), true).ToolFilter.Should().Equal("workspace__*", "daedalus__*", "jira__*");
    }

    /// <summary>Red: change &lt; to &lt;= in the confidence check and exactly 0.6 falls back.</summary>
    [Fact]
    public async Task Confidence_exactly_at_the_threshold_is_taken()
    {
        var selection = NodeSelectionMapper.Map(await AnswerAsync(Demand.Hard, confidence: 0.6), Menu, new DecisionsConfig(), true);

        selection.Model.Should().Be("strong");
        selection.Fallback.Should().BeNull();
    }
}
