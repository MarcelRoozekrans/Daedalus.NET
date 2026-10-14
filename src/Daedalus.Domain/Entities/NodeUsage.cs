using System.Text;
using System.Text.Json;
using ZeroAlloc.Results;

namespace Daedalus.Domain.Entities;

/// <summary>
///     The token usage of one completed agent node of a manufacture run: the payload of a
///     <see cref="WorkflowRunRecord.NodeUsageKind"/> record. <see cref="InputTokens"/> is the total input, and it already
///     includes <see cref="CacheReadTokens"/> and <see cref="CacheWriteTokens"/>, as the provider reports them.
/// </summary>
/// <remarks>
///     <b>A node can run on more than one model.</b> A review node whose lens is pinned to a model, phase 2.10, runs that
///     lens's pass on the pinned model and the others on the node's own. Its record then carries <see cref="ByModel"/>: one
///     part per model, in the order the models first ran, whose counts sum to the totals. Cost analytics prices each part at
///     its own model's rate. The totals and <see cref="Model"/>, the first model the node ran on, stay as they were, so a
///     reader that knows nothing of the split still reads the node's total. A record without the split, every record written
///     before it existed and every node that ran on one model, is one part: the record itself.
/// </remarks>
/// <param name="Model">The model the turn ran on, or null when the provider reported none.</param>
/// <param name="InputTokens">All input tokens, cache reads and writes included.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CacheReadTokens">Input tokens read from a prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to a prompt cache.</param>
public sealed record NodeUsage(string? Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    /// <summary>
    ///     The usage per model, when the node ran on more than one; <see langword="null"/> when it ran on one. Set through
    ///     <see cref="WithSplit"/> or read back by <see cref="FromPayloadJson"/>, which both check that the parts sum to the
    ///     totals. A part never carries a split of its own.
    /// </summary>
    public IReadOnlyList<NodeUsage>? ByModel { get; private init; }

    /// <summary>What is priced: each part of <see cref="ByModel"/>, or this usage alone when there is no split.</summary>
    public IReadOnlyList<NodeUsage> Parts => ByModel ?? [this];

    /// <summary>
    ///     This usage with <paramref name="parts"/> as its per-model split. A failure when there are fewer than two parts, a
    ///     part carries a split itself, or the parts do not sum to this usage's four counts.
    /// </summary>
    public Result<NodeUsage> WithSplit(IReadOnlyList<NodeUsage> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count < 2 || parts.Any(p => p.ByModel is not null))
        {
            return Result<NodeUsage>.Failure("A per-model split needs at least two parts, none split itself.");
        }

        if (parts.Sum(p => p.InputTokens) != InputTokens
            || parts.Sum(p => p.OutputTokens) != OutputTokens
            || parts.Sum(p => p.CacheReadTokens) != CacheReadTokens
            || parts.Sum(p => p.CacheWriteTokens) != CacheWriteTokens)
        {
            return Result<NodeUsage>.Failure("A per-model split must sum to the node's totals.");
        }

        return Result<NodeUsage>.Success(this with { ByModel = [.. parts] });
    }

    /// <summary>The payload as JSON, with camel-case keys. A split is written as <c>byModel</c>, an array of parts.</summary>
    public string ToPayloadJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteCounts(writer, this);
            if (ByModel is { } parts)
            {
                writer.WriteStartArray("byModel");
                foreach (var part in parts)
                {
                    writer.WriteStartObject();
                    WriteCounts(writer, part);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCounts(Utf8JsonWriter writer, NodeUsage usage)
    {
        if (usage.Model is null)
        {
            writer.WriteNull("model");
        }
        else
        {
            writer.WriteString("model", usage.Model);
        }

        writer.WriteNumber("inputTokens", usage.InputTokens);
        writer.WriteNumber("outputTokens", usage.OutputTokens);
        writer.WriteNumber("cacheReadTokens", usage.CacheReadTokens);
        writer.WriteNumber("cacheWriteTokens", usage.CacheWriteTokens);
    }

    /// <summary>
    ///     Reads a payload <see cref="ToPayloadJson"/> wrote. A missing or negative count is a failure, never zero. A payload
    ///     without <c>byModel</c> has no split; one with a <c>byModel</c> that is not an array of valid parts summing to the
    ///     totals is a failure, so a node is never priced from a split that disagrees with its own total.
    /// </summary>
    public static Result<NodeUsage> FromPayloadJson(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (ReadCounts(root) is not { } usage)
            {
                return Result<NodeUsage>.Failure("A node-usage payload needs four non-negative token counts.");
            }

            if (!root.TryGetProperty("byModel", out var split))
            {
                return Result<NodeUsage>.Success(usage);
            }

            if (split.ValueKind != JsonValueKind.Array)
            {
                return Result<NodeUsage>.Failure("A node-usage payload's byModel must be an array.");
            }

            var parts = new List<NodeUsage>(split.GetArrayLength());
            foreach (var element in split.EnumerateArray())
            {
                if (ReadCounts(element) is not { } part || element.TryGetProperty("byModel", out _))
                {
                    return Result<NodeUsage>.Failure("Each part of a node-usage payload's byModel needs four non-negative token counts.");
                }

                parts.Add(part);
            }

            return usage.WithSplit(parts);
        }
        catch (JsonException ex)
        {
            return Result<NodeUsage>.Failure($"A node-usage payload is not valid JSON: {ex.Message}");
        }
    }

    private static NodeUsage? ReadCounts(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !TryCount(element, "inputTokens", out var input)
            || !TryCount(element, "outputTokens", out var output)
            || !TryCount(element, "cacheReadTokens", out var cacheRead)
            || !TryCount(element, "cacheWriteTokens", out var cacheWrite))
        {
            return null;
        }

        var model = element.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        return new NodeUsage(model, input, output, cacheRead, cacheWrite);
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
