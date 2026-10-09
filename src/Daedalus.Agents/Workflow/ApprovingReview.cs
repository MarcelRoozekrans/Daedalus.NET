using System.Text.Json;
using Daedalus.Domain.Entities;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>One lens of a review visit: what it checked.</summary>
public sealed record LensApproval(string Lens, IReadOnlyList<string> Checked);

/// <summary>A deferred finding of the approving visit, with the id the gate and the filing action both name it by.</summary>
public sealed record IdentifiedDeferredFinding(string Id, string Lens, DeferredFinding Finding);

/// <summary>
///     The latest review visit as the host recorded it. <see cref="Deferred"/> is empty unless <see cref="Approved"/>:
///     a visit with a rejection sends the run back to implement, and anything it deferred points at code about to change.
/// </summary>
public sealed record ReviewVisit(
    long Seq, bool Approved, string? RejectedLens, IReadOnlyList<LensApproval> Lenses, IReadOnlyList<IdentifiedDeferredFinding> Deferred);

/// <summary>
///     Reads the latest review visit off a run's <see cref="WorkflowRunRecord.ReviewEvidenceKind"/> records: those at the
///     highest <see cref="WorkflowRunRecord.Seq"/>, the last record per lens. A redelivered review node records its
///     lenses again at the same seq, and the store lists records in append order within a seq, so the last one is the
///     visit's final word. Payloads are read as parsed JSON, since <c>jsonb</c> keeps no text. One reader for
///     <c>open-pull-request</c>, the gate and <c>file-review-findings</c>, so the three cannot disagree about which
///     findings exist or what their ids are.
/// </summary>
public static class ApprovingReview
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The latest visit, <see langword="null"/> when the run records no review evidence at all, or a failure naming the
    ///     first record that cannot be read. Records of other kinds are ignored, so a caller may pass a full listing.
    /// </summary>
    public static Result<ReviewVisit?> Read(IReadOnlyList<WorkflowRunRecord>? records)
    {
        if (records is null)
            return Result<ReviewVisit?>.Failure("the run record store answered with no review evidence list");

        var evidence = records.Where(r => string.Equals(r.Kind, WorkflowRunRecord.ReviewEvidenceKind, StringComparison.Ordinal)).ToList();
        if (evidence.Count == 0)
            return Result<ReviewVisit?>.Success(null);

        var seq = evidence.Max(r => r.Seq);
        var lenses = new List<(string Lens, bool Approved, IReadOnlyList<string> Checked, IReadOnlyList<DeferredFinding> Deferred)>();
        foreach (var record in evidence.Where(r => r.Seq == seq))
        {
            var read = ReadRecord(record);
            if (read.IsFailure)
                return Result<ReviewVisit?>.Failure(read.Error);

            var at = lenses.FindIndex(l => string.Equals(l.Lens, read.Value.Lens, StringComparison.Ordinal));
            if (at >= 0)
                lenses[at] = read.Value;
            else
                lenses.Add(read.Value);
        }

        var rejected = lenses.FirstOrDefault(l => !l.Approved).Lens;
        var approved = rejected is null;
        IReadOnlyList<IdentifiedDeferredFinding> deferred = approved
            ? [.. lenses.SelectMany(l => l.Deferred.Select((d, i) => new IdentifiedDeferredFinding($"{l.Lens}-{i + 1}", l.Lens, d)))]
            : [];

        return Result<ReviewVisit?>.Success(new ReviewVisit(
            seq, approved, rejected, [.. lenses.Select(l => new LensApproval(l.Lens, l.Checked))], deferred));
    }

    private static Result<(string Lens, bool Approved, IReadOnlyList<string> Checked, IReadOnlyList<DeferredFinding> Deferred)> ReadRecord(
        WorkflowRunRecord record)
    {
        try
        {
            using var payload = JsonDocument.Parse(record.PayloadJson);
            var root = payload.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("lens", out var lens) && lens.ValueKind == JsonValueKind.String
                && root.TryGetProperty("verdict", out var verdict) && verdict.ValueKind == JsonValueKind.String
                && root.TryGetProperty("checked", out var examined) && examined.ValueKind == JsonValueKind.Array
                && examined.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
            {
                IReadOnlyList<DeferredFinding> deferred = [];
                if (root.TryGetProperty("deferred", out var d) && d.ValueKind == JsonValueKind.Array)
                    deferred = JsonSerializer.Deserialize<DeferredFinding[]>(d.GetRawText(), JsonOptions) ?? [];

                return Result<(string, bool, IReadOnlyList<string>, IReadOnlyList<DeferredFinding>)>.Success((
                    lens.GetString()!,
                    string.Equals(verdict.GetString(), ReviewEvidence.Approved, StringComparison.Ordinal),
                    [.. examined.EnumerateArray().Select(e => e.GetString()!)],
                    deferred));
            }
        }
        catch (JsonException)
        {
            // Reported below with the record's id, the same as a payload of the wrong shape.
        }

        return Result<(string, bool, IReadOnlyList<string>, IReadOnlyList<DeferredFinding>)>.Failure(
            $"review evidence record {record.Id} has no readable lens, verdict, checked list and deferred list");
    }
}
