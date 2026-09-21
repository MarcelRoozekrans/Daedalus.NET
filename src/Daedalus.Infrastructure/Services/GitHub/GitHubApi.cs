#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute — JsonSerializer.Serialize
                               // over small anonymous write payloads; accepted risk, matching GitHubPullRequestFactory
                               // and AzureDevOpsPullRequestFactory elsewhere in this project.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ZeroAlloc.Results;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
public sealed class GitHubApi : IGitHubReader, IGitHubWriter
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
