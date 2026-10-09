using System.Collections.Frozen;
using System.Text.Json;
using Daedalus.Domain.Entities;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>The deferred findings a human dropped at the gate, and who dropped them.</summary>
public sealed record DroppedFindings(IReadOnlySet<string> Ids, string? By)
{
    /// <summary>Nothing dropped, because no record exists.</summary>
    public static readonly DroppedFindings None = new(FrozenSet<string>.Empty, null);
}

/// <summary>
///     Writes and reads the host-written records of what happened to a run's deferred findings (spec amendment A1). Both
///     halves live here, so the payload shape the gateway writes is the one <c>file-review-findings</c> reads.
/// </summary>
public static class FindingRecords
{
    /// <summary>The payload of a <see cref="WorkflowRunRecord.FindingsDroppedKind"/> record.</summary>
    public static string DroppedPayload(IReadOnlyCollection<string> ids, string? by) =>
        JsonSerializer.Serialize(new { dropped = ids, by });

    /// <summary>The latest drop record's ids, <see cref="DroppedFindings.None"/> when there is none, or a failure naming an unreadable record.</summary>
    public static Result<DroppedFindings> ReadDropped(IReadOnlyList<WorkflowRunRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var latest = records.LastOrDefault(r => string.Equals(r.Kind, WorkflowRunRecord.FindingsDroppedKind, StringComparison.Ordinal));
        if (latest is null)
            return Result<DroppedFindings>.Success(DroppedFindings.None);

        try
        {
            using var doc = JsonDocument.Parse(latest.PayloadJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("dropped", out var dropped) && dropped.ValueKind == JsonValueKind.Array
                && dropped.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
            {
                var by = root.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                return Result<DroppedFindings>.Success(new DroppedFindings(
                    dropped.EnumerateArray().Select(e => e.GetString()!).ToFrozenSet(StringComparer.Ordinal), by ?? latest.PrincipalId));
            }
        }
        catch (JsonException)
        {
            // Reported below.
        }

        return Result<DroppedFindings>.Failure($"findings-dropped record {latest.Id} has no readable 'dropped' list");
    }
}
