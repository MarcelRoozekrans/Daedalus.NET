using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Agents.Decisions;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The payload of a <c>node-selection</c> run record, <c>{ level, model, tool_filter, kept_families, fallback,
///     confidence, answers }</c>: written by <see cref="NodeSelectionRunner"/> and read by it and by the run view, through this
///     one type so the two cannot disagree about the schema. Tool names appear in it, unlike in telemetry: a run record is the
///     run's own audit, read only by people who can read the run. <c>answers</c>, when Minos answered, is
///     <c>{ question_set, demand, demand_confidence, families }</c>: the <see cref="NodeQuestions.Version"/> answered, the
///     demand level and its confidence, and each tool family's probability. It holds family names and numbers, never task
///     text. A record written before <c>answers</c> existed, or for a visit nothing was asked about, has none.
/// </summary>
public static class NodeSelectionRecord
{
    /// <summary>The payload JSON for <paramref name="selection"/>.</summary>
    public static string Serialize(NodeSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return JsonSerializer.Serialize(Payload.From(selection));
    }

    /// <summary>
    ///     The recorded decision, or <see langword="null"/> when the payload is not one <see cref="Serialize"/> wrote: not
    ///     JSON, no model or level, or a tool filter Thalos would refuse (empty, or with a blank entry), which would fail
    ///     every replay of the visit. A payload without <c>answers</c> reads with none.
    /// </summary>
    public static NodeSelection? Parse(string payloadJson)
    {
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Model) || string.IsNullOrWhiteSpace(payload.Level))
            return null;

        if (payload.ToolFilter is { } filter && (filter.Count == 0 || filter.Any(string.IsNullOrWhiteSpace)))
            return null;

        return new NodeSelection(payload.Model, payload.ToolFilter, payload.Level, payload.KeptFamilies, payload.Fallback, payload.Confidence)
        {
            // Answers are audit data, never needed to run the turn, so an incomplete answers object reads as none.
            Answers = payload.Answers is { Demand: not null, Families: not null } answers ? answers.ToAnswers() : null,
        };
    }

    private sealed record Payload(
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("tool_filter")] IReadOnlyList<string>? ToolFilter,
        [property: JsonPropertyName("kept_families")] int KeptFamilies,
        [property: JsonPropertyName("fallback")] string? Fallback,
        [property: JsonPropertyName("confidence")] double? Confidence,
        [property: JsonPropertyName("answers")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnswersPayload? Answers)
    {
        public static Payload From(NodeSelection selection) =>
            new(selection.Level, selection.Model, selection.ToolFilter, selection.KeptFamilies, selection.Fallback, selection.Confidence,
                selection.Answers is { } answers ? AnswersPayload.From(answers) : null);
    }

    private sealed record AnswersPayload(
        [property: JsonPropertyName("question_set")] int QuestionSet,
        [property: JsonPropertyName("demand")] string Demand,
        [property: JsonPropertyName("demand_confidence")] double DemandConfidence,
        [property: JsonPropertyName("families")] IReadOnlyDictionary<string, double> Families)
    {
        public static AnswersPayload From(NodeAnswers answers) => new(answers.QuestionSet, answers.Demand, answers.DemandConfidence, answers.Families);

        public NodeAnswers ToAnswers() => new(QuestionSet, Demand, DemandConfidence, Families);
    }
}
