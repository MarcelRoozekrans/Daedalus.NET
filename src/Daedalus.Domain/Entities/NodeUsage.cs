using System.Text;
using System.Text.Json;
using ZeroAlloc.Results;

namespace Daedalus.Domain.Entities;

/// <summary>
///     The token usage of one completed agent node of a manufacture run: the payload of a
///     <see cref="WorkflowRunRecord.NodeUsageKind"/> record. <see cref="InputTokens"/> is the total input, and it already
///     includes <see cref="CacheReadTokens"/> and <see cref="CacheWriteTokens"/>, as the provider reports them.
/// </summary>
/// <param name="Model">The model the turn ran on, or null when the provider reported none.</param>
/// <param name="InputTokens">All input tokens, cache reads and writes included.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CacheReadTokens">Input tokens read from a prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to a prompt cache.</param>
public sealed record NodeUsage(string? Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    /// <summary>The payload as JSON, with camel-case keys.</summary>
    public string ToPayloadJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (Model is null)
            {
                writer.WriteNull("model");
            }
            else
            {
                writer.WriteString("model", Model);
            }

            writer.WriteNumber("inputTokens", InputTokens);
            writer.WriteNumber("outputTokens", OutputTokens);
            writer.WriteNumber("cacheReadTokens", CacheReadTokens);
            writer.WriteNumber("cacheWriteTokens", CacheWriteTokens);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads a payload <see cref="ToPayloadJson"/> wrote. A missing or negative count is a failure, never zero.</summary>
    public static Result<NodeUsage> FromPayloadJson(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryCount(root, "inputTokens", out var input)
                || !TryCount(root, "outputTokens", out var output)
                || !TryCount(root, "cacheReadTokens", out var cacheRead)
                || !TryCount(root, "cacheWriteTokens", out var cacheWrite))
            {
                return Result<NodeUsage>.Failure("A node-usage payload needs four non-negative token counts.");
            }

            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return Result<NodeUsage>.Success(new NodeUsage(model, input, output, cacheRead, cacheWrite));
        }
        catch (JsonException ex)
        {
            return Result<NodeUsage>.Failure($"A node-usage payload is not valid JSON: {ex.Message}");
        }
    }

    private static bool TryCount(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && value >= 0;
    }
}
