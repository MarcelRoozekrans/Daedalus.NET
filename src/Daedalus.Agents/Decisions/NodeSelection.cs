namespace Daedalus.Agents.Decisions;

/// <summary>What a node runs with: the model, the tool patterns to keep, and why.</summary>
/// <param name="Model">The model id for the node's turns.</param>
/// <param name="ToolFilter">The kept tool patterns in configured order; <see langword="null"/> → the full tool list.</param>
/// <param name="Level">The chosen demand level name, or the default's name when nothing was decided.</param>
/// <param name="KeptFamilies">How many tool families remain (telemetry; never tool names).</param>
/// <param name="Fallback">Why the default model applies, or <see langword="null"/> when the decision was taken.</param>
/// <param name="Confidence">
///     The demand answer's confidence when Minos answered, whether or not the model choice was taken; <see langword="null"/>
///     when nothing was asked or the decision failed.
/// </param>
public sealed record NodeSelection(
    string Model, IReadOnlyList<string>? ToolFilter, string Level, int KeptFamilies, string? Fallback, double? Confidence)
{
    /// <summary>
    ///     What Minos answered, when it answered, whether or not the answer was taken; <see langword="null"/> when nothing was
    ///     asked or the decision failed. Recorded so the thresholds can be tuned from what the questions really scored.
    /// </summary>
    public NodeAnswers? Answers { get; init; }
}

/// <summary>Minos's answers to one <see cref="NodeQuestions"/> set: never task text, only the levels and numbers it gave.</summary>
/// <param name="QuestionSet">The <see cref="NodeQuestions.Version"/> the answers belong to.</param>
/// <param name="Demand">The demand level answered, such as <c>Hard</c>.</param>
/// <param name="DemandConfidence">The confidence of the demand answer.</param>
/// <param name="Families">Each tool family asked about, such as <c>roslyn</c>, to the probability it is needed, in question order.</param>
public sealed record NodeAnswers(int QuestionSet, string Demand, double DemandConfidence, IReadOnlyDictionary<string, double> Families);

/// <summary>The choices an agent offers a decision: its default model, the models it may narrow to (cheapest first), and its tool patterns.</summary>
/// <param name="DefaultModel">The agent's own model.</param>
/// <param name="AllowedModels">Models a decision may pick, cheapest first; empty → no choice.</param>
/// <param name="Tools">The agent's tool patterns in configured order, such as <c>roslyn__*</c>.</param>
public sealed record RoleMenu(string DefaultModel, IReadOnlyList<string> AllowedModels, IReadOnlyList<string> Tools);
