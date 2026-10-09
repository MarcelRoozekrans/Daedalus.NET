using System.Globalization;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Phase 2.7: the <c>file-review-findings</c> host action, run as node <c>file-findings</c> after <c>publish</c>. It
///     files each deferred finding of the approving review visit that the gate did not drop, as a new issue or as a
///     comment on the open issue the reviewer named. Then it posts one summary comment on the pull request. Host code
///     does all of it; no agent holds a tool that could.
/// </summary>
/// <remarks>
///     <para>
///     <b>Outcomes.</b> <c>none</c> when nothing remains after the drops, and then GitHub is not called. <c>filed</c>
///     otherwise. Every failure is a failed result, never an outcome, so the run stops <c>Failed</c> at this node, where
///     the admin retry re-runs it alone (spec amendment A3).
///     </para>
///     <para>
///     <b>Idempotent, because the outbox redelivers and the admin retries.</b> Each finding's result is a
///     <see cref="WorkflowRunRecord.FindingFiledKind"/> record appended as soon as it is written, and a finding already
///     recorded is skipped (A1). Every write carries <see cref="DeferredFindingText.Marker"/>. Before writing, the issues
///     and comments updated since the gate's resume are scanned for that marker, so a write whose record never landed is
///     found rather than repeated (A2). A record-store failure propagates as an exception, and the outbox retries.
///     </para>
///     <para>
///     <b>Where it files.</b> The repository and pull request come from the run's <c>pr_url</c>, and the repository is
///     checked against the allow-list again (A4). No workspace is needed, which matters because the sweeper may already
///     have removed it.
///     </para>
///     <para>
///     A singleton: the scoped GitHub client and the record store come from a scope of its own per call.
///     </para>
/// </remarks>
internal sealed partial class FileReviewFindingsAction(
    IServiceScopeFactory scopes,
    WorkflowConfig config,
    TimeProvider clock,
    ILogger<FileReviewFindingsAction> logger) : IWorkflowHostAction
{
    /// <summary>The name a process node references with <c>action: file-review-findings</c>.</summary>
    public const string ActionName = ReviewHandoff.FileFindingsActionName;

    /// <summary>At least one finding was filed or commented.</summary>
    public const string FiledOutcome = "filed";

    /// <summary>Nothing remained to file after the gate's drops.</summary>
    public const string NoneOutcome = "none";

    /// <summary>GitHub's clock and this host's may disagree; the marker scan looks this far before the resume.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public string Name => ActionName;

    /// <inheritdoc />
    public async ValueTask<Result<HostActionResult>> RunAsync(WorkflowRun run, ProcessNode node, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            return await FileAsync(run, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            LogFilingCancelled(logger, ex, run.Id);
            return Failed("filing was cancelled without the dispatch being cancelled, as a timeout does; the server log has the details");
        }
    }

    /// <summary>The repository and number of a github.com pull request link, and nothing else.</summary>
    public static bool TryParsePullRequest(string? url, out RepoRef repo, out int number)
    {
        repo = null!;
        number = 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;

        var owner = RepoRef.FromGitHubUrl(url);
        if (owner.IsFailure)
            return false;

        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4 || !string.Equals(segments[2], "pull", StringComparison.Ordinal)
            || !int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0)
            return false;

        repo = owner.Value;
        return true;
    }

    private bool IsAllowListed(RepoRef repo)
    {
        foreach (var configured in config.Repositories)
        {
            var allowed = RepoRef.FromGitHubUrl(configured.Remote);
            if (allowed.IsSuccess && string.Equals(allowed.Value.ToString(), repo.ToString(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async ValueTask<Result<HostActionResult>> FileAsync(WorkflowRun run, CancellationToken ct)
    {
        var prLink = run.Variables.GetValueOrDefault(ReviewHandoff.PrUrlKey) as string;
        if (!TryParsePullRequest(prLink, out var repo, out var prNumber))
            return Failed($"the run's '{ReviewHandoff.PrUrlKey}' is not a github.com pull request link, so there is nothing to file the findings against");

        var prUrl = new Uri(prLink!, UriKind.Absolute);
        if (!IsAllowListed(repo))
            return Failed($"repository '{repo}' is no longer allow-listed");

        if (run.LastResume is not { } resume)
            return Failed("the run was never resumed at its gate, so no human approved filing anything");

        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>();

        // All kinds, so a voided drop record is listed beside the drop it voids; ReadDropped pairs them.
        var records = await store.ListAsync(run.Id, kind: null, ct).ConfigureAwait(false);
        if (records is null)
            return Failed("the run record store answered with no record list");

        var visit = ApprovingReview.Read(records);
        if (visit.IsFailure)
            return Failed(visit.Error);
        if (visit.Value is not { Approved: true } approved)
            return Failed("no approving review is recorded for this run");

        var dropped = FindingRecords.ReadDropped(records);
        if (dropped.IsFailure)
            return Failed(dropped.Error);
        var filed = FindingRecords.ReadFiled(records);
        if (filed.IsFailure)
            return Failed(filed.Error);

        var remaining = approved.Deferred.Where(d => !dropped.Value.Ids.Contains(d.Id)).ToList();
        if (remaining.Count == 0)
            return Outcome(NoneOutcome);

        var reader = scope.ServiceProvider.GetRequiredService<IGitHubReader>();
        var writer = scope.ServiceProvider.GetRequiredService<IGitHubWriter>();

        var head = await reader.GetPullRequestHeadShaAsync(repo, prNumber, ct).ConfigureAwait(false);
        if (head.IsFailure)
            return Failed($"reading pull request #{prNumber} failed: {head.Error}");

        // Only what this token wrote counts as a marker: anyone else on a public repository can paste one (D2).
        var login = await reader.GetAuthenticatedLoginAsync(ct).ConfigureAwait(false);
        if (login.IsFailure)
            return Failed($"reading the authenticated GitHub account failed: {login.Error}");

        var scan = new MarkerScan(reader, repo, resume.At - ClockSkew, login.Value);
        var done = new Dictionary<string, FiledFinding>(filed.Value, StringComparer.Ordinal);

        foreach (var finding in remaining)
        {
            if (done.ContainsKey(finding.Id))
                continue;

            var one = await FileOneAsync(run.Id, repo, head.Value, prUrl, finding, reader, writer, scan, ct).ConfigureAwait(false);
            if (one.IsFailure)
                return Failed($"filing finding '{finding.Id}' failed: {one.Error}");

            await AppendAsync(store, run, resume.By.Id, one.Value, ct).ConfigureAwait(false);
            done[finding.Id] = one.Value;
        }

        if (!done.ContainsKey(FindingRecords.SummaryId))
        {
            var marker = DeferredFindingText.Marker(run.Id, FindingRecords.SummaryId);
            var seen = await scan.FindCommentAsync(prNumber, marker, ct).ConfigureAwait(false);
            if (seen.IsFailure)
                return Failed($"reading the pull request's comments failed: {seen.Error}");

            if (seen.Value is null)
            {
                var posted = await writer.CommentAsync(repo, prNumber, DeferredFindingText.Summary(run.Id, approved.Deferred, done, dropped.Value), ct).ConfigureAwait(false);
                if (posted.IsFailure)
                    return Failed($"posting the summary on pull request #{prNumber} failed: {posted.Error}");
            }

            await AppendAsync(store, run, resume.By.Id, new FiledFinding(FindingRecords.SummaryId, FindingRecords.SummaryMode, prNumber, prUrl), ct).ConfigureAwait(false);
        }

        return Outcome(FiledOutcome);
    }

    private static async ValueTask<Result<FiledFinding>> FileOneAsync(
        Guid runId, RepoRef repo, string headSha, Uri prUrl, IdentifiedDeferredFinding finding,
        IGitHubReader reader, IGitHubWriter writer, MarkerScan scan, CancellationToken ct)
    {
        var marker = DeferredFindingText.Marker(runId, finding.Id);
        string? notUsed = null;

        if (finding.Finding.ExistingIssue is { } number)
        {
            var existing = await reader.GetIssueAsync(repo, number, ct).ConfigureAwait(false);
            if (existing.IsFailure)
                return Result<FiledFinding>.Failure($"reading #{number} failed: {existing.Error}");

            if (existing.Value is { IsPullRequest: false } issue)
            {
                // Looked at before the issue's state is: a comment this run already wrote on an issue that has been
                // closed since is found, not answered with a duplicate new issue.
                var seen = await scan.FindCommentAsync(number, marker, ct).ConfigureAwait(false);
                if (seen.IsFailure)
                    return Result<FiledFinding>.Failure(seen.Error);
                if (seen.Value is not null)
                    return Result<FiledFinding>.Success(new FiledFinding(finding.Id, FindingRecords.CommentedMode, number, issue.HtmlUrl));
            }

            notUsed = existing.Value switch
            {
                null => $"#{number} does not exist",
                { IsPullRequest: true } => $"#{number} is a pull request, not an issue",
                { State: var state } when !string.Equals(state, "open", StringComparison.OrdinalIgnoreCase) => $"#{number} is {state}",
                _ => null,
            };

            if (notUsed is null)
            {
                var posted = await writer.CommentAsync(repo, number, DeferredFindingText.IssueComment(repo, headSha, prUrl, runId, finding), ct).ConfigureAwait(false);
                return posted.IsFailure
                    ? Result<FiledFinding>.Failure(posted.Error)
                    : Result<FiledFinding>.Success(new FiledFinding(finding.Id, FindingRecords.CommentedMode, number, existing.Value!.HtmlUrl));
            }
        }

        var earlier = await scan.FindIssueAsync(marker, ct).ConfigureAwait(false);
        if (earlier.IsFailure)
            return Result<FiledFinding>.Failure(earlier.Error);
        if (earlier.Value is { } found)
            return Result<FiledFinding>.Success(new FiledFinding(finding.Id, FindingRecords.CreatedMode, found.Number, found.HtmlUrl));

        var created = await writer.CreateIssueAsync(repo, DeferredFindingText.Line(finding.Finding.Title), DeferredFindingText.IssueBody(repo, headSha, prUrl, runId, finding, notUsed), ct).ConfigureAwait(false);
        return created.IsSuccess
            ? Result<FiledFinding>.Success(new FiledFinding(finding.Id, FindingRecords.CreatedMode, created.Value.Number, created.Value.HtmlUrl))
            : Result<FiledFinding>.Failure(created.Error);
    }

    private async ValueTask AppendAsync(IWorkflowRunRecordStore store, WorkflowRun run, string principal, FiledFinding filed, CancellationToken ct)
    {
        // Every field is host-built and bounded, so a validation failure here is a bug, not an expected outcome.
        var record = WorkflowRunRecord.Create(
            run.Id, run.CurrentSeq, run.CurrentNode, WorkflowRunRecord.FindingFiledKind, principal, run.StartedBy?.Id,
            FindingRecords.FiledPayload(filed), clock.GetUtcNow().UtcDateTime);
        if (record.IsFailure)
            throw new InvalidOperationException($"A finding-filed record was refused: {record.Error}");

        await store.AppendAsync(record.Value, ct).ConfigureAwait(false);
    }

    private static Result<HostActionResult> Outcome(string outcome) =>
        Result<HostActionResult>.Success(new HostActionResult(outcome, new Dictionary<string, object?>(StringComparer.Ordinal)));

    private static Result<HostActionResult> Failed(string what) => Result<HostActionResult>.Failure(what);

    [LoggerMessage(Level = LogLevel.Error, Message = "Filing the review findings of run {RunId} was cancelled without its dispatch being cancelled")]
    private static partial void LogFilingCancelled(ILogger logger, Exception exception, Guid runId);

    /// <summary>Reads each listing at most once per call, since every finding of one run scans the same window.</summary>
    private sealed class MarkerScan(IGitHubReader reader, RepoRef repo, DateTimeOffset since, string login)
    {
        private readonly Dictionary<int, IReadOnlyList<IssueCommentText>> _comments = [];
        private IReadOnlyList<IssueText>? _issues;

        public async ValueTask<Result<IssueText?>> FindIssueAsync(string marker, CancellationToken ct)
        {
            if (_issues is null)
            {
                var listed = await reader.ListIssuesUpdatedSinceAsync(repo, since, ct).ConfigureAwait(false);
                if (listed.IsFailure)
                    return Result<IssueText?>.Failure(listed.Error);
                _issues = listed.Value;
            }

            return Result<IssueText?>.Success(_issues.FirstOrDefault(i => !i.IsPullRequest && Ours(i.AuthorLogin) && EndsWithMarker(i.Body, marker)));
        }

        public async ValueTask<Result<IssueCommentText?>> FindCommentAsync(int number, string marker, CancellationToken ct)
        {
            if (!_comments.TryGetValue(number, out var comments))
            {
                var listed = await reader.ListIssueCommentsSinceAsync(repo, number, since, ct).ConfigureAwait(false);
                if (listed.IsFailure)
                    return Result<IssueCommentText?>.Failure(listed.Error);
                _comments[number] = comments = listed.Value;
            }

            return Result<IssueCommentText?>.Success(comments.FirstOrDefault(c => Ours(c.AuthorLogin) && EndsWithMarker(c.Body, marker)));
        }

        private bool Ours(string author) => string.Equals(author, login, StringComparison.OrdinalIgnoreCase);

        /// <summary>The host appends the marker as the last line, and escapes <c>&lt;!--</c> in everything a model wrote, so a marker anywhere else is not ours.</summary>
        private static bool EndsWithMarker(string? body, string marker) =>
            (body ?? "").TrimEnd().EndsWith(marker, StringComparison.Ordinal);
    }
}
