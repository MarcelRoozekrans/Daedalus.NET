using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Minos;

namespace Daedalus.Agents.Decisions;

/// <summary>What a decision is about: the node, its role, the work and the choices on offer.</summary>
/// <param name="Role">The agent role the node runs as.</param>
/// <param name="Node">The node name, such as <c>implement</c>.</param>
/// <param name="WorkIntent">The run's work intent; cut at <see cref="NodeSelectionDecider.MaxIntentCharacters"/> before it is sent.</param>
/// <param name="Visit">Which visit of the node this is, starting at 1.</param>
/// <param name="Menu">The role's default model, allowed models and tools.</param>
/// <param name="NarrowTools">Whether the tool list may be narrowed; <see langword="false"/> after a rejected review.</param>
public sealed record NodeSelectionContext(string Role, string Node, string WorkIntent, int Visit, RoleMenu Menu, bool NarrowTools);

/// <summary>
///     Asks Minos once for a node's model and tool families, within <see cref="DecisionsConfig.Timeout"/>. Never fails:
///     no client, a slow or failed call, or a low-confidence answer all give <see cref="NodeSelectionMapper.Default"/>
///     with the reason. Only the caller's own cancellation throws.
/// </summary>
public sealed partial class NodeSelectionDecider(IDecisionClient? client, DecisionsConfig config, ILogger<NodeSelectionDecider> logger)
{
    /// <summary>Fallback reason when decisions are off, so there is no client.</summary>
    public const string Off = "off";

    /// <summary>Fallback reason when the decision outran <see cref="DecisionsConfig.Timeout"/>.</summary>
    public const string Timeout = "timeout";

    /// <summary>Fallback reason when the client threw, such as a disposed client or a provider that answered other questions.</summary>
    public const string ClientFault = "error:Client";

    /// <summary>The most characters of work intent sent to the decision.</summary>
    public const int MaxIntentCharacters = 4_000;

    /// <summary>Decides the node's model and tools, or returns the defaults with the reason.</summary>
    public async ValueTask<NodeSelection> DecideAsync(NodeSelectionContext context, CancellationToken ct)
    {
        using var activity = DecisionTelemetry.Source.StartActivity(DecisionTelemetry.SpanName);
        var selection = await DecideCoreAsync(context, ct);
        Record(activity, context, selection);
        return selection;
    }

    private async ValueTask<NodeSelection> DecideCoreAsync(NodeSelectionContext context, CancellationToken ct)
    {
        if (client is null)
        {
            return NodeSelectionMapper.Default(context.Menu, Off);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(config.Timeout);
        try
        {
            var result = await client.EvaluateAsync<NodeQuestions>(StateOf(context), budget.Token);
            if (result.IsFailure)
            {
                var kind = result.Error.Kind;
                LogDecisionFailed(context.Node, kind);
                return NodeSelectionMapper.Default(context.Menu, $"error:{kind}");
            }

            return NodeSelectionMapper.Map(result.Value, context.Menu, config, context.NarrowTools);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogDecisionTimedOut(context.Node);
            return NodeSelectionMapper.Default(context.Menu, Timeout);
        }
        catch (InvalidOperationException ex)
        {
            LogClientFaulted(context.Node, ex.GetType().Name);
            return NodeSelectionMapper.Default(context.Menu, ClientFault);
        }
    }

    private static string StateOf(NodeSelectionContext context)
    {
        var intent = context.WorkIntent;
        if (intent.Length > MaxIntentCharacters)
        {
            var cut = char.IsHighSurrogate(intent[MaxIntentCharacters - 1]) ? MaxIntentCharacters - 1 : MaxIntentCharacters;
            intent = intent[..cut];
        }

        return JsonSerializer.Serialize(new DecisionState(context.Role, context.Node, context.Visit, intent));
    }

    private sealed record DecisionState(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("node")] string Node,
        [property: JsonPropertyName("visit")] int Visit,
        [property: JsonPropertyName("work_intent")] string WorkIntent);

    private static void Record(Activity? activity, NodeSelectionContext context, NodeSelection selection)
    {
        activity?.SetTag(DecisionTelemetry.NodeTag, context.Node);
        activity?.SetTag(DecisionTelemetry.RoleTag, context.Role);
        activity?.SetTag(DecisionTelemetry.LevelTag, selection.Level);
        activity?.SetTag(DecisionTelemetry.KeptFamiliesTag, selection.KeptFamilies);
        activity?.SetTag(DecisionTelemetry.ModelTag, selection.Model);
        if (selection.Confidence is { } answered)
        {
            activity?.SetTag(DecisionTelemetry.ConfidenceTag, answered);
        }

        if (selection.Fallback is not null)
        {
            activity?.SetTag(DecisionTelemetry.FallbackTag, selection.Fallback);
        }

        DecisionTelemetry.Decisions.Add(1, new KeyValuePair<string, object?>(DecisionTelemetry.FallbackTag, selection.Fallback ?? "none"));
    }

    [LoggerMessage(EventId = 1860, Level = LogLevel.Warning, Message = "Decision for node {Node} failed with {Kind}; the node keeps its default model and tools.")]
    private partial void LogDecisionFailed(string node, DecisionErrorKind kind);

    [LoggerMessage(EventId = 1861, Level = LogLevel.Warning, Message = "Decision for node {Node} timed out; the node keeps its default model and tools.")]
    private partial void LogDecisionTimedOut(string node);

    [LoggerMessage(EventId = 1862, Level = LogLevel.Warning, Message = "Decision for node {Node} failed in the client with {ExceptionType}; the node keeps its default model and tools.")]
    private partial void LogClientFaulted(string node, string exceptionType);
}
