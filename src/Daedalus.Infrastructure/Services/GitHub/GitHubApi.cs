using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;
using ZeroAlloc.Rest.SystemTextJson;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>
///     The one class that knows GitHub's HTTP surface. It reads repository activity and issues, writes for interactive
///     agents and post-gate host actions, and is the one place that opens a pull request on GitHub, for
///     <c>Daedalus.Infrastructure.Services.CodeAnalysis.GitHubPullRequestFactory</c>.
/// </summary>
/// <remarks>
///     <para>
///     <b>Transport.</b> Two ZeroAlloc.Rest clients generated from <see cref="IGitHubRestApi"/>, over the named
///     <c>HttpClient</c>s <see cref="ReadClientName"/> and <see cref="WriteClientName"/>, whose pipelines
///     <c>InfrastructureServiceExtensions.AddGitHubApi</c> sets up. Everything that is not HTTP stays here: the activity
///     sweep, paging, the 404-to-null rule of <see cref="GetIssueAsync"/> and the checks on GitHub's answers.
///     </para>
///     <para>
///     <b>Authentication.</b> Every request is authenticated up front. An unauthenticated request to a private repository
///     404s and looks identical to a missing repository, so a missing token fails before anything is sent rather than
///     surfacing as a confusing category error later.
///     </para>
///     <para>
///     <b>Errors.</b> Every expected fault is a failed <see cref="GitHubError"/>, never an exception; only a cancellation of
///     the caller's own token propagates. No message copies GitHub's answer.
///     </para>
/// </remarks>
public sealed partial class GitHubApi : IGitHubReader, IGitHubWriter
{
    /// <summary>The named <c>HttpClient</c> for GETs.</summary>
    public const string ReadClientName = "GitHub.Read";

    /// <summary>The named <c>HttpClient</c> for POST and PATCH. Its pipeline never retries.</summary>
    public const string WriteClientName = "GitHub.Write";

    /// <summary>GitHub's own limit on an issue title.</summary>
    public const int MaxIssueTitleLength = 256;

    /// <summary>The most hits <see cref="SearchIssuesAsync"/> returns.</summary>
    public const int MaxSearchHits = 10;

    /// <summary>The most pages of 100 a <c>since</c> listing reads before it gives up rather than read further.</summary>
    public const int MaxScanPages = 10;

    private const int ScanPageSize = 100;

    private static readonly SystemTextJsonSerializer Serializer = new(GitHubJsonContext.Default);

    // The concrete generated type, not IGitHubRestApi: CA1859 asks for it, and nothing substitutes these fields.
    private readonly GitHubRestApiClient _reads;
    private readonly GitHubRestApiClient _writes;
    private readonly GitHubOptions _options;
    private readonly IGitHubTokenSource _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubApi> _logger;

    public GitHubApi(IHttpClientFactory httpClients, IOptions<GitHubOptions> options, IGitHubTokenSource tokens, TimeProvider clock, ILogger<GitHubApi> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClients);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _tokens = tokens;
        _clock = clock;
        _logger = logger;

        var mapper = new GitHubErrorMapper(clock);
        _reads = new GitHubRestApiClient(Rooted(httpClients.CreateClient(ReadClientName)), Serializer, mapper);
        _writes = new GitHubRestApiClient(Rooted(httpClients.CreateClient(WriteClientName)), Serializer, mapper);
    }

    private string UserAgent => _options.UserAgent;

    // ---- Reads -------------------------------------------------------------------------------------------------

    public Task<Result<string, GitHubError>> GetDefaultBranchAsync(RepoRef repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return ReadDefaultBranchAsync(repo, ct);
    }

    public async Task<Result<IssueDetail?, GitHubError>> GetIssueAsync(RepoRef repo, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0)
            return Result<IssueDetail?, GitHubError>.Failure(GitHubError.Refused("An issue number must be positive."));

        var read = await CallAsync((auth, c) => _reads.GetIssueAsync(repo.Owner, repo.Name, number, auth, UserAgent, c), ct).ConfigureAwait(false);
        if (read.IsFailure)
        {
            // A caller asking whether an issue exists is answered, not failed (ruling P10).
            return read.Error.Kind == GitHubErrorKind.NotFound
                ? Result<IssueDetail?, GitHubError>.Success(null)
                : Result<IssueDetail?, GitHubError>.Failure(read.Error);
        }

        return ToIssueDetail(read.Value) is { } detail
            ? Result<IssueDetail?, GitHubError>.Success(detail)
            : Result<IssueDetail?, GitHubError>.Failure(Unexpected("reading an issue"));
    }

    public async Task<Result<IReadOnlyList<IssueHit>, GitHubError>> SearchIssuesAsync(
        RepoRef repo, string query, IssueSearchState state, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(query))
            return Result<IReadOnlyList<IssueHit>, GitHubError>.Failure(GitHubError.Refused("A search needs some words to search for."));

        // A5: GitHub ORs repo: qualifiers, so one in the query would widen the search past this repository.
        if (ScopeQualifier().IsMatch(query))
        {
            return Result<IReadOnlyList<IssueHit>, GitHubError>.Failure(GitHubError.Refused(
                "The query may not name its own repo:, org:, user: or owner: qualifier; this search is always limited to the given repository."));
        }

        var stateQualifier = state switch
        {
            IssueSearchState.Open => "state:open ",
            IssueSearchState.Closed => "state:closed ",
            _ => "",
        };
        var q = $"repo:{repo} is:issue {stateQualifier}{query.Trim()}";
        var perPage = Math.Clamp(limit, 1, MaxSearchHits);

        var found = await CallAsync((auth, c) => _reads.SearchIssuesAsync(q, perPage, auth, UserAgent, c), ct).ConfigureAwait(false);
        if (found.IsFailure)
            return Result<IReadOnlyList<IssueHit>, GitHubError>.Failure(found.Error);

        var what = "searching issues";
        if (found.Value.Items is not { } items)
            return Result<IReadOnlyList<IssueHit>, GitHubError>.Failure(Unexpected(what));

        var repoSuffix = $"/repos/{repo.Owner}/{repo.Name}";
        var hits = new List<IssueHit>(perPage);
        foreach (var item in items)
        {
            if (hits.Count == perPage)
                break;
            if (IsPresent(item.PullRequest))
                continue;
            if (item.RepositoryUrl is not { } repositoryUrl)
                return Result<IReadOnlyList<IssueHit>, GitHubError>.Failure(Unexpected(what));
            if (!repositoryUrl.EndsWith(repoSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (item is not { Number: { } number, Title: { } title, State: { } itemState } || !TryWebUrl(item.HtmlUrl, out var link))
                return Result<IReadOnlyList<IssueHit>, GitHubError>.Failure(Unexpected(what));

            hits.Add(new IssueHit(number, title, itemState, link));
        }

        return Result<IReadOnlyList<IssueHit>, GitHubError>.Success(hits);
    }

    public Task<Result<IReadOnlyList<IssueText>, GitHubError>> ListIssuesUpdatedSinceAsync(RepoRef repo, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var from = FormatIso(since.UtcDateTime);

        return ScanAsync(
            (page, auth, c) => _reads.ListIssuesAsync(repo.Owner, repo.Name, from, "all", ScanPageSize, page, auth, UserAgent, c),
            "listing issues updated since a time",
            static dto => dto is { Number: { } number, User.Login: { } login } && TryWebUrl(dto.HtmlUrl, out var link)
                ? new IssueText(number, dto.Body, link, IsPresent(dto.PullRequest), login)
                : null,
            ct);
    }

    public Task<Result<IReadOnlyList<IssueCommentText>, GitHubError>> ListIssueCommentsSinceAsync(
        RepoRef repo, int number, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0)
            return Task.FromResult(Result<IReadOnlyList<IssueCommentText>, GitHubError>.Failure(GitHubError.Refused("An issue number must be positive.")));

        var from = FormatIso(since.UtcDateTime);
        return ScanAsync(
            (page, auth, c) => _reads.ListCommentsAsync(repo.Owner, repo.Name, number, from, ScanPageSize, page, auth, UserAgent, c),
            "listing an issue's comments",
            static dto => dto is { Id: { } id, User.Login: { } login } && TryWebUrl(dto.HtmlUrl, out var link)
                ? new IssueCommentText(id, dto.Body, link, login)
                : null,
            ct);
    }

    public async Task<Result<string, GitHubError>> GetPullRequestHeadShaAsync(RepoRef repo, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0)
            return Result<string, GitHubError>.Failure(GitHubError.Refused("A pull request number must be positive."));

        var read = await CallAsync((auth, c) => _reads.GetPullRequestAsync(repo.Owner, repo.Name, number, auth, UserAgent, c), ct).ConfigureAwait(false);
        if (read.IsFailure)
            return Result<string, GitHubError>.Failure(read.Error);

        return read.Value.Head?.Sha is { } sha
            ? Result<string, GitHubError>.Success(sha)
            : Result<string, GitHubError>.Failure(Unexpected("reading a pull request"));
    }

    public async Task<Result<string, GitHubError>> GetAuthenticatedLoginAsync(CancellationToken ct = default)
    {
        var read = await CallAsync((auth, c) => _reads.GetAuthenticatedUserAsync(auth, UserAgent, c), ct).ConfigureAwait(false);
        if (read.IsFailure)
            return Result<string, GitHubError>.Failure(read.Error);

        return read.Value.Login is { } login
            ? Result<string, GitHubError>.Success(login)
            : Result<string, GitHubError>.Failure(Unexpected("reading the authenticated GitHub account"));
    }

    /// <summary>
    ///     The open pull request whose head is <paramref name="headBranch"/> in <paramref name="repo"/>, or
    ///     <see langword="null"/> when none is open. GitHub's <c>head</c> filter takes <c>owner:branch</c>; the whole value
    ///     is URL-encoded, so a branch such as <c>manufacture/&lt;run-id&gt;</c> is sent as <c>manufacture%2F&lt;run-id&gt;</c>.
    /// </summary>
    public async Task<Result<PullRequestResult?, GitHubError>> FindOpenPullRequestAsync(RepoRef repo, string headBranch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(headBranch);

        var listed = await CallAsync(
            (auth, c) => _reads.ListPullRequestsAsync(repo.Owner, repo.Name, "open", $"{repo.Owner}:{headBranch}", null, null, null, auth, UserAgent, c),
            ct).ConfigureAwait(false);
        if (listed.IsFailure)
        {
            LogOpenPullRequestLookupFailed(_logger, listed.Error.Kind);
            return Result<PullRequestResult?, GitHubError>.Failure(listed.Error);
        }

        if (listed.Value.Count == 0)
            return Result<PullRequestResult?, GitHubError>.Success(null);

        var first = listed.Value[0];
        return first is { Number: { } number, HtmlUrl: { } webUrl }
            ? Result<PullRequestResult?, GitHubError>.Success(new PullRequestResult
            {
                PullRequestId = number.ToString(CultureInfo.InvariantCulture),
                PullRequestUrl = first.Url ?? string.Empty,
                WebUrl = webUrl,
                Status = PullRequestStatus.Open,
            })
            : Result<PullRequestResult?, GitHubError>.Failure(Unexpected("looking up an open pull request"));
    }

    public async Task<RepoActivity> GetActivityAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var windowEnd = _clock.GetUtcNow().UtcDateTime;

        // Each category stands alone: a dead category must not take the other four down with it.
        var commits = await ReadCommitsAsync(repo, sinceUtc, ct).ConfigureAwait(false);
        var merged = await ReadMergedPullRequestsAsync(repo, sinceUtc, ct).ConfigureAwait(false);
        var open = await ReadOpenPullRequestsAsync(repo, ct).ConfigureAwait(false);
        var issues = await ReadIssuesAsync(repo, sinceUtc, ct).ConfigureAwait(false);
        var failedRuns = await ReadFailedRunsAsync(repo, sinceUtc, ct).ConfigureAwait(false);

        return new RepoActivity(repo, sinceUtc, windowEnd, commits, merged, open, issues, failedRuns);
    }

    // ---- Writes: sent once, through the write client ------------------------------------------------------------

    public Task<Result<string, GitHubError>> CommentAsync(RepoRef repo, int number, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return WriteAsync(
            (auth, c) => _writes.CreateCommentAsync(repo.Owner, repo.Name, number, new GitHubCommentBody(body), auth, UserAgent, c),
            $"Comment posted to {repo}#{number}.",
            ct);
    }

    public Task<Result<string, GitHubError>> AddLabelAsync(RepoRef repo, int number, string label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return WriteAsync(
            (auth, c) => _writes.AddLabelsAsync(repo.Owner, repo.Name, number, new GitHubLabelsBody([label]), auth, UserAgent, c),
            $"Label '{label}' added to {repo}#{number}.",
            ct);
    }

    public Task<Result<string, GitHubError>> CloseIssueAsync(RepoRef repo, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return WriteAsync(
            (auth, c) => _writes.UpdateIssueStateAsync(repo.Owner, repo.Name, number, new GitHubIssueStateBody("closed"), auth, UserAgent, c),
            $"{repo}#{number} closed.",
            ct);
    }

    /// <summary>
    ///     Files an issue and returns what GitHub assigned. Sent once; a timeout is a failure. An issue created before the
    ///     timeout is never found by retrying here: the <c>file-review-findings</c> host action scans for its own marker
    ///     before it files again, while any other caller, such as the <c>repoaction__create_issue</c> tool, gets a failure
    ///     that may hide an issue that was in fact created.
    /// </summary>
    public async Task<Result<CreatedIssue, GitHubError>> CreateIssueAsync(RepoRef repo, string title, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxIssueTitleLength)
            return Result<CreatedIssue, GitHubError>.Failure(GitHubError.Refused($"An issue title must be 1 to {MaxIssueTitleLength} characters."));

        var created = await CallAsync(
            (auth, c) => _writes.CreateIssueAsync(repo.Owner, repo.Name, new GitHubCreateIssueBody(title, body), auth, UserAgent, c),
            ct).ConfigureAwait(false);
        if (created.IsFailure)
            return Result<CreatedIssue, GitHubError>.Failure(created.Error.ForWrite());

        return created.Value is { Number: { } number } && TryWebUrl(created.Value.HtmlUrl, out var link)
            ? Result<CreatedIssue, GitHubError>.Success(new CreatedIssue(number, link))
            : Result<CreatedIssue, GitHubError>.Failure(Unexpected("filing an issue").ForWrite());
    }

    /// <summary>Opens a pull request and returns the number GitHub assigned and both its API and web URLs. Sent once.</summary>
    public async Task<Result<PullRequestResult, GitHubError>> CreatePullRequestAsync(
        RepoRef repo, string featureBranch, string baseBranch, string title, string description, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var body = new GitHubCreatePullRequestBody(title, description, featureBranch, baseBranch, Draft: false);
        var created = await CallAsync((auth, c) => _writes.CreatePullRequestAsync(repo.Owner, repo.Name, body, auth, UserAgent, c), ct).ConfigureAwait(false);
        if (created.IsFailure)
            return Result<PullRequestResult, GitHubError>.Failure(created.Error.ForWrite());

        return created.Value is { Number: { } number, Url: { } apiUrl, HtmlUrl: { } webUrl }
            ? Result<PullRequestResult, GitHubError>.Success(new PullRequestResult
            {
                PullRequestId = number.ToString(CultureInfo.InvariantCulture),
                PullRequestUrl = apiUrl,
                WebUrl = webUrl,
                Status = PullRequestStatus.Open,
            })
            : Result<PullRequestResult, GitHubError>.Failure(Unexpected("opening a pull request").ForWrite());
    }

    // ---- The digest's categories --------------------------------------------------------------------------------

    private async Task<Result<string, GitHubError>> ReadDefaultBranchAsync(RepoRef repo, CancellationToken ct)
    {
        var read = await CallAsync((auth, c) => _reads.GetRepositoryAsync(repo.Owner, repo.Name, auth, UserAgent, c), ct).ConfigureAwait(false);
        if (read.IsFailure)
            return Result<string, GitHubError>.Failure(read.Error);

        // No guessing "main": a caller may name any repository, and a wrong assumed branch would make the commits query
        // silently return nothing rather than surface as the error this actually is.
        return string.IsNullOrWhiteSpace(read.Value.DefaultBranch)
            ? Result<string, GitHubError>.Failure(GitHubError.Unreadable("GitHub did not report a default branch for this repository."))
            : Result<string, GitHubError>.Success(read.Value.DefaultBranch);
    }

    private async Task<CategoryResult<CommitSummary>> ReadCommitsAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct)
    {
        var branch = await ReadDefaultBranchAsync(repo, ct).ConfigureAwait(false);
        if (branch.IsFailure)
            return Unavailable<CommitSummary>("commits", branch.Error);

        var listed = await CallAsync(
            (auth, c) => _reads.ListCommitsAsync(repo.Owner, repo.Name, branch.Value, FormatIso(sinceUtc), _options.MaxItemsPerCategory, auth, UserAgent, c),
            ct).ConfigureAwait(false);
        if (listed.IsFailure)
            return Unavailable<CommitSummary>("commits", listed.Error);

        var items = new List<CommitSummary>(listed.Value.Count);
        foreach (var dto in listed.Value)
        {
            if (dto is not { Sha: { } sha, Commit.Message: { } message })
                return Unavailable<CommitSummary>("commits", Unexpected("listing commits"));

            var author = dto.Commit.Author;
            var committedAt = TryParseTimestamp(author?.Date, out var date) ? date : sinceUtc;
            items.Add(new CommitSummary(sha, message, author?.Name ?? "", committedAt));
        }

        return BuildCategoryResult(items, listed.Value.Count);
    }

    private async Task<CategoryResult<PullRequestSummary>> ReadMergedPullRequestsAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct)
    {
        var listed = await CallAsync(
            (auth, c) => _reads.ListPullRequestsAsync(repo.Owner, repo.Name, "closed", null, "updated", "desc", _options.MaxItemsPerCategory, auth, UserAgent, c),
            ct).ConfigureAwait(false);
        if (listed.IsFailure)
            return Unavailable<PullRequestSummary>("merged pull requests", listed.Error);

        var items = new List<PullRequestSummary>();
        foreach (var dto in listed.Value)
        {
            if (!TryParseTimestamp(dto.MergedAt, out var mergedAt) || mergedAt < sinceUtc)
                continue;
            if (ToSummary(dto, merged: true) is not { } summary)
                return Unavailable<PullRequestSummary>("merged pull requests", Unexpected("listing pull requests"));

            items.Add(summary);
        }

        return BuildCategoryResult(items, listed.Value.Count);
    }

    private async Task<CategoryResult<PullRequestSummary>> ReadOpenPullRequestsAsync(RepoRef repo, CancellationToken ct)
    {
        var listed = await CallAsync(
            (auth, c) => _reads.ListPullRequestsAsync(repo.Owner, repo.Name, "open", null, null, null, _options.MaxItemsPerCategory, auth, UserAgent, c),
            ct).ConfigureAwait(false);
        if (listed.IsFailure)
            return Unavailable<PullRequestSummary>("open pull requests", listed.Error);

        var items = new List<PullRequestSummary>(listed.Value.Count);
        foreach (var dto in listed.Value)
        {
            if (ToSummary(dto, merged: false) is not { } summary)
                return Unavailable<PullRequestSummary>("open pull requests", Unexpected("listing pull requests"));

            items.Add(summary);
        }

        return BuildCategoryResult(items, listed.Value.Count);
    }

    private async Task<CategoryResult<IssueSummary>> ReadIssuesAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct)
    {
        var listed = await CallAsync(
            (auth, c) => _reads.ListIssuesAsync(repo.Owner, repo.Name, FormatIso(sinceUtc), "all", _options.MaxItemsPerCategory, null, auth, UserAgent, c),
            ct).ConfigureAwait(false);
        if (listed.IsFailure)
            return Unavailable<IssueSummary>("issues", listed.Error);

        var items = new List<IssueSummary>();
        foreach (var dto in listed.Value)
        {
            // The /issues endpoint also returns pull requests; skip anything carrying that marker or the digest
            // double-counts pull requests as issues.
            if (IsPresent(dto.PullRequest))
                continue;
            if (dto is not { Number: { } number, Title: { } title })
                return Unavailable<IssueSummary>("issues", Unexpected("listing issues"));

            var updatedAt = TryParseTimestamp(dto.UpdatedAt, out var updated) ? updated : sinceUtc;
            items.Add(new IssueSummary(number, title, dto.State ?? "", updatedAt));
        }

        return BuildCategoryResult(items, listed.Value.Count);
    }

    private async Task<CategoryResult<WorkflowRunSummary>> ReadFailedRunsAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct)
    {
        var listed = await CallAsync(
            (auth, c) => _reads.ListWorkflowRunsAsync(repo.Owner, repo.Name, "failure", _options.MaxItemsPerCategory, auth, UserAgent, c),
            ct).ConfigureAwait(false);
        if (listed.IsFailure)
            return Unavailable<WorkflowRunSummary>("failed workflow runs", listed.Error);

        var runs = listed.Value.WorkflowRuns ?? [];
        var items = new List<WorkflowRunSummary>();
        foreach (var dto in runs)
        {
            var startedAt = TryParseTimestamp(dto.RunStartedAt, out var started) ? started : DateTime.MinValue;
            if (startedAt < sinceUtc)
                continue;
            if (dto.Id is not { } id)
                return Unavailable<WorkflowRunSummary>("failed workflow runs", Unexpected("listing workflow runs"));

            items.Add(new WorkflowRunSummary(id, dto.Name ?? "", dto.Conclusion ?? "", dto.HeadBranch ?? "", startedAt));
        }

        return BuildCategoryResult(items, runs.Count);
    }

    /// <summary>
    ///     Builds the category result. <paramref name="rawCount"/> is the length of the array GitHub returned before any
    ///     client-side filtering: truncation must be judged against what the page could have held, not against what
    ///     survived filtering, or a full page that filters down to a handful reports as complete.
    /// </summary>
    private CategoryResult<T> BuildCategoryResult<T>(List<T> items, int rawCount)
    {
        var truncated = rawCount >= _options.MaxItemsPerCategory;
        IReadOnlyList<T> taken = items.Count > _options.MaxItemsPerCategory
            ? items.Take(_options.MaxItemsPerCategory).ToList()
            : items;

        return CategoryResult<T>.Ok(taken, truncated);
    }

    private CategoryResult<T> Unavailable<T>(string category, GitHubError error)
    {
        LogCategoryUnavailable(_logger, category, error.Kind);
        return CategoryResult<T>.Failed(error.Message);
    }

    // ---- Transport helpers --------------------------------------------------------------------------------------

    /// <summary>
    ///     Reads up to <see cref="MaxScanPages"/> pages of 100 and stops at the first short page. More than that is a
    ///     failure rather than a silent cut, because a marker scan that stopped early would report "not found" for
    ///     something that exists. <paramref name="read"/> returns <see langword="null"/> for an item missing a field.
    /// </summary>
    private async Task<Result<IReadOnlyList<T>, GitHubError>> ScanAsync<TDto, T>(
        Func<int, string, CancellationToken, Task<Result<IReadOnlyList<TDto>, GitHubError>>> readPage,
        string what,
        Func<TDto, T?> read,
        CancellationToken ct)
        where T : class
    {
        var found = new List<T>();
        for (var page = 1; page <= MaxScanPages; page++)
        {
            var current = page;
            var listed = await CallAsync((auth, c) => readPage(current, auth, c), ct).ConfigureAwait(false);
            if (listed.IsFailure)
                return Result<IReadOnlyList<T>, GitHubError>.Failure(listed.Error);

            foreach (var dto in listed.Value)
            {
                if (read(dto) is not { } item)
                    return Result<IReadOnlyList<T>, GitHubError>.Failure(Unexpected(what));

                found.Add(item);
            }

            if (listed.Value.Count < ScanPageSize)
                return Result<IReadOnlyList<T>, GitHubError>.Success(found);
        }

        return Result<IReadOnlyList<T>, GitHubError>.Failure(GitHubError.Refused(
            $"{what}: the scan read the {MaxScanPages * ScanPageSize}-item cap and stops rather than read further."));
    }

    /// <summary>
    ///     Every call with an answer goes through here: the token first, so a missing one sends nothing, then the call. A
    ///     Polly rejection, which no generated client maps, becomes a <see cref="GitHubError"/> too (ruling P13).
    /// </summary>
    private async Task<Result<T, GitHubError>> CallAsync<T>(
        Func<string, CancellationToken, Task<Result<T, GitHubError>>> call, CancellationToken ct)
    {
        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<T, GitHubError>.Failure(GitHubError.NoToken(token.Error));

        try
        {
            return await call("Bearer " + token.Value, ct).ConfigureAwait(false);
        }
        catch (TimeoutRejectedException)
        {
            return Result<T, GitHubError>.Failure(GitHubError.TimedOut());
        }
        catch (ExecutionRejectedException ex)
        {
            LogRejected(_logger, ex.GetType().Name);
            return Result<T, GitHubError>.Failure(GitHubError.Rejected());
        }
    }

    /// <summary>As <see cref="CallAsync{T}"/>, for a write without an answer; success is <paramref name="confirmation"/>.</summary>
    private async Task<Result<string, GitHubError>> WriteAsync(
        Func<string, CancellationToken, Task<UnitResult<GitHubError>>> send, string confirmation, CancellationToken ct)
    {
        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<string, GitHubError>.Failure(GitHubError.NoToken(token.Error));

        UnitResult<GitHubError> sent;
        try
        {
            sent = await send("Bearer " + token.Value, ct).ConfigureAwait(false);
        }
        catch (TimeoutRejectedException)
        {
            return Result<string, GitHubError>.Failure(GitHubError.TimedOut().ForWrite());
        }
        catch (ExecutionRejectedException ex)
        {
            LogRejected(_logger, ex.GetType().Name);
            // A rejection is raised before the request goes out, so nothing can have landed: no ForWrite hint.
            return Result<string, GitHubError>.Failure(GitHubError.Rejected());
        }

        return sent.IsSuccess
            ? Result<string, GitHubError>.Success(confirmation)
            : Result<string, GitHubError>.Failure(sent.Error.ForWrite());
    }

    /// <summary>
    ///     <see cref="IHttpClientFactory.CreateClient"/> returns a new <c>HttpClient</c> per call, so setting its base address
    ///     here, before anything is sent, changes nothing shared. The trailing slash keeps a GitHub Enterprise path such as
    ///     <c>/api/v3</c>, since every route is relative.
    /// </summary>
    private HttpClient Rooted(HttpClient http)
    {
        // Interpolated, not concatenated: Sonar S1075 reads a "/" or '/' operand of + as a hard-coded path delimiter.
        http.BaseAddress = new Uri($"{_options.ApiUrl.TrimEnd('/')}/", UriKind.Absolute);
        return http;
    }

    // ---- Mapping GitHub's answers -------------------------------------------------------------------------------

    private static IssueDetail? ToIssueDetail(GitHubIssueDto dto)
    {
        if (dto is not { Number: { } number, Title: { } title, State: { } state } || !TryWebUrl(dto.HtmlUrl, out var link))
            return null;

        var labels = new List<string>();
        foreach (var label in dto.Labels ?? [])
        {
            if (label?.Name is not { } name)
                return null;

            labels.Add(name);
        }

        return new IssueDetail(number, title, state, dto.Body, labels, link, IsPresent(dto.PullRequest));
    }

    private static PullRequestSummary? ToSummary(GitHubPullRequestDto dto, bool merged)
    {
        if (dto is not { Number: { } number, Title: { } title })
            return null;

        var updatedAt = TryParseTimestamp(dto.UpdatedAt, out var updated) ? updated : default;
        return new PullRequestSummary(number, title, dto.User?.Login ?? "", updatedAt, merged);
    }

    /// <summary><c>pull_request</c> marks a pull request whether it holds an object or <c>null</c>; only its absence marks an issue.</summary>
    private static bool IsPresent(JsonElement marker) => marker.ValueKind != JsonValueKind.Undefined;

    private static bool TryWebUrl(string? value, [NotNullWhen(true)] out Uri? link) =>
        Uri.TryCreate(value, UriKind.Absolute, out link);

    private static bool TryParseTimestamp(string? value, out DateTime utc)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }

        utc = default;
        return false;
    }

    private static GitHubError Unexpected(string what) =>
        GitHubError.Unreadable($"GitHub's answer to {what} was not in the expected shape.");

    private static string FormatIso(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?:^|[\s(""])-?(?:repo|org|user|owner):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScopeQualifier();

    [LoggerMessage(EventId = 510, Level = LogLevel.Warning, Message = "Looking up an open pull request failed: {Kind}")]
    private static partial void LogOpenPullRequestLookupFailed(ILogger logger, GitHubErrorKind kind);

    [LoggerMessage(EventId = 511, Level = LogLevel.Warning, Message = "GitHub read failed in category {Category}: {Kind}")]
    private static partial void LogCategoryUnavailable(ILogger logger, string category, GitHubErrorKind kind);

    [LoggerMessage(EventId = 512, Level = LogLevel.Warning, Message = "The resilience pipeline refused a GitHub request: {Rejection}")]
    private static partial void LogRejected(ILogger logger, string rejection);
}
