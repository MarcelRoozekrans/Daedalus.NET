namespace Daedalus.Agents.Decisions;

/// <summary>
///     Pure mapping from a Minos answer onto a <see cref="NodeSelection"/>. No I/O, no client call. Routine takes the first
///     allowed model, Hard the last and Moderate the middle one; on a two-model menu Moderate and Hard both pick the
///     stronger model, since quality comes first.
/// </summary>
public static class NodeSelectionMapper
{
    /// <summary>Fallback reason when the demand answer is below <see cref="DecisionsConfig.ModelConfidence"/>.</summary>
    public const string LowConfidence = "low-confidence";

    /// <summary>Fallback reason when the agent offers no allowed models, so there is nothing to choose between.</summary>
    public const string NoAllowedModels = "no-allowed-models";

    /// <summary>The level name of a selection that chose no demand level, so the default model applies.</summary>
    public const string DefaultLevel = "default";

    private static readonly string[] NeverRemovable = ["workspace", "daedalus"];

    /// <summary>The selection when nothing was decided: the default model, the full tool list.</summary>
    public static NodeSelection Default(RoleMenu menu, string fallback) =>
        new(menu.DefaultModel, null, DefaultLevel, CountFamilies(menu.Tools), fallback, Confidence: null);

    /// <summary>Maps an answered question set onto a model and, when <paramref name="narrowTools"/>, a narrowed tool filter.</summary>
    public static NodeSelection Map(NodeQuestions answers, RoleMenu menu, DecisionsConfig config, bool narrowTools)
    {
        var (model, level, fallback) = PickModel(answers, menu, config);
        var (filter, kept) = PickTools(answers, menu.Tools, config.ToolFamilyProbability, narrowTools);
        return new NodeSelection(model, filter, level, kept, fallback, answers.Demand.Confidence) { Answers = AnswersOf(answers) };
    }

    /// <summary>The answers as recorded: the demand level and its confidence, and each family's probability, in question order.</summary>
    public static NodeAnswers AnswersOf(NodeQuestions answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var families = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["roslyn"] = answers.NeedsRoslyn.Probability,
            ["sandbox"] = answers.NeedsSandbox.Probability,
            ["memory"] = answers.NeedsMemory.Probability,
            ["skills"] = answers.NeedsSkills.Probability,
            ["context7"] = answers.NeedsContext7.Probability,
            ["issues"] = answers.NeedsIssues.Probability,
        };
        return new NodeAnswers(NodeQuestions.Version, answers.Demand.Value.ToString(), answers.Demand.Confidence, families);
    }

    private static (string Model, string Level, string? Fallback) PickModel(NodeQuestions answers, RoleMenu menu, DecisionsConfig config)
    {
        var allowed = menu.AllowedModels;
        if (allowed.Count == 0)
        {
            return (menu.DefaultModel, DefaultLevel, NoAllowedModels);
        }

        var demand = answers.Demand;
        if (demand.Confidence < config.ModelConfidence)
        {
            return (menu.DefaultModel, DefaultLevel, LowConfidence);
        }

        var model = demand.Value switch
        {
            Demand.Routine => allowed[0],
            Demand.Hard => allowed[^1],
            _ => allowed[allowed.Count / 2],
        };
        return (model, demand.Value.ToString(), null);
    }

    private static (IReadOnlyList<string>? Filter, int Kept) PickTools(
        NodeQuestions answers, IReadOnlyList<string> tools, double threshold, bool narrowTools)
    {
        if (!narrowTools)
        {
            return (null, CountFamilies(tools));
        }

        var kept = tools.Where(pattern => IsKept(Family(pattern), answers, threshold)).ToList();

        // Nothing removed means no filter. Nothing kept means no filter either: Thalos refuses an empty one, so a role
        // that holds only removable families keeps its full list rather than failing the node.
        return kept.Count == tools.Count || kept.Count == 0 ? (null, CountFamilies(tools)) : (kept, CountFamilies(kept));
    }

    private static bool IsKept(string family, NodeQuestions answers, double threshold)
    {
        if (NeverRemovable.Contains(family, StringComparer.Ordinal))
        {
            return true;
        }

        return family switch
        {
            "roslyn" => answers.NeedsRoslyn.Probability >= threshold,
            "sandbox" => answers.NeedsSandbox.Probability >= threshold,
            "memory" => answers.NeedsMemory.Probability >= threshold,
            "skills" => answers.NeedsSkills.Probability >= threshold,
            "context7" => answers.NeedsContext7.Probability >= threshold,
            "issues" => answers.NeedsIssues.Probability >= threshold,
            _ => true, // a family the question set does not cover is never removed
        };
    }

    private static string Family(string pattern)
    {
        var at = pattern.IndexOf("__", StringComparison.Ordinal);
        return at < 0 ? pattern : pattern[..at];
    }

    /// <summary>How many tool families <paramref name="patterns"/> name; a family is the part before <c>__</c>.</summary>
    internal static int CountFamilies(IEnumerable<string> patterns) =>
        patterns.Select(Family).Distinct(StringComparer.Ordinal).Count();
}
