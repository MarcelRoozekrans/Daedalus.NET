using System.Text.Json;
using Daedalus.Domain.Entities;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The payload of a <see cref="WorkflowRunRecord.ReviewResponseKind"/> record, <c>{ "response": &lt;value&gt; }</c>, written by
///     <see cref="ReviewResponseRecorder"/> and read back by the run view. One type for both sides, so they cannot disagree
///     about the shape.
/// </summary>
public static class ReviewResponseRecord
{
    /// <summary>The payload's one property.</summary>
    public const string ResponseProperty = "response";

    /// <summary>
    ///     Whether <paramref name="value"/>, as a node reported it, is a response at all. Absent, null, JSON null, an empty string
    ///     and an empty array are not, and nothing is recorded for them: an empty answer answers nothing.
    /// </summary>
    public static bool IsResponse(object? value) => value switch
    {
        null => false,
        string text => text.Length > 0,
        JsonElement json => json.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => json.GetString()!.Length > 0,
            JsonValueKind.Array => json.GetArrayLength() > 0,
            _ => true,
        },
        System.Collections.ICollection items => items.Count > 0,
        _ => true,
    };

    /// <summary>
    ///     The payload for <paramref name="value"/>, the value as implement reported it. The value is written through the
    ///     JSON-shaped contract Thalos keeps for workflow variables, so it needs no reflection; a value outside it, such as
    ///     a <see cref="Guid"/>, throws <see cref="ArgumentException"/> naming its type.
    /// </summary>
    public static string ToPayloadJson(object? value) =>
        JsonSerializer.Serialize(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [ResponseProperty] = PlainJson.ToElement(value) },
            DaedalusRecordsJsonContext.Default.DictionaryStringJsonElement);

    /// <summary>
    ///     The response as the run page shows it: a string as it is, an array of strings one entry per line (the shape the
    ///     implement skill asks for), and any other value as its JSON text. A failure when the record has no
    ///     <c>response</c> property or its payload is not JSON.
    /// </summary>
    public static Result<string> Read(WorkflowRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            using var payload = JsonDocument.Parse(record.PayloadJson);
            var root = payload.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(ResponseProperty, out var response)
                && response.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return Result<string>.Success(Text(response));
            }
        }
        catch (JsonException)
        {
            // Reported below, the same as a payload of the wrong shape.
        }

        return Result<string>.Failure($"review-response record {record.Id} has no readable response");
    }

    private static string Text(JsonElement response) => response.ValueKind switch
    {
        JsonValueKind.String => response.GetString()!,
        JsonValueKind.Array when response.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) =>
            string.Join('\n', response.EnumerateArray().Select(e => e.GetString())),
        _ => response.GetRawText(),
    };
}
