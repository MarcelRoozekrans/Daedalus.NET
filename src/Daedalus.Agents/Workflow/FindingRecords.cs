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

/// <summary>What <c>file-review-findings</c> did with one deferred finding, or with the summary.</summary>
public sealed record FiledFinding(string FindingId, string Mode, int Issue, Uri Url);

/// <summary>
///     Writes and reads the host-written records of what happened to a run's deferred findings (spec amendment A1). Both
///     halves live here, so the payload shape the gateway writes is the one <c>file-review-findings</c> reads.
/// </summary>
public static class FindingRecords
{
    /// <summary>A new issue was filed.</summary>
    public const string CreatedMode = "created";

    /// <summary>The finding was added to an open issue the reviewer named.</summary>
    public const string CommentedMode = "commented";

    /// <summary>The pull-request summary comment was posted.</summary>
    public const string SummaryMode = "summary";

    /// <summary>The finding id the summary is recorded and marked under.</summary>
    public const string SummaryId = "summary";

    /// <summary>
    ///     Who a drop is credited to in the public pull-request summary when the approver has no display name. Never the
    ///     subject id: the summary is public, and the record's own principal already holds the id.
    /// </summary>
    public const string UnnamedApprover = "the approver";

    /// <summary>The payload of a <see cref="WorkflowRunRecord.FindingsDroppedKind"/> record; <paramref name="attempt"/> is the resume attempt it belongs to.</summary>
    public static string DroppedPayload(IReadOnlyCollection<string> ids, string? by, string attempt) =>
        JsonSerializer.Serialize(new { attempt, dropped = ids, by });

    /// <summary>The payload of a <see cref="WorkflowRunRecord.FindingsDropVoidedKind"/> record naming <paramref name="attempt"/>.</summary>
    public static string DropVoidedPayload(string attempt) => JsonSerializer.Serialize(new { attempt });

    /// <summary>
    ///     The latest drop record whose attempt no <see cref="WorkflowRunRecord.FindingsDropVoidedKind"/> record names,
    ///     <see cref="DroppedFindings.None"/> when none remains, or a failure naming an unreadable record. A drop record
    ///     with no attempt is unreadable.
    /// </summary>
    public static Result<DroppedFindings> ReadDropped(IReadOnlyList<WorkflowRunRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var voided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.Where(r => string.Equals(r.Kind, WorkflowRunRecord.FindingsDropVoidedKind, StringComparison.Ordinal)))
        {
            var attempt = ReadAttempt(record);
            if (attempt is null)
                return Result<DroppedFindings>.Failure($"findings-drop-voided record {record.Id} has no readable 'attempt'");

            voided.Add(attempt);
        }

        WorkflowRunRecord? latest = null;
        foreach (var record in records.Where(r => string.Equals(r.Kind, WorkflowRunRecord.FindingsDroppedKind, StringComparison.Ordinal)))
        {
            var attempt = ReadAttempt(record);
            if (attempt is null)
                return Result<DroppedFindings>.Failure($"findings-dropped record {record.Id} has no readable 'attempt'");

            if (!voided.Contains(attempt))
                latest = record;
        }

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

    private static string? ReadAttempt(WorkflowRunRecord record)
    {
        try
        {
            using var doc = JsonDocument.Parse(record.PayloadJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("attempt", out var attempt)
                && attempt.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(attempt.GetString())
                    ? attempt.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The payload of a <see cref="WorkflowRunRecord.FindingFiledKind"/> record.</summary>
    public static string FiledPayload(FiledFinding filed)
    {
        ArgumentNullException.ThrowIfNull(filed);
        return JsonSerializer.Serialize(new { id = filed.FindingId, mode = filed.Mode, issue = filed.Issue, url = filed.Url.AbsoluteUri });
    }

    /// <summary>Every finding already handled, by id; a later record for the same id wins.</summary>
    public static Result<IReadOnlyDictionary<string, FiledFinding>> ReadFiled(IReadOnlyList<WorkflowRunRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var filed = new Dictionary<string, FiledFinding>(StringComparer.Ordinal);
        foreach (var record in records.Where(r => string.Equals(r.Kind, WorkflowRunRecord.FindingFiledKind, StringComparison.Ordinal)))
        {
            var read = ReadOneFiled(record);
            if (read is null)
                return Result<IReadOnlyDictionary<string, FiledFinding>>.Failure($"finding-filed record {record.Id} is unreadable");

            filed[read.FindingId] = read;
        }

        return Result<IReadOnlyDictionary<string, FiledFinding>>.Success(filed);
    }

    private static FiledFinding? ReadOneFiled(WorkflowRunRecord record)
    {
        try
        {
            using var doc = JsonDocument.Parse(record.PayloadJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())
                && root.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mode.GetString())
                && root.TryGetProperty("issue", out var issue) && issue.ValueKind == JsonValueKind.Number && issue.TryGetInt32(out var number) && number > 0
                && root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var link))
            {
                return new FiledFinding(id.GetString()!, mode.GetString()!, number, link);
            }
        }
        catch (JsonException)
        {
            // Reported by the caller, the same as a payload of the wrong shape.
        }

        return null;
    }
}
