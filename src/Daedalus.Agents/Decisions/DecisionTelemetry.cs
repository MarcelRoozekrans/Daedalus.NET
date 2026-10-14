using System.Diagnostics;
using System.Diagnostics.Metrics;
using Thalos.Workflow;

namespace Daedalus.Agents.Decisions;

/// <summary>
///     Telemetry for the per-node decision, on the <c>daedalus.decisions</c> source and meter.
/// </summary>
/// <remarks>
///     <b>No content.</b> Tags carry the node, role, level, a family count, the chosen model id, the fallback
///     reason and, when Minos answered, the demand answer's confidence: never task text, never a tool name and never a <c>DecisionError</c> message, which can quote the request.
/// </remarks>
public static class DecisionTelemetry
{
    /// <summary>The source and meter name, under the <c>daedalus.*</c> wildcard the service defaults subscribe to.</summary>
    public const string SourceName = "daedalus.decisions";

    /// <summary>The span around one node's decision.</summary>
    public const string SpanName = "daedalus.node_selection";

    /// <summary>The counter of decisions, tagged by fallback.</summary>
    public const string CounterName = "daedalus.decisions";

    /// <summary>The node, Thalos's own tag name, aliased so the two cannot drift.</summary>
    public const string NodeTag = WorkflowTelemetry.NodeTag;

    /// <summary>The agent role the node runs as.</summary>
    public const string RoleTag = "daedalus.role";

    /// <summary>The chosen demand level name.</summary>
    public const string LevelTag = "daedalus.decision.level";

    /// <summary>How many tool families remain.</summary>
    public const string KeptFamiliesTag = "daedalus.decision.kept_families";

    /// <summary>The confidence of the demand answer; set only when Minos answered, absent for off, timeout and error fallbacks.</summary>
    public const string ConfidenceTag = "daedalus.decision.confidence";

    /// <summary>Why the default applies; absent when the decision was taken.</summary>
    public const string FallbackTag = "daedalus.decision.fallback";

    /// <summary>The chosen model, the OpenTelemetry GenAI attribute name.</summary>
    public const string ModelTag = "gen_ai.request.model";

    internal static readonly ActivitySource Source = new(SourceName);

    internal static readonly Meter Meter = new(SourceName);

    internal static readonly Counter<long> Decisions =
        Meter.CreateCounter<long>(CounterName, unit: "{decision}", description: "Node decisions, tagged by fallback reason");
}
