using System.Diagnostics;
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
///     no client, a slow or failed call, a client that throws, or a low-confidence answer all give
///     <see cref="NodeSelectionMapper.Default"/> with the reason. Only the caller's own cancellation throws.
/// </summary>
/// <remarks>
///     <b>One budget for the whole decision.</b> <see cref="DecisionsConfig.Timeout"/> bounds everything a decision waits
///     on, not only the Minos call: the host's preparation of the menu, such as resolving the agent's tools, the call, and
///     the host's check of the result. When the budget runs out at any of the three, the node runs on its default model and
///     full tool list with the fallback <see cref="Timeout"/>, so a slow tool source cannot stall the node either.
/// </remarks>
public sealed partial class NodeSelectionDecider(IDecisionClient? client, DecisionsConfig config, ILogger<NodeSelectionDecider> logger)
{
    /// <summary>Fallback reason when decisions are off, so there is no client.</summary>
    public const string Off = "off";

    /// <summary>Fallback reason when the decision, its preparation or its check outran <see cref="DecisionsConfig.Timeout"/>.</summary>
    public const string Timeout = "timeout";

    /// <summary>
    ///     Fallback reason when the client threw anything other than the caller's own cancellation: a disposed client, a
    ///     provider that answered other questions, a network fault the client did not turn into a result, or a bug.
    /// </summary>
    public const string ClientFault = "error:Client";

    /// <summary>
    ///     Whether a decision can be taken at all. Without a client every decision is the default with fallback
    ///     <see cref="Off"/>, so a caller may skip work that only a taken decision would use.
    /// </summary>
    public bool HasClient => client is not null;

    /// <summary>The most characters of work intent sent to the decision.</summary>
    public const int MaxIntentCharacters = 4_000;

    /// <summary>Decides the node's model and tools, or returns the defaults with the reason.</summary>
    public ValueTask<NodeSelection> DecideAsync(NodeSelectionContext context, CancellationToken ct) =>
        DecideAsync(context, prepare: null, settle: null, ct);

    /// <summary>
    ///     Decides the node's model and tools, then lets <paramref name="settle"/>, the host's check of the result,
    ///     replace it before the span is tagged and the decision counted, so a fallback the host applies is told and
    ///     counted like the decider's own, once.
    /// </summary>
    public ValueTask<NodeSelection> DecideAsync(
        NodeSelectionContext context, Func<NodeSelection, CancellationToken, ValueTask<NodeSelection>>? settle, CancellationToken ct) =>
        DecideAsync(context, prepare: null, settle, ct);

    /// <summary>
    ///     Decides the node's model and tools within one budget: <paramref name="prepare"/>, the host's work on the context
    ///     such as resolving the menu's tools, then the Minos call, then <paramref name="settle"/>, the host's check. Both
    ///     callbacks are handed the budget's token; when it runs out the selection is the prepared context's default with
    ///     fallback <see cref="Timeout"/>, still carrying Minos's answers when the budget ran out after it answered. Without a client neither callback is given a budget: <paramref name="prepare"/>
    ///     is not called, since only a taken decision would use it, and <paramref name="settle"/> sees the default.
    /// </summary>
    public async ValueTask<NodeSelection> DecideAsync(
        NodeSelectionContext context,
        Func<NodeSelectionContext, CancellationToken, ValueTask<NodeSelectionContext>>? prepare,
        Func<NodeSelection, CancellationToken, ValueTask<NodeSelection>>? settle,
        CancellationToken ct)
    {
        using var activity = DecisionTelemetry.Source.StartActivity(DecisionTelemetry.SpanName);
        NodeSelection selection;
        if (client is null)
        {
            selection = NodeSelectionMapper.Default(context.Menu, Off);
            if (settle is not null)
            {
                selection = await settle(selection, ct);
            }
        }
        else
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(config.Timeout);

            // What Minos answered, once it has: a budget that runs out in the host's check still records the answers.
            NodeAnswers? answered = null;
            try
            {
                if (prepare is not null)
                {
                    context = await prepare(context, budget.Token);
                }

                selection = await AskAsync(client, context, budget.Token);
                answered = selection.Answers;
                if (settle is not null)
                {
                    selection = await settle(selection, budget.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LogDecisionTimedOut(context.Node);
                selection = NodeSelectionMapper.Default(context.Menu, Timeout) with { Answers = answered };
            }
        }

        Record(activity, context, selection);
        return selection;
    }

    /// <summary>
    ///     The Minos call. Every exception the client throws, other than a cancellation, is <see cref="ClientFault"/>, logged
    ///     by its type name only, since a message can quote the request. A cancellation is left to the caller, which tells
    ///     the budget's expiry from the caller's own.
    /// </summary>
    private async ValueTask<NodeSelection> AskAsync(IDecisionClient decisions, NodeSelectionContext context, CancellationToken budget)
    {
        try
        {
            var result = await decisions.EvaluateAsync<NodeQuestions>(StateOf(context), budget);
            if (result.IsFailure)
            {
                var kind = result.Error.Kind;
                LogDecisionFailed(context.Node, kind);
                return NodeSelectionMapper.Default(context.Menu, $"error:{kind}");
            }

            return NodeSelectionMapper.Map(result.Value, context.Menu, config, context.NarrowTools);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

        return DecisionStateJson.Serialize(context.Role, context.Node, context.Visit, intent);
    }

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
