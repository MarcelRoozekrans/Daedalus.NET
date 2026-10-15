using System.Globalization;
using Daedalus.Agents.Workflow;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Domain.Entities;

namespace Daedalus.Api.Services;

/// <summary>
///     Every review visit of a run, read on request off its <c>review-evidence</c> records and paired with the
///     <c>review-response</c> record of the implement visit that answered it. Nothing is stored: the history is whatever the
///     host's records say today.
/// </summary>
/// <remarks>
///     <para>
///     <b>A visit is the records at one seq.</b> <see cref="ReviewLensRunner"/> writes one record per lens pass at the run's
///     current seq, so a review visit's lenses share it. A redelivered review records its lenses again at the same seq, and
///     the last record per lens is the visit's final word, the same rule <see cref="ApprovingReview"/> applies to the latest
///     visit. Each record is read by <see cref="ApprovingReview.ReadLens"/>, the one parser for these records.
///     </para>
///     <para>
///     <b>Which answer belongs to which rejection.</b> A rejection sends the run back to implement, so the answer to a rejected
///     visit is the <c>review-response</c> record of the next implement visit: the earliest response seq after the visit's own
///     and before the next review visit's. A response after that bound answers a later rejection, so a visit whose own
///     implement visit recorded nothing reads as none recorded rather than borrowing it. An approval has no answer.
///     </para>
///     <para>
///     <b>A record that cannot be read is reported, not dropped silently.</b> It is left out, the readable records around it are
///     still listed, and the error names its seq, as <c>NodeSelectionsError</c> does.
///     </para>
/// </remarks>
internal static class ReviewVisitHistory
{
    /// <summary>The verdict of a visit no lens rejected.</summary>
    internal const string Approved = "Approved";

    /// <summary>The verdict of a visit a lens rejected.</summary>
    internal const string Rejected = "Rejected";

    /// <summary>
    ///     The visits, newest first, and which records could not be read, or <see langword="null"/> when every record could.
    ///     Records of other kinds are ignored.
    /// </summary>
    internal static (IReadOnlyList<ReviewVisitView> Visits, string? Error) Read(
        IReadOnlyList<WorkflowRunRecord> evidence, IReadOnlyList<WorkflowRunRecord> responses)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(responses);

        var unreadableEvidence = new List<long>();
        var groups = evidence
            .Where(r => string.Equals(r.Kind, WorkflowRunRecord.ReviewEvidenceKind, StringComparison.Ordinal))
            .OrderBy(r => r.Seq).ThenBy(r => r.Id)
            .GroupBy(r => r.Seq)
            .ToList();
        var reviewSeqs = groups.Select(g => g.Key).ToList();
        var answers = ReadAnswers(responses, out var unreadableResponses);

        var visits = new List<ReviewVisitView>(groups.Count);
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var lenses = ReadLenses(groups[i], unreadableEvidence);
            if (lenses.Count == 0)
            {
                continue;
            }

            var seq = groups[i].Key;
            var rejecting = lenses.Find(l => !l.Approved);
            var nextReview = i + 1 < reviewSeqs.Count ? reviewSeqs[i + 1] : long.MaxValue;
            visits.Add(new ReviewVisitView(
                seq,
                rejecting is null ? Approved : Rejected,
                rejecting?.Lens,
                [.. lenses.Select(ToView)],
                rejecting is null ? null : AnswerBetween(answers, seq, nextReview)));
        }

        unreadableEvidence.Sort();
        var errors = new[] { Describe("review-evidence", unreadableEvidence), Describe("review-response", unreadableResponses) }
            .OfType<string>()
            .ToList();
        return (visits, errors.Count > 0 ? string.Join(' ', errors) : null);
    }

    /// <summary>The visit's lenses in the order they first ran, the last record per lens winning.</summary>
    private static List<ReviewLensEvidence> ReadLenses(IEnumerable<WorkflowRunRecord> records, List<long> unreadable)
    {
        var lenses = new List<ReviewLensEvidence>();
        foreach (var record in records)
        {
            var read = ApprovingReview.ReadLens(record);
            if (read.IsFailure)
            {
                unreadable.Add(record.Seq);
                continue;
            }

            var at = lenses.FindIndex(l => string.Equals(l.Lens, read.Value.Lens, StringComparison.Ordinal));
            if (at >= 0)
            {
                lenses[at] = read.Value;
            }
            else
            {
                lenses.Add(read.Value);
            }
        }

        return lenses;
    }

    /// <summary>Each readable response by seq, the last record at a seq winning, as a redelivered implement visit writes again.</summary>
    private static SortedList<long, string> ReadAnswers(IReadOnlyList<WorkflowRunRecord> responses, out List<long> unreadable)
    {
        unreadable = [];
        var answers = new SortedList<long, string>();
        foreach (var record in responses
            .Where(r => string.Equals(r.Kind, WorkflowRunRecord.ReviewResponseKind, StringComparison.Ordinal))
            .OrderBy(r => r.Seq).ThenBy(r => r.Id))
        {
            var read = ReviewResponseRecord.Read(record);
            if (read.IsFailure)
            {
                unreadable.Add(record.Seq);
                continue;
            }

            answers[record.Seq] = read.Value;
        }

        return answers;
    }

    /// <summary>The earliest answer after <paramref name="after"/> and before <paramref name="before"/>, or none.</summary>
    private static string? AnswerBetween(SortedList<long, string> answers, long after, long before)
    {
        foreach (var (seq, answer) in answers)
        {
            if (seq > after)
            {
                return seq < before ? answer : null;
            }
        }

        return null;
    }

    private static LensCheckedView ToView(ReviewLensEvidence lens) =>
        new(lens.Lens, lens.Checked, [.. lens.Findings.Select(f => new ReviewFindingView(f.File, f.Line, f.Scenario))]);

    private static string? Describe(string kind, List<long> seqs) =>
        seqs.Count == 0
            ? null
            : $"{seqs.Count.ToString(CultureInfo.InvariantCulture)} {kind} record(s) could not be read, at seq " +
                $"{string.Join(", ", seqs.Select(s => s.ToString(CultureInfo.InvariantCulture)))}.";
}
