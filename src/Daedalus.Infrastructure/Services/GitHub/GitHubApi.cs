#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute — JsonSerializer.Serialize
// over small anonymous write payloads; accepted risk, matching GitHubPullRequestFactory
// and AzureDevOpsPullRequestFactory elsewhere in this project.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>
///     Reads repository activity straight from the GitHub REST API, and — for interactive agents only — acts on it.
///     Also the one place that opens a pull request on GitHub: <see cref="CreatePullRequestAsync"/> serves the
///     non-interactive Ralph Loop and workspace orchestrators through
///     <c>Daedalus.Infrastructure.Services.CodeAnalysis.GitHubPullRequestFactory</c>, which owns URL parsing and
///     keeps its place behind <c>IPullRequestFactory</c>'s platform dispatch but no longer speaks HTTP itself.
///     Every request is authenticated up front — an unauthenticated request to a private repository 404s and looks
///     identical to a missing repository, so a missing token fails before anything is sent rather than surfacing as
///     a confusing category error later. Writes carry no retry: a failure is returned to the caller as-is, because a
///     retried comment or label is a visible action taken twice.
/// </summary>
public sealed partial class GitHubApi : IGitHubReader, IGitHubWriter
{
    private const string ApiVersion = "2022-11-28";

    private readonly HttpClient _http;
    private readonly GitHubOptions _options;
    private readonly IGitHubTokenSource _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubApi> _logger;

    public GitHubApi(HttpClient http, IOptions<GitHubOptions> options, IGitHubTokenSource tokens, TimeProvider clock, ILogger<GitHubApi> logger)
    {
        _http = http;
        _options = options.Value;
        _tokens = tokens;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken ct = default)
    {
        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<string>.Failure(token.Error);

        try
        {
            var branch = await FetchDefaultBranchAsync(repo, token.Value, ct).ConfigureAwait(false);
            return Result<string>.Success(branch);
        }
        catch (GitHubRequestException ex)
        {
            return Result<string>.Failure(ex.Message);
        }
    }

    public async Task<Result<string>> CommentAsync(RepoRef repo, int number, string body, CancellationToken ct = default)
    {
        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<string>.Failure(token.Error);

        var payload = JsonSerializer.Serialize(new { body });
        return await SendWriteAsync(
            HttpMethod.Post, $"{IssueUrl(repo, number)}/comments", payload, token.Value,
            $"Comment posted to {repo}#{number}.", ct).ConfigureAwait(false);
    }

    public async Task<Result<string>> AddLabelAsync(RepoRef repo, int number, string label, CancellationToken ct = default)
    {
        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<string>.Failure(token.Error);

        var payload = JsonSerializer.Serialize(new { labels = new[] { label } });
        return await SendWriteAsync(
            HttpMethod.Post, $"{IssueUrl(repo, number)}/labels", payload, token.Value,
            $"Label '{label}' added to {repo}#{number}.", ct).ConfigureAwait(false);
    }

    public async Task<Result<string>> CloseIssueAsync(RepoRef repo, int number, CancellationToken ct = default)
    {
        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<string>.Failure(token.Error);

        var payload = JsonSerializer.Serialize(new { state = "closed" });
        return await SendWriteAsync(
            HttpMethod.Patch, IssueUrl(repo, number), payload, token.Value,
            $"{repo}#{number} closed.", ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Opens a pull request. Unlike the other writes, a successful response body is not a fixed confirmation
    ///     string — the caller needs the number GitHub assigned and both the API and web URLs — so this does not
    ///     go through <see cref="SendWriteAsync"/>, which only ever returns the message it was given.
    /// </summary>
    public async Task<Result<PullRequestResult>> CreatePullRequestAsync(
        RepoRef repo, string featureBranch, string baseBranch, string title, string description, CancellationToken ct = default)
    {
        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<PullRequestResult>.Failure(token.Error);

        var payload = JsonSerializer.Serialize(new { title, body = description, head = featureBranch, @base = baseBranch, draft = false });

        // Not disposed here, matching SendWriteAsync: the stub handler in tests keeps this request around so a
        // test can inspect the body it sent, and disposing it would dispose that content out from under it.
        var request = new HttpRequestMessage(HttpMethod.Post, $"{RepoUrl(repo)}/pulls")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(request, token.Value);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return Result<PullRequestResult>.Failure(MapError(response, body));

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement;

        return Result<PullRequestResult>.Success(new PullRequestResult
        {
            PullRequestId = content.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture),
            PullRequestUrl = content.GetProperty("url").GetString() ?? string.Empty,
            WebUrl = content.GetProperty("html_url").GetString() ?? string.Empty,
            Status = PullRequestStatus.Open,
        });
    }

    /// <summary>GitHub's own limit on an issue title.</summary>
    public const int MaxIssueTitleLength = 256;

    /// <summary>
    ///     Files an issue. Like <see cref="CreatePullRequestAsync"/> it returns what GitHub assigned rather than a fixed
    ///     confirmation. No retry, and a timeout is a failure: an issue that was created before the timeout is found
    ///     again by the caller's marker scan, never by retrying here.
    /// </summary>
    public async Task<Result<CreatedIssue>> CreateIssueAsync(RepoRef repo, string title, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxIssueTitleLength)
            return Result<CreatedIssue>.Failure($"An issue title must be 1 to {MaxIssueTitleLength} characters.");

        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<CreatedIssue>.Failure(token.Error);

        var payload = JsonSerializer.Serialize(new { title, body });

        // Not disposed, matching SendWriteAsync: the test stub keeps the request so a test can read its body.
        var request = new HttpRequestMessage(HttpMethod.Post, $"{RepoUrl(repo)}/issues")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(request, token.Value);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<CreatedIssue>.Failure(MapError(response, text));

            using var doc = JsonDocument.Parse(text);
            return ReadShape($"filing an issue in {repo}", () => new CreatedIssue(
                doc.RootElement.GetProperty("number").GetInt32(),
                HtmlUrl(doc.RootElement)));
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Result<CreatedIssue>.Failure($"Filing an issue in {repo} timed out: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            LogReadFailed(_logger, ex, $"filing an issue in {repo}");
            return Result<CreatedIssue>.Failure($"Filing an issue in {repo} failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     The open pull request whose head is <paramref name="headBranch"/> in <paramref name="repo"/>, or
    ///     <see langword="null"/> when none is open. GitHub's <c>head</c> filter takes <c>owner:branch</c>; the whole
    ///     value is URL-encoded, so a branch such as <c>manufacture/&lt;run-id&gt;</c> is sent as
    ///     <c>manufacture%2F&lt;run-id&gt;</c> rather than splitting the query.
    /// </summary>
    /// <remarks>
    ///     Every expected fault is a failed result, never an exception: a network or DNS fault, a timeout of the
    ///     client's own, a body that is not JSON (a proxy's HTML page on a 200), and an element missing
    ///     <c>number</c> or <c>html_url</c>. Only a cancellation of <paramref name="ct"/> itself propagates.
    /// </remarks>
    public async Task<Result<PullRequestResult?>> FindOpenPullRequestAsync(RepoRef repo, string headBranch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(headBranch);

        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<PullRequestResult?>.Failure(token.Error);

        try
        {
            return await QueryOpenPullRequestAsync(repo, headBranch, token.Value, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Result<PullRequestResult?>.Failure($"Looking up an open pull request on GitHub timed out: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or FormatException)
        {
            LogOpenPullRequestLookupFailed(_logger, ex, repo.ToString());
            return Result<PullRequestResult?>.Failure($"Looking up an open pull request on GitHub failed: {ex.Message}");
        }
    }

    private async Task<Result<PullRequestResult?>> QueryOpenPullRequestAsync(
        RepoRef repo, string headBranch, string token, CancellationToken ct)
    {
        var url = $"{RepoUrl(repo)}/pulls?state=open&head={Uri.EscapeDataString($"{repo.Owner}:{headBranch}")}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return Result<PullRequestResult?>.Failure(MapError(response, body));

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Result<PullRequestResult?>.Failure($"GitHub returned {doc.RootElement.ValueKind} for a pull request list, not an array.");

        if (doc.RootElement.GetArrayLength() == 0)
            return Result<PullRequestResult?>.Success(null);

        var content = doc.RootElement[0];
        return Result<PullRequestResult?>.Success(new PullRequestResult
        {
            PullRequestId = content.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture),
            PullRequestUrl = content.TryGetProperty("url", out var apiUrl) ? apiUrl.GetString() ?? string.Empty : string.Empty,
            WebUrl = content.GetProperty("html_url").GetString() ?? string.Empty,
            Status = PullRequestStatus.Open,
        });
    }

    /// <summary>
    ///     Sends a single write request and returns whatever GitHub said, mapped through the same error helper the
    ///     read side uses. There is no retry here — this method is called exactly once per public write method, and
    ///     a non-success response returns a failure directly rather than looping or re-queuing.
    /// </summary>
    private async Task<Result<string>> SendWriteAsync(
        HttpMethod method, string url, string jsonPayload, string token, string successMessage, CancellationToken ct)
    {
        // Not disposed here (unlike the read path's request): the stub handler in tests keeps this request around
        // so a test can inspect the body it sent, and disposing it would dispose that content out from under it.
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(request, token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? Result<string>.Success(successMessage)
            : Result<string>.Failure(MapError(response, body));
    }

    public async Task<RepoActivity> GetActivityAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct = default)
    {
        var windowEnd = _clock.GetUtcNow().UtcDateTime;
        var token = _tokens.GetToken();

        if (token.IsFailure)
        {
            return new RepoActivity(
                repo,
                sinceUtc,
                windowEnd,
                CategoryResult<CommitSummary>.Failed(token.Error),
                CategoryResult<PullRequestSummary>.Failed(token.Error),
                CategoryResult<PullRequestSummary>.Failed(token.Error),
                CategoryResult<IssueSummary>.Failed(token.Error),
                CategoryResult<WorkflowRunSummary>.Failed(token.Error));
        }

        var accessToken = token.Value;

        // Each category is wrapped independently: a dead category must not take the other four down with it.
        var commits = await RunCategoryAsync("commits", repo, async () =>
        {
            var branch = await FetchDefaultBranchAsync(repo, accessToken, ct).ConfigureAwait(false);
            return await GetCommitsAsync(repo, accessToken, branch, sinceUtc, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var merged = await RunCategoryAsync("merged pull requests", repo,
            () => GetMergedPullRequestsAsync(repo, accessToken, sinceUtc, ct)).ConfigureAwait(false);

        var open = await RunCategoryAsync("open pull requests", repo,
            () => GetOpenPullRequestsAsync(repo, accessToken, ct)).ConfigureAwait(false);

        var issues = await RunCategoryAsync("issues", repo,
            () => GetIssuesAsync(repo, accessToken, sinceUtc, ct)).ConfigureAwait(false);

        var failedRuns = await RunCategoryAsync("failed workflow runs", repo,
            () => GetFailedRunsAsync(repo, accessToken, sinceUtc, ct)).ConfigureAwait(false);

        return new RepoActivity(repo, sinceUtc, windowEnd, commits, merged, open, issues, failedRuns);
    }

    private async Task<CategoryResult<T>> RunCategoryAsync<T>(string category, RepoRef repo, Func<Task<CategoryResult<T>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled request is a caller decision, not a GitHub problem — let it propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("GitHub read failed for {Repo} in category {Category}", repo, category);
            return CategoryResult<T>.Failed(ex.Message);
        }
    }

    private async Task<string> FetchDefaultBranchAsync(RepoRef repo, string token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(RepoUrl(repo), token, ct).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("default_branch", out var branchEl) && branchEl.ValueKind == JsonValueKind.String)
        {
            var branch = branchEl.GetString();
            if (!string.IsNullOrWhiteSpace(branch))
                return branch;
        }

        // No guessing "main": a caller may name any repository, and a wrong assumed branch would make the
        // commits query silently return nothing rather than surface as the error this actually is.
        throw new GitHubRequestException("GitHub did not report a default branch for this repository.");
    }

    private async Task<CategoryResult<CommitSummary>> GetCommitsAsync(
        RepoRef repo, string token, string branch, DateTime sinceUtc, CancellationToken ct)
    {
        var url = $"{RepoUrl(repo)}/commits?sha={Uri.EscapeDataString(branch)}&since={Uri.EscapeDataString(FormatIso(sinceUtc))}" +
                  $"&per_page={_options.MaxItemsPerCategory}";

        using var doc = await GetJsonAsync(url, token, ct).ConfigureAwait(false);
        var rawCount = doc.RootElement.GetArrayLength();

        var items = new List<CommitSummary>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var sha = element.GetProperty("sha").GetString() ?? "";
            var commit = element.GetProperty("commit");
            var message = commit.GetProperty("message").GetString() ?? "";

            var author = commit.TryGetProperty("author", out var authorEl) ? authorEl : default;
            var authorName = author.ValueKind == JsonValueKind.Object && author.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString() ?? ""
                : "";
            var committedAt = author.ValueKind == JsonValueKind.Object
                               && author.TryGetProperty("date", out var dateEl)
                               && dateEl.TryGetDateTime(out var parsedDate)
                ? parsedDate.ToUniversalTime()
                : sinceUtc;

            items.Add(new CommitSummary(sha, message, authorName, committedAt));
        }

        return BuildCategoryResult(items, rawCount);
    }

    private async Task<CategoryResult<PullRequestSummary>> GetMergedPullRequestsAsync(
        RepoRef repo, string token, DateTime sinceUtc, CancellationToken ct)
    {
        var url = $"{RepoUrl(repo)}/pulls?state=closed&sort=updated&direction=desc&per_page={_options.MaxItemsPerCategory}";
        using var doc = await GetJsonAsync(url, token, ct).ConfigureAwait(false);
        var rawCount = doc.RootElement.GetArrayLength();

        var items = new List<PullRequestSummary>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("merged_at", out var mergedAtEl) || mergedAtEl.ValueKind != JsonValueKind.String)
                continue;

            if (!mergedAtEl.TryGetDateTime(out var mergedAt) || mergedAt.ToUniversalTime() < sinceUtc)
                continue;

            items.Add(MapPullRequest(element, merged: true));
        }

        return BuildCategoryResult(items, rawCount);
    }

    private async Task<CategoryResult<PullRequestSummary>> GetOpenPullRequestsAsync(RepoRef repo, string token, CancellationToken ct)
    {
        var url = $"{RepoUrl(repo)}/pulls?state=open&per_page={_options.MaxItemsPerCategory}";
        using var doc = await GetJsonAsync(url, token, ct).ConfigureAwait(false);

        var items = doc.RootElement.EnumerateArray().Select(element => MapPullRequest(element, merged: false)).ToList();
        return BuildCategoryResult(items, doc.RootElement.GetArrayLength());
    }

    private async Task<CategoryResult<IssueSummary>> GetIssuesAsync(RepoRef repo, string token, DateTime sinceUtc, CancellationToken ct)
    {
        var url = $"{RepoUrl(repo)}/issues?since={Uri.EscapeDataString(FormatIso(sinceUtc))}&state=all&per_page={_options.MaxItemsPerCategory}";
        using var doc = await GetJsonAsync(url, token, ct).ConfigureAwait(false);
        var rawCount = doc.RootElement.GetArrayLength();

        var items = new List<IssueSummary>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            // The /issues endpoint also returns pull requests; skip anything carrying that marker or the digest
            // double-counts pull requests as issues.
            if (element.TryGetProperty("pull_request", out _))
                continue;

            var number = element.GetProperty("number").GetInt32();
            var title = element.GetProperty("title").GetString() ?? "";
            var state = element.TryGetProperty("state", out var stateEl) ? stateEl.GetString() ?? "" : "";
            var updatedAt = element.TryGetProperty("updated_at", out var updatedEl) && updatedEl.TryGetDateTime(out var updated)
                ? updated.ToUniversalTime()
                : sinceUtc;

            items.Add(new IssueSummary(number, title, state, updatedAt));
        }

        return BuildCategoryResult(items, rawCount);
    }

    private async Task<CategoryResult<WorkflowRunSummary>> GetFailedRunsAsync(
        RepoRef repo, string token, DateTime sinceUtc, CancellationToken ct)
    {
        var url = $"{RepoUrl(repo)}/actions/runs?status=failure&per_page={_options.MaxItemsPerCategory}";
        using var doc = await GetJsonAsync(url, token, ct).ConfigureAwait(false);

        var items = new List<WorkflowRunSummary>();
        var rawCount = 0;
        if (doc.RootElement.TryGetProperty("workflow_runs", out var runsEl) && runsEl.ValueKind == JsonValueKind.Array)
        {
            rawCount = runsEl.GetArrayLength();

            foreach (var element in runsEl.EnumerateArray())
            {
                var startedAt = element.TryGetProperty("run_started_at", out var startedEl) && startedEl.TryGetDateTime(out var started)
                    ? started.ToUniversalTime()
                    : DateTime.MinValue;

                if (startedAt < sinceUtc)
                    continue;

                var id = element.GetProperty("id").GetInt64();
                var name = element.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var conclusion = element.TryGetProperty("conclusion", out var conclusionEl) ? conclusionEl.GetString() ?? "" : "";
                var headBranch = element.TryGetProperty("head_branch", out var branchEl) ? branchEl.GetString() ?? "" : "";

                items.Add(new WorkflowRunSummary(id, name, conclusion, headBranch, startedAt));
            }
        }

        return BuildCategoryResult(items, rawCount);
    }

    private static PullRequestSummary MapPullRequest(JsonElement element, bool merged)
    {
        var number = element.GetProperty("number").GetInt32();
        var title = element.GetProperty("title").GetString() ?? "";
        var author = element.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object
                     && userEl.TryGetProperty("login", out var loginEl)
            ? loginEl.GetString() ?? ""
            : "";
        var updatedAt = element.TryGetProperty("updated_at", out var updatedEl) && updatedEl.TryGetDateTime(out var updated)
            ? updated.ToUniversalTime()
            : default;

        return new PullRequestSummary(number, title, author, updatedAt, merged);
    }

    /// <summary>
    ///     Builds the category result. <paramref name="rawCount"/> is the length of the array GitHub returned
    ///     before any client-side filtering — truncation must be judged against what the page could have held, not
    ///     against what survived filtering, or a full page that filters down to a handful reports as complete.
    /// </summary>
    private CategoryResult<T> BuildCategoryResult<T>(List<T> items, int rawCount)
    {
        var truncated = rawCount >= _options.MaxItemsPerCategory;
        IReadOnlyList<T> taken = items.Count > _options.MaxItemsPerCategory
            ? items.Take(_options.MaxItemsPerCategory).ToList()
            : items;

        return CategoryResult<T>.Ok(taken, truncated);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new GitHubRequestException(MapError(response, body));

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    /// <summary>
    ///     GETs <paramref name="url"/> and parses the body. 404 is a successful <see langword="null"/>, since a caller
    ///     asking whether an issue exists is answered, not failed. Every other expected fault is a failed result naming
    ///     <paramref name="what"/>: another non-success status, a network fault, a timeout of the client's own, and a
    ///     body that is not JSON. Only a cancellation of <paramref name="ct"/> itself propagates. The caller disposes
    ///     the document.
    /// </summary>
    private async Task<Result<JsonDocument?>> GetJsonAsync(string url, string token, string what, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request, token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Result<JsonDocument?>.Success(null);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? Result<JsonDocument?>.Success(JsonDocument.Parse(body))
                : Result<JsonDocument?>.Failure(MapError(response, body));
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Result<JsonDocument?>.Failure($"{what} on GitHub timed out: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            LogReadFailed(_logger, ex, what);
            return Result<JsonDocument?>.Failure($"{what} on GitHub failed: {ex.Message}");
        }
    }

    /// <summary>Headers GitHub requires on every request, read or write: the bearer token, User-Agent and API version.</summary>
    private void ApplyHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
    }

    private string RepoUrl(RepoRef repo) =>
        $"{_options.ApiUrl.TrimEnd('/')}/repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}";

    private string IssueUrl(RepoRef repo, int number) => $"{RepoUrl(repo)}/issues/{number}";

    private static string FormatIso(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string MapError(HttpResponseMessage response, string body)
    {
        var message = ExtractMessage(body);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return $"Repository does not exist, or the configured token does not have access to it. GitHub said: {message}";

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var isRateLimited = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
                                 && string.Equals(remaining.FirstOrDefault(), "0", StringComparison.Ordinal);

            if (isRateLimited)
            {
                var resetHeader = response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
                    ? resetValues.FirstOrDefault()
                    : null;

                if (resetHeader is not null && long.TryParse(resetHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetUnix))
                {
                    var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix).UtcDateTime;
                    return $"GitHub rate limit exceeded; it resets at {resetAt.ToString("u", CultureInfo.InvariantCulture)}.";
                }

                return "GitHub rate limit exceeded.";
            }

            return $"GitHub request forbidden. GitHub said: {message}";
        }

        return $"GitHub request failed with status {(int)response.StatusCode} {response.StatusCode}. GitHub said: {message}";
    }

    /// <summary>The most hits <see cref="SearchIssuesAsync"/> returns.</summary>
    public const int MaxSearchHits = 10;

    /// <summary>The most pages of 100 a <c>since</c> listing reads before it gives up rather than read further.</summary>
    public const int MaxScanPages = 10;

    public async Task<Result<IssueDetail?>> GetIssueAsync(RepoRef repo, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0)
            return Result<IssueDetail?>.Failure("An issue number must be positive.");

        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<IssueDetail?>.Failure(token.Error);

        var what = $"reading {repo}#{number}";
        var fetched = await GetJsonAsync(IssueUrl(repo, number), token.Value, what, ct).ConfigureAwait(false);
        if (fetched.IsFailure)
            return Result<IssueDetail?>.Failure(fetched.Error);
        if (fetched.Value is not { } doc)
            return Result<IssueDetail?>.Success(null);

        using (doc)
        {
            return ReadShape<IssueDetail?>(what, () =>
            {
                var root = doc.RootElement;
                IReadOnlyList<string> labels = root.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                    ? [.. l.EnumerateArray().Select(e => e.GetProperty("name").GetString() ?? "")]
                    : [];
                return new IssueDetail(
                    root.GetProperty("number").GetInt32(),
                    root.GetProperty("title").GetString() ?? "",
                    root.GetProperty("state").GetString() ?? "",
                    root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null,
                    labels,
                    HtmlUrl(root),
                    root.TryGetProperty("pull_request", out _));
            });
        }
    }

    public async Task<Result<IReadOnlyList<IssueHit>>> SearchIssuesAsync(
        RepoRef repo, string query, IssueSearchState state, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(query))
            return Result<IReadOnlyList<IssueHit>>.Failure("A search needs some words to search for.");

        // A5: GitHub ORs repo: qualifiers, so one in the query would widen the search past this repository.
        if (ScopeQualifier().IsMatch(query))
        {
            return Result<IReadOnlyList<IssueHit>>.Failure(
                $"The query may not name its own repo:, org:, user: or owner: qualifier; this search is always limited to {repo}.");
        }

        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<IReadOnlyList<IssueHit>>.Failure(token.Error);

        var stateQualifier = state switch
        {
            IssueSearchState.Open => "state:open ",
            IssueSearchState.Closed => "state:closed ",
            _ => "",
        };
        var q = $"repo:{repo} is:issue {stateQualifier}{query.Trim()}";
        var perPage = Math.Clamp(limit, 1, MaxSearchHits);
        var url = $"{_options.ApiUrl.TrimEnd('/')}/search/issues?q={Uri.EscapeDataString(q)}&per_page={perPage}";

        var what = $"searching {repo}'s issues";
        var fetched = await GetJsonAsync(url, token.Value, what, ct).ConfigureAwait(false);
        if (fetched.IsFailure)
            return Result<IReadOnlyList<IssueHit>>.Failure(fetched.Error);
        if (fetched.Value is not { } doc)
            return Result<IReadOnlyList<IssueHit>>.Failure($"{repo} does not exist, or the configured token cannot see it.");

        using (doc)
        {
            var repoSuffix = $"/repos/{repo.Owner}/{repo.Name}";
            return ReadShape<IReadOnlyList<IssueHit>>(what, () =>
            [
                .. doc.RootElement.GetProperty("items").EnumerateArray()
                    .Where(i => !i.TryGetProperty("pull_request", out _)
                                && (i.GetProperty("repository_url").GetString() ?? "").EndsWith(repoSuffix, StringComparison.OrdinalIgnoreCase))
                    .Take(perPage)
                    .Select(i => new IssueHit(
                        i.GetProperty("number").GetInt32(),
                        i.GetProperty("title").GetString() ?? "",
                        i.GetProperty("state").GetString() ?? "",
                        HtmlUrl(i))),
            ]);
        }
    }

    public Task<Result<IReadOnlyList<IssueText>>> ListIssuesUpdatedSinceAsync(RepoRef repo, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return ScanAsync(
            page => $"{RepoUrl(repo)}/issues?state=all&since={Uri.EscapeDataString(FormatIso(since.UtcDateTime))}&per_page=100&page={page}",
            $"listing {repo}'s issues updated since {FormatIso(since.UtcDateTime)}",
            e => new IssueText(
                e.GetProperty("number").GetInt32(),
                e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
                HtmlUrl(e),
                e.TryGetProperty("pull_request", out _),
                AuthorLogin(e)),
            ct);
    }

    public Task<Result<IReadOnlyList<IssueCommentText>>> ListIssueCommentsSinceAsync(
        RepoRef repo, int number, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0)
            return Task.FromResult(Result<IReadOnlyList<IssueCommentText>>.Failure("An issue number must be positive."));

        return ScanAsync(
            page => $"{IssueUrl(repo, number)}/comments?since={Uri.EscapeDataString(FormatIso(since.UtcDateTime))}&per_page=100&page={page}",
            $"listing the comments on {repo}#{number}",
            e => new IssueCommentText(
                e.GetProperty("id").GetInt64(),
                e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
                HtmlUrl(e),
                AuthorLogin(e)),
            ct);
    }

    public async Task<Result<string>> GetPullRequestHeadShaAsync(RepoRef repo, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0)
            return Result<string>.Failure("A pull request number must be positive.");

        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<string>.Failure(token.Error);

        var what = $"reading pull request {repo}#{number}";
        var fetched = await GetJsonAsync($"{RepoUrl(repo)}/pulls/{number}", token.Value, what, ct).ConfigureAwait(false);
        if (fetched.IsFailure)
            return Result<string>.Failure(fetched.Error);
        if (fetched.Value is not { } doc)
            return Result<string>.Failure($"Pull request {repo}#{number} does not exist, or the configured token cannot see it.");

        using (doc)
        {
            return ReadShape(what, () => doc.RootElement.GetProperty("head").GetProperty("sha").GetString()
                ?? throw new InvalidOperationException("head.sha is null"));
        }
    }

    /// <summary>
    ///     Reads up to <see cref="MaxScanPages"/> pages of 100 and stops at the first short page. More than that is a
    ///     failure rather than a silent cut, because a marker scan that stopped early would report "not found" for
    ///     something that exists.
    /// </summary>
    private async Task<Result<IReadOnlyList<T>>> ScanAsync<T>(
        Func<int, string> pageUrl, string what, Func<JsonElement, T> read, CancellationToken ct)
    {
        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<IReadOnlyList<T>>.Failure(token.Error);

        var found = new List<T>();
        for (var page = 1; page <= MaxScanPages; page++)
        {
            var fetched = await GetJsonAsync(pageUrl(page), token.Value, what, ct).ConfigureAwait(false);
            if (fetched.IsFailure)
                return Result<IReadOnlyList<T>>.Failure(fetched.Error);
            if (fetched.Value is not { } doc)
                return Result<IReadOnlyList<T>>.Failure($"{what}: GitHub answered 404.");

            using (doc)
            {
                var items = ReadShape<IReadOnlyList<T>>(what, () => [.. doc.RootElement.EnumerateArray().Select(read)]);
                if (items.IsFailure)
                    return items;

                found.AddRange(items.Value);
                if (items.Value.Count < 100)
                    return Result<IReadOnlyList<T>>.Success(found);
            }
        }

        return Result<IReadOnlyList<T>>.Failure($"{what}: the scan read the {MaxScanPages * 100}-item cap and stops rather than read further.");
    }

    public async Task<Result<string>> GetAuthenticatedLoginAsync(CancellationToken ct = default)
    {
        var token = _tokens.GetToken();
        if (token.IsFailure)
            return Result<string>.Failure(token.Error);

        const string What = "reading the authenticated GitHub account";
        var fetched = await GetJsonAsync($"{_options.ApiUrl.TrimEnd('/')}/user", token.Value, What, ct).ConfigureAwait(false);
        if (fetched.IsFailure)
            return Result<string>.Failure(fetched.Error);
        if (fetched.Value is not { } doc)
            return Result<string>.Failure("GitHub answered 404 for the authenticated account, so the configured token cannot be used to tell which comments are Daedalus's own.");

        using (doc)
        {
            return ReadShape(What, () => doc.RootElement.GetProperty("login").GetString()
                ?? throw new InvalidOperationException("login is null"));
        }
    }

    /// <summary>The <c>user.login</c> of an issue or comment; a missing user, such as a deleted account, is a <see cref="InvalidOperationException"/>, which <see cref="ReadShape{T}"/> turns into a failed result.</summary>
    private static string AuthorLogin(JsonElement element) =>
        element.GetProperty("user").GetProperty("login").GetString()
            ?? throw new InvalidOperationException("user.login is null");

    /// <summary>GitHub's <c>html_url</c>; one that is not an absolute URL is a <see cref="FormatException"/>, which <see cref="ReadShape{T}"/> turns into a failed result.</summary>
    private static Uri HtmlUrl(JsonElement element) =>
        new(element.GetProperty("html_url").GetString() ?? "", UriKind.Absolute);

    /// <summary>A body of the wrong shape, such as a missing property, is a failed result rather than an exception.</summary>
    private static Result<T> ReadShape<T>(string what, Func<T> read)
    {
        try
        {
            return Result<T>.Success(read());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Result<T>.Failure($"GitHub's answer to {what} was not in the expected shape: {ex.Message}");
        }
    }

    [GeneratedRegex(@"(?:^|[\s(])-?(?:repo|org|user|owner):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScopeQualifier();

    [LoggerMessage(EventId = 511, Level = LogLevel.Warning, Message = "GitHub read failed: {What}")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, string what);

    [LoggerMessage(EventId = 510, Level = LogLevel.Warning,
        Message = "Looking up an open pull request for {Repository} failed")]
    private static partial void LogOpenPullRequestLookupFailed(ILogger logger, Exception exception, string repository);

    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "no message";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
                return messageEl.GetString() ?? "no message";
        }
        catch (JsonException)
        {
            // Not JSON, or not shaped as expected — fall back to the raw body below.
        }

        return body;
    }
}

/// <summary>A GitHub REST request returned a non-success status. Carries the already-mapped, user-facing reason.</summary>
public sealed class GitHubRequestException : Exception
{
    public GitHubRequestException()
    {
    }

    public GitHubRequestException(string message) : base(message)
    {
    }

    public GitHubRequestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
