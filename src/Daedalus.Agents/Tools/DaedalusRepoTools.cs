using System.ComponentModel;
using System.Globalization;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Options;
using Thalos;

namespace Daedalus.Agents.Tools;

/// <summary>
///     Read-only GitHub observation exposed to Thalos agents: <c>repo_activity</c> and <c>repo_default_branch</c>.
/// </summary>
/// <remarks>
///     <para>
///     <b>Only <see cref="IGitHubReader"/>.</b> This class cannot reach <see cref="IGitHubWriter"/> or
///     <c>GitHubApi</c> directly — the security boundary between observing a repository and acting on it starts
///     here. A tool class that cannot reach the writer cannot write, whatever an agent asks it to do.
///     </para>
///     <para>
///     <b>Why text, not JSON.</b> A model reads this output once and reasons over it; it has no grid, no colour.
///     A category this tool could not read is stated as unreadable, with the reason, and never rendered with the
///     same wording as a category that was read and found empty — collapsing the two produces a digest that calls
///     a repository quiet when it does not actually know that.
///     </para>
/// </remarks>
/// <param name="reader">The one seam this tool reads a repository through.</param>
/// <param name="clock">
///     Where "now" comes from when <c>since</c> is omitted. Required, like every other <see cref="TimeProvider"/>
///     consumer in this codebase (<c>PostgresMemoryStore</c>, <c>ScheduledRunStore</c>, <c>ScheduleReconciler</c>,
///     <c>PostgresAgentSessionStore</c>, <c>PostgresSkillStore</c>): an optional clock with an ambient fallback
///     would let a DI regression silently substitute <see cref="TimeProvider.System"/> instead of failing fast.
/// </param>
/// <param name="options">
///     The configured lookback used when <c>since</c> is omitted. Required for the same reason as
///     <paramref name="clock"/>: a broken configuration binding must surface as a startup error, not silently
///     fall back to a hardcoded default.
/// </param>
[ThalosToolType]
public sealed class DaedalusRepoTools(IGitHubReader reader, TimeProvider clock, IOptions<GitHubOptions> options)
{
    private GitHubOptions Options => options.Value;

    /// <summary>Reports commits, pull requests, issues and failed CI runs for a repository within a time window.</summary>
    [ThalosTool("repo_activity")]
    [Description(
        "Report a GitHub repository's recent activity: commits, merged pull requests, open pull requests, " +
        "issues, and failed CI runs. A category that could not be read is reported as unreadable with the " +
        "reason, never as empty. The output states the exact window it covers.")]
    public async Task<string> RepoActivity(
        [Description("The repository to inspect, given as owner/name (e.g. octocat/hello-world).")] string repo,
        [Description(
            "Only include activity at or after this UTC instant, as a round-trip ISO 8601 timestamp " +
            "(e.g. 2026-09-18T07:00:00Z). Omit to use the configured lookback window.")] string? since,
        CancellationToken ct = default)
    {
        var parsedRepo = RepoRef.Parse(repo);
        if (parsedRepo.IsFailure)
            return parsedRepo.Error;

        if (!TryResolveSince(since, out var sinceUtc, out var error))
            return error;

        var activity = await reader.GetActivityAsync(parsedRepo.Value, sinceUtc, ct);
        return Render(activity);
    }

    /// <summary>Looks up a repository's default branch.</summary>
    [ThalosTool("repo_default_branch")]
    [Description("Look up a GitHub repository's default branch.")]
    public async Task<string> RepoDefaultBranch(
        [Description("The repository to inspect, given as owner/name (e.g. octocat/hello-world).")] string repo,
        CancellationToken ct = default)
    {
        var parsedRepo = RepoRef.Parse(repo);
        if (parsedRepo.IsFailure)
            return parsedRepo.Error;

        var result = await reader.GetDefaultBranchAsync(parsedRepo.Value, ct);
        return result.IsSuccess ? result.Value : $"Could not determine the default branch: {result.Error}";
    }

    private bool TryResolveSince(string? since, out DateTime sinceUtc, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(since))
        {
            sinceUtc = clock.GetUtcNow().UtcDateTime - Options.DefaultLookback;
            return true;
        }

        if (DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            sinceUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            return true;
        }

        sinceUtc = default;
        error = $"'{since}' is not a recognizable UTC timestamp. Use a round-trip ISO 8601 value such as 2026-09-18T07:00:00Z.";
        return false;
    }

    private static string Render(RepoActivity activity)
    {
        var header =
            $"Activity in {activity.Repo} between {activity.WindowStartUtc:yyyy-MM-dd HH:mm} and " +
            $"{activity.WindowEndUtc:yyyy-MM-dd HH:mm} UTC:";

        string[] lines =
        [
            header,
            RenderCategory("commits", activity.Commits, FormatCommit),
            RenderCategory("merged pull requests", activity.MergedPullRequests, FormatPullRequest),
            RenderCategory("open pull requests", activity.OpenPullRequests, FormatPullRequest),
            RenderCategory("issues", activity.Issues, FormatIssue),
            RenderCategory("failed CI runs", activity.FailedRuns, FormatRun),
        ];

        return string.Join('\n', lines);
    }

    /// <summary>
    ///     Renders one category. <see cref="CategoryResult{T}.Checked"/> decides the branch: an unchecked category
    ///     always says it could not be read, with the reason, and this must never fall through to the
    ///     checked-and-empty wording below it — that collapse is exactly the failure this tool exists to prevent.
    /// </summary>
    private static string RenderCategory<T>(string label, CategoryResult<T> category, Func<T, string> format)
    {
        if (!category.Checked)
            return $"- {Capitalize(label)} could not be read: {category.Unavailable}.";

        if (category.Truncated && category.Items.Count == 0)
            return $"- {Capitalize(label)}: more of this window exists than could be read, and none of what " +
                   "was read qualified — this category is incomplete, not clean.";

        if (category.Items.Count == 0)
            return $"- No {label}.";

        var header = category.Truncated
            ? $"- More than {category.Items.Count} {label}:"
            : $"- {category.Items.Count} {label}:";

        var itemLines = category.Items.Select(item => $"    - {format(item)}");
        return string.Join('\n', [header, .. itemLines]);
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static string FormatCommit(CommitSummary c) =>
        $"{c.Sha[..Math.Min(7, c.Sha.Length)]} {c.Message} ({c.Author}, {c.CommittedAtUtc:yyyy-MM-dd HH:mm} UTC)";

    private static string FormatPullRequest(PullRequestSummary p) =>
        $"#{p.Number} {p.Title} ({p.Author}, updated {p.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC)";

    private static string FormatIssue(IssueSummary i) =>
        $"#{i.Number} {i.Title} [{i.State}] (updated {i.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC)";

    private static string FormatRun(WorkflowRunSummary r) =>
        $"{r.Name} on {r.HeadBranch}: {r.Conclusion} (started {r.RunStartedAtUtc:yyyy-MM-dd HH:mm} UTC)";
}
