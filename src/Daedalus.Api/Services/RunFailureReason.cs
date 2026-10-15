using System.Globalization;
using Daedalus.Application.DTOs.WorkflowRuns;

namespace Daedalus.Api.Services;

/// <summary>
///     Why a failed run failed, derived on read from the run's steps, its last error and, for a run the review limit
///     stopped, the lens that last rejected it. Nothing is stored: a run's reason is whatever its log says today.
/// </summary>
internal static class RunFailureReason
{
    private const string FailedStatus = "Failed";
    private const string CapExceededKind = "CapExceeded";
    private const string ReviewNode = "review";
    private const string ImplementNode = "implement";
    private const string RejectedOutcome = "rejected";
    private const string BlockedOutcome = "blocked";

    /// <summary>Whether <paramref name="steps"/> end in the review loop hitting its cap, which is the one reason that needs the lens.</summary>
    internal static bool IsCappedReview(IReadOnlyList<WorkflowRunStepView> steps) =>
        steps.Any(IsReviewCap);

    /// <summary>
    ///     The reason, or <see langword="null"/> unless <paramref name="status"/> is <c>Failed</c>. In order: the review
    ///     limit, a blocked implement, the host's <paramref name="lastError"/>, and last the node the run ended at.
    /// </summary>
    /// <param name="status">The run's status.</param>
    /// <param name="steps">The run's steps in seq order.</param>
    /// <param name="lastError">The run's last host error, if any.</param>
    /// <param name="rejectingLens">
    ///     The lens of the latest rejected review visit, or <see langword="null"/> when its evidence cannot be read, in
    ///     which case the review-limit reason leaves the lens sentence out.
    /// </param>
    internal static string? Derive(
        string status, IReadOnlyList<WorkflowRunStepView> steps, string? lastError, string? rejectingLens = null)
    {
        if (!string.Equals(status, FailedStatus, StringComparison.Ordinal))
        {
            return null;
        }

        if (IsCappedReview(steps))
        {
            var rejected = steps.Count(s =>
                string.Equals(s.FromNode, ReviewNode, StringComparison.Ordinal)
                && string.Equals(s.Outcome, RejectedOutcome, StringComparison.Ordinal));
            var reason = string.Create(CultureInfo.InvariantCulture, $"Review rejected the change {rejected} times, the review limit.");
            return string.IsNullOrWhiteSpace(rejectingLens) ? reason : $"{reason} Last rejecting lens: {rejectingLens}.";
        }

        if (steps.Any(s =>
            string.Equals(s.FromNode, ImplementNode, StringComparison.Ordinal)
            && string.Equals(s.Outcome, BlockedOutcome, StringComparison.Ordinal)))
        {
            return "Implement reported it is blocked.";
        }

        if (!string.IsNullOrWhiteSpace(lastError))
        {
            return lastError;
        }

        return steps.Count > 0 ? $"The run ended at {steps[^1].ToNode}." : "The run ended before any step.";
    }

    private static bool IsReviewCap(WorkflowRunStepView step) =>
        string.Equals(step.Kind, CapExceededKind, StringComparison.Ordinal)
        && string.Equals(step.FromNode, ReviewNode, StringComparison.Ordinal);
}
