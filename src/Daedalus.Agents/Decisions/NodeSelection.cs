namespace Daedalus.Agents.Decisions;

/// <summary>What a node runs with: the model, the tool patterns to keep, and why.</summary>
/// <param name="Model">The model id for the node's turns.</param>
/// <param name="ToolFilter">The kept tool patterns in configured order; <see langword="null"/> → the full tool list.</param>
/// <param name="Level">The chosen demand level name, or the default's name when nothing was decided.</param>
/// <param name="KeptFamilies">How many tool families remain (telemetry; never tool names).</param>
/// <param name="Fallback">Why the default model applies, or <see langword="null"/> when the decision was taken.</param>
public sealed record NodeSelection(string Model, IReadOnlyList<string>? ToolFilter, string Level, int KeptFamilies, string? Fallback);

/// <summary>The choices an agent offers a decision: its default model, the models it may narrow to (cheapest first), and its tool patterns.</summary>
/// <param name="DefaultModel">The agent's own model.</param>
/// <param name="AllowedModels">Models a decision may pick, cheapest first; empty → no choice.</param>
/// <param name="Tools">The agent's tool patterns in configured order, such as <c>roslyn__*</c>.</param>
public sealed record RoleMenu(string DefaultModel, IReadOnlyList<string> AllowedModels, IReadOnlyList<string> Tools);
