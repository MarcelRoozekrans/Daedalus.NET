using System.Diagnostics.Metrics;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Daedalus's own manufacture telemetry, on the <c>daedalus.manufacture</c> source and meter. The source carries the
///     generated start and cancel spans; the meter carries the token counter. Tag names shared with Thalos use Thalos's
///     spelling, so one dashboard filter on <c>thalos.run.id</c> finds the start span and every step of the run.
/// </summary>
/// <remarks>
///     <b>No content.</b> Tags carry ids, names, counts and kinds only: never the work intent, a task's title or
///     description, a cancel reason, a PR body or any agent text.
/// </remarks>
public static class ManufactureTelemetry
{
    /// <summary>The source and meter name, under the <c>daedalus.*</c> wildcard the service defaults subscribe to.</summary>
    public const string SourceName = "daedalus.manufacture";

    /// <summary>The start span: the "run start" every dispatch of the run links back to.</summary>
    public const string StartSpanName = "daedalus.manufacture.start";

    /// <summary>The compensating cancel span.</summary>
    public const string CancelSpanName = "daedalus.manufacture.cancel";

    /// <summary>The token counter's name.</summary>
    public const string TokensMetricName = "daedalus.manufacture.tokens";

    /// <summary>The run's id, Thalos's own tag name, aliased so the two cannot drift.</summary>
    public const string RunIdTag = WorkflowTelemetry.RunIdTag;

    /// <summary>The allow-listed repository's name.</summary>
    public const string RepositoryTag = "daedalus.repository";

    /// <summary>The board task a run was started for, set on the request span.</summary>
    public const string TaskIdTag = "daedalus.task.id";

    /// <summary>Whether a compensating cancel cancelled its run.</summary>
    public const string CancelledTag = "daedalus.cancelled";

    /// <summary>The node that spent the tokens, Thalos's own tag name, aliased so the two cannot drift.</summary>
    public const string NodeTag = WorkflowTelemetry.NodeTag;

    /// <summary>The model that spent them, the OpenTelemetry GenAI attribute name.</summary>
    public const string ModelTag = "gen_ai.request.model";

    /// <summary>Which kind of token: <c>input</c>, <c>output</c>, <c>cache_read</c> or <c>cache_write</c>.</summary>
    public const string TokenKindTag = "daedalus.token.kind";

    internal static readonly Meter Meter = new(SourceName);

    internal static readonly Counter<long> Tokens =
        Meter.CreateCounter<long>(TokensMetricName, unit: "{token}", description: "Tokens a completed manufacture node spent");
}
