using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Agents.Workflow;

namespace Daedalus.Agents.Decisions;

/// <summary>
///     The state <see cref="NodeSelectionDecider"/> sends Minos for one decision: <c>{ role, node, visit, work_intent }</c>,
///     serialised here, through <see cref="DaedalusRecordsJsonContext"/>, so its bytes can be pinned apart from the Minos
///     call.
/// </summary>
internal static class DecisionStateJson
{
    /// <summary>The state JSON; <paramref name="workIntent"/> is already cut to the length the decider sends.</summary>
    internal static string Serialize(string role, string node, int visit, string workIntent) =>
        JsonSerializer.Serialize(new DecisionState(role, node, visit, workIntent), DaedalusRecordsJsonContext.Default.DecisionState);

    /// <summary>The state, serialised through <see cref="DaedalusRecordsJsonContext"/>.</summary>
    internal sealed record DecisionState(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("node")] string Node,
        [property: JsonPropertyName("visit")] int Visit,
        [property: JsonPropertyName("work_intent")] string WorkIntent);
}
