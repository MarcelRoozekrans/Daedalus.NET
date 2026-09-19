# Phase 1.9 — scout repository tooling implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the scout agent tools that can actually observe repository activity, so the 7am digest contains true statements rather than an empty report or an invented one.

**Architecture:** A thin `GitHubApi` over `HttpClient`, split into `IGitHubReader` and `IGitHubWriter` so the read/write boundary is a type-level fact. Two `[ThalosToolType]` classes sit above them, registered under **two different tool sources**, and the write source is bound to the existing `developer` authorization policy — which the scheduled run's `reader` principal cannot satisfy.

**Tech Stack:** .NET 10, `HttpClient`, Thalos.NET 0.5.0, ZeroAlloc.Authorization. Tests: xunit, NSubstitute, AwesomeAssertions.

**Spec:** `docs/plans/2026-09-19-scout-repository-tooling-design.md`

## Global Constraints

- **Time comes from injected `TimeProvider`.** No `DateTime.UtcNow` anywhere.
- **`Daedalus.Application` must not reference Infrastructure.** `CleanArchitectureTests` enforces it.
- **The token is never logged and never returned** by any tool or client method.
- **No automatic retry on any write.** A retried comment is a double comment.
- **An unverified category must never read as "nothing happened."** Absence of evidence is not evidence of absence.
- **No silent caps.** A truncated result says it was truncated.
- **Do not build on the Ralph-era GitHub code** in `Daedalus.Infrastructure/Services/CodeAnalysis/` — phase 1.7 retires it. Reuse only the *idea* of resolving `GITHUB_TOKEN` from the environment.
- Conventional commits. **Commit bodies must not contain nested parentheses** — release-please silently drops such commits from the changelog while the workflow reports success. Headers at or under 100 characters. No `claude.ai/code/session_*` URL or `Claude-Session:` trailer.
- **Do not run `Daedalus.Tests.Playwright.Browser`** — ~11 minutes, excluded from CI, and it stalled two implementers in the previous phase. Skip `Playwright.Api` too; its 126 `OneTimeSetUp` failures are pre-existing, as are 9 `AuthenticationFlowTests`.
- **Assume nothing is pinned until you have seen it fail.** Every test below has an explicit failing step.

## Verified facts — do not re-derive, and do not contradict

These were checked against the installed packages and the running codebase while writing this plan.

| Fact | Evidence |
|---|---|
| `AddLocalTools(sourceName, params Type[])` exposes tools as `{sourceName}__{tool}` | `Thalos.NET.xml`, `M:Thalos.ThalosBuilder.AddLocalTools` |
| `AgentDefinition.Tools` is a **glob allow-list over qualified `source__tool` names, defaulting to everything** | `Thalos.NET.Abstractions.xml` |
| `Thalos:ToolPolicies` binds a tool pattern to a policy, evaluated by `DefaultToolAuthorizer` | `DaedalusAgentsOptions.ToolPolicies`, wired at `DaedalusAgentsServiceCollectionExtensions.cs:135` |
| The precedent already exists: `roslyn__apply_*` and `roslyn__rename_*` → `developer` | `appsettings.json` `Thalos:ToolPolicies` |
| `DeveloperPolicy` requires role `developer` **or** `admin` | `src/Daedalus.Agents/Security/DeveloperPolicy.cs` |
| A scheduled run executes as `schedule:daedalus` with roles `["reader"]` | `appsettings.json`; `SubagentRunExecutor.cs:53` builds `DetachedPrincipal` |
| Existing tools register as `AddLocalTools("daedalus", typeof(DaedalusKnowledgeTools), typeof(DaedalusScheduleTools))` | `DaedalusAgentsServiceCollectionExtensions.cs:131` |
| The scout's current tools are `roslyn__*`, `daedalus__*`, `memory__*` | `appsettings.json` agent `scout` |
| House style is raw `HttpClient` against `api.github.com`; **no Octokit anywhere** | `GitHubPullRequestFactory.cs:68` |

**The naming consequence.** Because reads register under the existing `daedalus` source, the scout picks them up through its existing `daedalus__*` glob with **no config change**. Writes must register under a source whose name cannot be matched by `daedalus__*` — and must not be one character away from it. This plan uses **`repoaction`**, giving `repoaction__comment_on_issue`. Do not use `daedalus_write`.

## File Structure

| File | Responsibility |
|---|---|
| `src/Daedalus.Agents/GitHub/GitHubOptions.cs` | Create — API base URL, user agent, page cap, default lookback |
| `src/Daedalus.Agents/GitHub/GitHubTokenSource.cs` | Create — resolves `GITHUB_TOKEN`, fails loudly when absent |
| `src/Daedalus.Agents/GitHub/RepoRef.cs` | Create — parses and validates `owner/repo` |
| `src/Daedalus.Agents/GitHub/GitHubResults.cs` | Create — the result records, including the unchecked-category shape |
| `src/Daedalus.Agents/GitHub/IGitHubReader.cs` | Create — the five reads |
| `src/Daedalus.Agents/GitHub/IGitHubWriter.cs` | Create — comment, label, close |
| `src/Daedalus.Agents/GitHub/GitHubApi.cs` | Create — the `HttpClient` client implementing both interfaces |
| `src/Daedalus.Agents/Tools/DaedalusRepoTools.cs` | Create — `[ThalosToolType]`, injects **only** `IGitHubReader` |
| `src/Daedalus.Agents/Tools/DaedalusRepoActionTools.cs` | Create — `[ThalosToolType]`, injects **only** `IGitHubWriter` |
| `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs` | Modify — register the client, both interfaces, and the second tool source |
| `src/Daedalus.Agents/Scheduling/RepoDigestPrompts.cs` | Modify — `ScoutTask` becomes a function taking repo and window |
| `src/Daedalus.Agents/Scheduling/RunSteps.cs` | Modify — supply repo and `since` from the previous occurrence |
| `src/Daedalus.Api/appsettings.json` | Modify — GitHub options, the tool policy, the digest's target repo |

---

### Task 1: `RepoRef`, options, and the token source

The smallest independently testable unit, and it carries two constraints that are easy to get wrong.

**Files:**
- Create: `src/Daedalus.Agents/GitHub/RepoRef.cs`, `GitHubOptions.cs`, `GitHubTokenSource.cs`
- Test: `tests/Daedalus.Tests.Unit/Agents/GitHub/RepoRefTests.cs`, `GitHubTokenSourceTests.cs`

**Interfaces:**
- Produces: `RepoRef` with `static Result<RepoRef> Parse(string value)`, properties `Owner` and `Name`, and `override string ToString()` returning `"owner/name"`.
- Produces: `GitHubOptions` with `string ApiUrl = "https://api.github.com"`, `string UserAgent = "Daedalus"`, `int MaxItemsPerCategory = 50`, `TimeSpan DefaultLookback = TimeSpan.FromHours(24)`, and `const string SectionName = "ExternalServices:Platforms:GitHub"`.
- Produces: `IGitHubTokenSource` with `Result<string> GetToken()`; implementation `GitHubTokenSource`.

- [ ] **Step 1: Write the failing tests for `RepoRef`**

```csharp
[Theory]
[InlineData("owner/repo", "owner", "repo")]
[InlineData("Marcel-R/Daedalus.NET", "Marcel-R", "Daedalus.NET")]
public void Parse_accepts_a_well_formed_reference(string input, string owner, string name)
{
    var result = RepoRef.Parse(input);

    result.IsSuccess.Should().BeTrue();
    result.Value.Owner.Should().Be(owner);
    result.Value.Name.Should().Be(name);
    result.Value.ToString().Should().Be(input);
}

[Theory]
[InlineData("")]
[InlineData("   ")]
[InlineData("noslash")]
[InlineData("too/many/parts")]
[InlineData("/repo")]
[InlineData("owner/")]
[InlineData("../etc")]
[InlineData("owner/repo?query=1")]
public void Parse_rejects_anything_that_would_build_a_malformed_url(string input)
{
    RepoRef.Parse(input).IsFailure.Should().BeTrue(
        "an agent passes this string straight from a model, and a bad value must not reach HttpClient");
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter RepoRefTests`
Expected: FAIL — `RepoRef` does not exist, so these do not compile. That is the correct RED for a new type.

- [ ] **Step 3: Implement `RepoRef`**

```csharp
using CSharpFunctionalExtensions;

namespace Daedalus.Agents.GitHub;

/// <summary>
///     A validated <c>owner/name</c> repository reference. Agents pass this string straight from a model, so it is
///     parsed rather than interpolated: a value containing a slash, a query or a traversal segment would otherwise
///     build a URL pointing somewhere other than the caller intended.
/// </summary>
public sealed class RepoRef
{
    private static readonly char[] Forbidden = ['?', '#', '\\', ' '];

    private RepoRef(string owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    public string Owner { get; }
    public string Name { get; }

    public static Result<RepoRef> Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<RepoRef>("Repository must be given as owner/name.");

        var parts = value.Split('/');
        if (parts.Length != 2)
            return Result.Failure<RepoRef>($"Repository must be given as owner/name, not '{value}'.");

        var owner = parts[0];
        var name = parts[1];

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            return Result.Failure<RepoRef>($"Repository must be given as owner/name, not '{value}'.");

        if (owner.IndexOfAny(Forbidden) >= 0 || name.IndexOfAny(Forbidden) >= 0
            || owner.Contains("..", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
        {
            return Result.Failure<RepoRef>($"Repository name contains characters that are not allowed: '{value}'.");
        }

        return Result.Success(new RepoRef(owner, name));
    }

    public override string ToString() => $"{Owner}/{Name}";
}
```

- [ ] **Step 4: Run them and watch them pass**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter RepoRefTests`
Expected: PASS, 10 tests.

- [ ] **Step 5: Write the failing token-source tests**

```csharp
[Fact]
public void GetToken_fails_when_the_environment_variable_is_absent()
{
    var source = new GitHubTokenSource(_ => null);

    var result = source.GetToken();

    result.IsFailure.Should().BeTrue(
        "falling back to unauthenticated requests turns a credential problem into a 404 on every private repo");
}

[Fact]
public void GetToken_fails_when_the_environment_variable_is_blank()
{
    new GitHubTokenSource(_ => "   ").GetToken().IsFailure.Should().BeTrue();
}

[Fact]
public void GetToken_returns_the_token_when_present()
{
    new GitHubTokenSource(_ => "ghp_example").GetToken().Value.Should().Be("ghp_example");
}

[Fact]
public void The_failure_message_does_not_contain_the_variable_value()
{
    var result = new GitHubTokenSource(_ => null).GetToken();

    result.Error.Should().NotContain("ghp_", "a token must never reach a message, a log or a model");
}
```

- [ ] **Step 6: Run them and watch them fail**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter GitHubTokenSourceTests`
Expected: FAIL — the type does not exist.

- [ ] **Step 7: Implement the options and the token source**

```csharp
namespace Daedalus.Agents.GitHub;

/// <summary>Bindable options for the GitHub reader and writer, under <see cref="SectionName"/>.</summary>
public sealed class GitHubOptions
{
    public const string SectionName = "ExternalServices:Platforms:GitHub";

    public string ApiUrl { get; set; } = "https://api.github.com";

    /// <summary>GitHub rejects requests without one.</summary>
    public string UserAgent { get; set; } = "Daedalus";

    /// <summary>Per-category ceiling. A category that hits it reports that it was truncated.</summary>
    public int MaxItemsPerCategory { get; set; } = 50;

    /// <summary>Window used only when a run has no previous occurrence to measure from.</summary>
    public TimeSpan DefaultLookback { get; set; } = TimeSpan.FromHours(24);
}
```

```csharp
using CSharpFunctionalExtensions;

namespace Daedalus.Agents.GitHub;

public interface IGitHubTokenSource
{
    Result<string> GetToken();
}

/// <summary>
///     Resolves the GitHub token from <c>GITHUB_TOKEN</c>. Takes the lookup as a delegate so a test can drive it
///     without mutating process environment state, which leaks between parallel tests.
/// </summary>
/// <remarks>
///     There is deliberately no unauthenticated fallback. An absent token would otherwise present as a 404 on every
///     private repository, disguising a credential problem as a missing repository.
/// </remarks>
public sealed class GitHubTokenSource(Func<string, string?> readEnvironmentVariable) : IGitHubTokenSource
{
    public const string VariableName = "GITHUB_TOKEN";

    public GitHubTokenSource() : this(Environment.GetEnvironmentVariable) { }

    public Result<string> GetToken()
    {
        var token = readEnvironmentVariable(VariableName);

        return string.IsNullOrWhiteSpace(token)
            ? Result.Failure<string>($"No GitHub token is configured. Set the {VariableName} environment variable.")
            : Result.Success(token);
    }
}
```

- [ ] **Step 8: Run them and watch them pass, then commit**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter "RepoRefTests|GitHubTokenSourceTests"`
Expected: PASS, 14 tests.

```bash
git add src/Daedalus.Agents/GitHub tests/Daedalus.Tests.Unit/Agents/GitHub
git commit -m "feat(github): add a validated repo reference, options and token source"
```

---

### Task 2: The result shapes

Small, but it decides whether the whole feature can tell the truth. Every later task depends on these types.

**Files:**
- Create: `src/Daedalus.Agents/GitHub/GitHubResults.cs`
- Test: `tests/Daedalus.Tests.Unit/Agents/GitHub/CategoryResultTests.cs`

**Interfaces:**
- Produces: `sealed record CategoryResult<T>(IReadOnlyList<T> Items, bool Truncated, string? Unavailable)` with `static CategoryResult<T> Ok(IReadOnlyList<T> items, bool truncated)`, `static CategoryResult<T> Failed(string reason)`, and `bool Checked => Unavailable is null`.
- Produces: `sealed record CommitSummary(string Sha, string Message, string Author, DateTime CommittedAtUtc)`.
- Produces: `sealed record PullRequestSummary(int Number, string Title, string Author, DateTime UpdatedAtUtc, bool Merged)`.
- Produces: `sealed record IssueSummary(int Number, string Title, string State, DateTime UpdatedAtUtc)`.
- Produces: `sealed record WorkflowRunSummary(long Id, string Name, string Conclusion, string HeadBranch, DateTime RunStartedAtUtc)`.
- Produces: `sealed record RepoActivity(RepoRef Repo, DateTime WindowStartUtc, DateTime WindowEndUtc, CategoryResult<CommitSummary> Commits, CategoryResult<PullRequestSummary> MergedPullRequests, CategoryResult<PullRequestSummary> OpenPullRequests, CategoryResult<IssueSummary> Issues, CategoryResult<WorkflowRunSummary> FailedRuns)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_failed_category_is_not_an_empty_category()
{
    var failed = CategoryResult<CommitSummary>.Failed("the request timed out");
    var empty = CategoryResult<CommitSummary>.Ok([], truncated: false);

    failed.Checked.Should().BeFalse();
    empty.Checked.Should().BeTrue();

    failed.Should().NotBe(empty,
        "reporting a category we could not read as if it were quiet is the failure this feature exists to avoid");
}

[Fact]
public void A_failed_category_carries_the_reason()
{
    CategoryResult<IssueSummary>.Failed("403 rate limited").Unavailable.Should().Be("403 rate limited");
}

[Fact]
public void A_successful_category_has_no_reason()
{
    CategoryResult<IssueSummary>.Ok([], truncated: false).Unavailable.Should().BeNull();
}

[Fact]
public void Truncation_is_recorded_separately_from_emptiness()
{
    var truncated = CategoryResult<CommitSummary>.Ok(
        [new CommitSummary("abc123", "a change", "someone", new DateTime(2026, 9, 19, 6, 0, 0, DateTimeKind.Utc))],
        truncated: true);

    truncated.Truncated.Should().BeTrue();
    truncated.Checked.Should().BeTrue();
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter CategoryResultTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement the result shapes**

```csharp
namespace Daedalus.Agents.GitHub;

/// <summary>
///     One category of repository activity, and whether we actually managed to read it.
/// </summary>
/// <remarks>
///     <see cref="Unavailable"/> exists so that "nothing happened" and "we could not look" are different values
///     rather than the same empty list. Collapsing them produces a digest that calls a repository quiet when it does
///     not know that — the same distinction phase 1.6 drew between <c>Delivered</c> and <c>DeliveryUnknown</c>.
/// </remarks>
public sealed record CategoryResult<T>(IReadOnlyList<T> Items, bool Truncated, string? Unavailable)
{
    /// <summary>True when the category was read successfully, whatever it contained.</summary>
    public bool Checked => Unavailable is null;

    public static CategoryResult<T> Ok(IReadOnlyList<T> items, bool truncated) => new(items, truncated, null);

    public static CategoryResult<T> Failed(string reason) => new([], false, reason);
}

public sealed record CommitSummary(string Sha, string Message, string Author, DateTime CommittedAtUtc);

public sealed record PullRequestSummary(int Number, string Title, string Author, DateTime UpdatedAtUtc, bool Merged);

public sealed record IssueSummary(int Number, string Title, string State, DateTime UpdatedAtUtc);

public sealed record WorkflowRunSummary(long Id, string Name, string Conclusion, string HeadBranch, DateTime RunStartedAtUtc);

/// <summary>Everything one sweep of a repository found, with the window it actually covered.</summary>
public sealed record RepoActivity(
    RepoRef Repo,
    DateTime WindowStartUtc,
    DateTime WindowEndUtc,
    CategoryResult<CommitSummary> Commits,
    CategoryResult<PullRequestSummary> MergedPullRequests,
    CategoryResult<PullRequestSummary> OpenPullRequests,
    CategoryResult<IssueSummary> Issues,
    CategoryResult<WorkflowRunSummary> FailedRuns);
```

- [ ] **Step 4: Run them and watch them pass, then commit**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter CategoryResultTests`
Expected: PASS, 4 tests.

```bash
git add src/Daedalus.Agents/GitHub/GitHubResults.cs tests/Daedalus.Tests.Unit/Agents/GitHub/CategoryResultTests.cs
git commit -m "feat(github): separate an unread category from an empty one"
```

---

### Task 3: `GitHubApi` reads, and the request shape

The client itself. **The trap here is testing your own stub:** a fully mocked handler passes happily against a client that builds the wrong URL or omits the auth header, so one test asserts on the captured *request*.

**Files:**
- Create: `src/Daedalus.Agents/GitHub/IGitHubReader.cs`, `src/Daedalus.Agents/GitHub/GitHubApi.cs`
- Test: `tests/Daedalus.Tests.Unit/Agents/GitHub/GitHubApiReadTests.cs`, and a shared `tests/Daedalus.Tests.Unit/Agents/GitHub/StubHandler.cs`

**Interfaces:**
- Consumes: `RepoRef`, `GitHubOptions`, `IGitHubTokenSource`, all result records from Task 2.
- Produces: `IGitHubReader` with `Task<RepoActivity> GetActivityAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct = default)` and `Task<Result<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken ct = default)`.
- Produces: `GitHubApi : IGitHubReader` — constructor `GitHubApi(HttpClient http, IOptions<GitHubOptions> options, IGitHubTokenSource tokens, TimeProvider clock, ILogger<GitHubApi> logger)`.

- [ ] **Step 1: Write the stub handler**

```csharp
using System.Net;

namespace Daedalus.Tests.Unit.Agents.GitHub;

/// <summary>
///     Records every outgoing request and replies from a queue keyed by path fragment, so a test can assert on what
///     was actually sent rather than only on what it chose to return.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    public StubHandler Route(string containing, HttpStatusCode status, string json)
    {
        _routes[containing] = () => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        return this;
    }

    public StubHandler Route(string containing, Func<HttpResponseMessage> respond)
    {
        _routes[containing] = respond;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _requests.Add(request);

        foreach (var (fragment, respond) in _routes)
        {
            if (request.RequestUri!.PathAndQuery.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(respond());
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}""", System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
```

- [ ] **Step 2: Write the failing read tests**

```csharp
private static readonly DateTime Now = new(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc);
private static readonly DateTime Since = Now.AddHours(-24);

private static GitHubApi Build(StubHandler handler, string? token = "ghp_example")
{
    var http = new HttpClient(handler);
    return new GitHubApi(
        http,
        Options.Create(new GitHubOptions()),
        new GitHubTokenSource(_ => token),
        new FakeTimeProvider(Now),
        NullLogger<GitHubApi>.Instance);
}

[Fact]
public async Task The_request_carries_the_auth_header_the_user_agent_and_the_api_version()
{
    var handler = new StubHandler().Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"trunk"}""");

    await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

    var sent = handler.Requests.Should().ContainSingle().Subject;
    sent.RequestUri!.ToString().Should().Be("https://api.github.com/repos/owner/repo");
    sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
    sent.Headers.Authorization.Parameter.Should().Be("ghp_example");
    sent.Headers.UserAgent.ToString().Should().Contain("Daedalus", "GitHub rejects requests without a User-Agent");
    sent.Headers.Should().ContainKey("X-GitHub-Api-Version");
}

[Fact]
public async Task The_default_branch_is_read_from_the_repository_not_assumed()
{
    var handler = new StubHandler().Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"trunk"}""");

    var branch = await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

    branch.Value.Should().Be("trunk", "a caller may name any repository, and not every repository uses main");
}

[Fact]
public async Task A_category_that_fails_is_reported_unchecked_and_the_others_still_report()
{
    var handler = new StubHandler()
        .Route("/repos/owner/repo/commits", HttpStatusCode.OK,
            """[{"sha":"abc1234","commit":{"message":"a change","author":{"name":"someone","date":"2026-09-19T06:00:00Z"}}}]""")
        .Route("/repos/owner/repo/actions/runs", HttpStatusCode.InternalServerError, """{"message":"boom"}""")
        .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, "[]")
        .Route("/repos/owner/repo/issues", HttpStatusCode.OK, "[]")
        .Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

    var activity = await Build(handler).GetActivityAsync(RepoRef.Parse("owner/repo").Value, Since);

    activity.Commits.Checked.Should().BeTrue();
    activity.Commits.Items.Should().ContainSingle();
    activity.FailedRuns.Checked.Should().BeFalse("the CI read failed, so we must not claim CI was clean");
    activity.FailedRuns.Unavailable.Should().NotBeNullOrWhiteSpace();
}

[Fact]
public async Task A_404_says_both_things_it_could_mean()
{
    var handler = new StubHandler().Route("/repos/owner/repo", HttpStatusCode.NotFound, """{"message":"Not Found"}""");

    var result = await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

    result.IsFailure.Should().BeTrue();
    result.Error.Should().ContainEquivalentOf("does not exist");
    result.Error.Should().ContainEquivalentOf("token",
        "from outside, a missing repository and one the token cannot see are identical");
}

[Fact]
public async Task A_rate_limited_403_is_distinguished_from_a_forbidden_403_and_says_when_it_resets()
{
    var reset = DateTimeOffset.FromUnixTimeSeconds(1789000000);
    var handler = new StubHandler().Route("/repos/owner/repo", () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message":"rate limit exceeded"}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return response;
    });

    var result = await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

    result.Error.Should().ContainEquivalentOf("rate limit");
    result.Error.Should().Contain(reset.UtcDateTime.ToString("u", CultureInfo.InvariantCulture));
}

[Fact]
public async Task A_forbidden_403_that_is_not_rate_limiting_says_so_instead()
{
    var handler = new StubHandler().Route("/repos/owner/repo", () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message":"Resource not accessible"}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-RateLimit-Remaining", "4999");
        return response;
    });

    var result = await Build(handler).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

    result.Error.Should().NotContainEquivalentOf("rate limit");
}

[Fact]
public async Task A_missing_token_fails_before_any_request_is_sent()
{
    var handler = new StubHandler();

    var result = await Build(handler, token: null).GetDefaultBranchAsync(RepoRef.Parse("owner/repo").Value);

    result.IsFailure.Should().BeTrue();
    handler.Requests.Should().BeEmpty("an unauthenticated request would 404 and look like a missing repository");
}

[Fact]
public async Task A_category_at_the_ceiling_reports_that_it_was_truncated()
{
    var many = string.Join(",", Enumerable.Range(0, 50).Select(i =>
        $$"""{"sha":"sha{{i}}","commit":{"message":"m{{i}}","author":{"name":"a","date":"2026-09-19T06:00:00Z"}}}"""));

    var handler = new StubHandler()
        .Route("/repos/owner/repo/commits", HttpStatusCode.OK, $"[{many}]")
        .Route("/repos/owner/repo/actions/runs", HttpStatusCode.OK, """{"workflow_runs":[]}""")
        .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, "[]")
        .Route("/repos/owner/repo/issues", HttpStatusCode.OK, "[]")
        .Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

    var activity = await Build(handler).GetActivityAsync(RepoRef.Parse("owner/repo").Value, Since);

    activity.Commits.Truncated.Should().BeTrue("a silent cap reads as complete coverage");
}

[Fact]
public async Task The_window_it_covered_is_reported_back()
{
    var handler = new StubHandler()
        .Route("/repos/owner/repo/commits", HttpStatusCode.OK, "[]")
        .Route("/repos/owner/repo/actions/runs", HttpStatusCode.OK, """{"workflow_runs":[]}""")
        .Route("/repos/owner/repo/pulls", HttpStatusCode.OK, "[]")
        .Route("/repos/owner/repo/issues", HttpStatusCode.OK, "[]")
        .Route("/repos/owner/repo", HttpStatusCode.OK, """{"default_branch":"main"}""");

    var activity = await Build(handler).GetActivityAsync(RepoRef.Parse("owner/repo").Value, Since);

    activity.WindowStartUtc.Should().Be(Since);
    activity.WindowEndUtc.Should().Be(Now, "the end of the window is the injected clock, never DateTime.UtcNow");
}
```

- [ ] **Step 3: Run them and watch them fail**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter GitHubApiReadTests`
Expected: FAIL — `GitHubApi` and `IGitHubReader` do not exist.

- [ ] **Step 4: Implement `IGitHubReader` and the read half of `GitHubApi`**

Write `IGitHubReader` exactly as given in the Interfaces block above. Then implement `GitHubApi` against it, observing:

- Build every URL from `options.ApiUrl` plus `repo.Owner`/`repo.Name`. Never interpolate the raw user string.
- Set `Authorization: Bearer <token>`, `User-Agent` from options, `Accept: application/vnd.github+json`, and `X-GitHub-Api-Version: 2022-11-28` on every request.
- **Resolve the token once, before any request.** Return failure if absent.
- The five reads run independently. **Each is wrapped so a failure yields `CategoryResult<T>.Failed(reason)` rather than throwing** — one dead category must not lose the other four. Let `OperationCanceledException` propagate; it is a caller decision, not a category failure.
- Take at most `MaxItemsPerCategory` per category and set `Truncated` when the API returned at least that many.
- Commits use `since=` on `/commits` against the resolved default branch. Pull requests use `state=closed&sort=updated&direction=desc` filtered to `merged_at >= since`, and `state=open` for the open set. Issues use `since=` on `/issues` and **drop any element carrying a `pull_request` member**, because GitHub returns pull requests from the issues endpoint. Workflow runs use `/actions/runs?status=failure` filtered to `run_started_at >= since`.
- Map errors with one helper: 404 → a message naming **both** "does not exist" and "the token may not have access"; 403 with `X-RateLimit-Remaining: 0` → a rate-limit message including the reset instant formatted `"u"`; any other 403 → a plain forbidden message; anything else → status plus the API's `message` field.
- `WindowEndUtc` comes from `clock.GetUtcNow().UtcDateTime`.
- **Never log the token.** Log the repo and the category on failure, nothing else.

- [ ] **Step 5: Run them and watch them pass**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter GitHubApiReadTests`
Expected: PASS, 9 tests.

- [ ] **Step 6: Mutation-check the category isolation**

This is the boundary the whole feature rests on, so prove the test can fail.

Temporarily change the per-category wrapper so a failing category throws instead of returning `Failed(...)`. Run `A_category_that_fails_is_reported_unchecked_and_the_others_still_report` and confirm it **fails**. Restore, confirm it passes, and quote both runs in your report.

- [ ] **Step 7: Commit**

```bash
git add src/Daedalus.Agents/GitHub tests/Daedalus.Tests.Unit/Agents/GitHub
git commit -m "feat(github): read repository activity without hiding a failed category"
```

---

### Task 4: `GitHubApi` writes

Three operations, and the constraint that matters is what happens when one fails.

**Files:**
- Create: `src/Daedalus.Agents/GitHub/IGitHubWriter.cs`
- Modify: `src/Daedalus.Agents/GitHub/GitHubApi.cs`
- Test: `tests/Daedalus.Tests.Unit/Agents/GitHub/GitHubApiWriteTests.cs`

**Interfaces:**
- Produces: `IGitHubWriter` with `Task<Result<string>> CommentAsync(RepoRef repo, int number, string body, CancellationToken ct = default)`, `Task<Result<string>> AddLabelAsync(RepoRef repo, int number, string label, CancellationToken ct = default)`, `Task<Result<string>> CloseIssueAsync(RepoRef repo, int number, CancellationToken ct = default)`. Each success value is a short human-readable confirmation.
- `GitHubApi` now implements `IGitHubReader, IGitHubWriter`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Commenting_posts_to_the_issue_comments_endpoint()
{
    var handler = new StubHandler().Route("/issues/7/comments", HttpStatusCode.Created, """{"id":1}""");

    var result = await Build(handler).CommentAsync(RepoRef.Parse("owner/repo").Value, 7, "a note");

    result.IsSuccess.Should().BeTrue();
    var sent = handler.Requests.Should().ContainSingle().Subject;
    sent.Method.Should().Be(HttpMethod.Post);
    sent.RequestUri!.AbsolutePath.Should().Be("/repos/owner/repo/issues/7/comments");
}

[Fact]
public async Task A_failed_write_surfaces_and_is_not_retried()
{
    var handler = new StubHandler().Route("/issues/7/comments", HttpStatusCode.UnprocessableEntity,
        """{"message":"Validation Failed"}""");

    var result = await Build(handler).CommentAsync(RepoRef.Parse("owner/repo").Value, 7, "a note");

    result.IsFailure.Should().BeTrue();
    handler.Requests.Should().ContainSingle("a retried comment is a double comment");
}

[Fact]
public async Task Closing_an_issue_patches_state_to_closed()
{
    var handler = new StubHandler().Route("/issues/7", HttpStatusCode.OK, """{"number":7,"state":"closed"}""");

    await Build(handler).CloseIssueAsync(RepoRef.Parse("owner/repo").Value, 7);

    var sent = handler.Requests.Should().ContainSingle().Subject;
    sent.Method.Should().Be(HttpMethod.Patch);
    (await sent.Content!.ReadAsStringAsync()).Should().Contain("closed");
}

[Fact]
public async Task Adding_a_label_posts_to_the_labels_endpoint()
{
    var handler = new StubHandler().Route("/issues/7/labels", HttpStatusCode.OK, """[{"name":"stale"}]""");

    var result = await Build(handler).AddLabelAsync(RepoRef.Parse("owner/repo").Value, 7, "stale");

    result.IsSuccess.Should().BeTrue();
    handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsolutePath
        .Should().Be("/repos/owner/repo/issues/7/labels");
}

[Fact]
public async Task A_write_with_no_token_sends_nothing()
{
    var handler = new StubHandler();

    var result = await Build(handler, token: null).CommentAsync(RepoRef.Parse("owner/repo").Value, 7, "a note");

    result.IsFailure.Should().BeTrue();
    handler.Requests.Should().BeEmpty();
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter GitHubApiWriteTests`
Expected: FAIL — `IGitHubWriter` does not exist.

- [ ] **Step 3: Implement the write half**

Add `IGitHubWriter` and implement it on `GitHubApi`, reusing the same header and error-mapping helpers Task 3 built. `CommentAsync` posts `{"body": ...}` to `/repos/{owner}/{repo}/issues/{number}/comments`. `AddLabelAsync` posts `{"labels":[...]}` to `.../labels`. `CloseIssueAsync` patches `{"state":"closed"}` to `.../issues/{number}`.

**No retry, no retry handler, no Polly.** A failure returns `Result.Failure` and stops.

- [ ] **Step 4: Run them and watch them pass, then commit**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter GitHubApiWriteTests`
Expected: PASS, 5 tests.

```bash
git add src/Daedalus.Agents/GitHub tests/Daedalus.Tests.Unit/Agents/GitHub
git commit -m "feat(github): add comment, label and close without retry"
```

---

### Task 5: The two tool types

Where the read/write split becomes visible to agents. **Tool output is read by a model, not rendered in a grid** — it has only words, so an unchecked category must say so in prose.

**Files:**
- Create: `src/Daedalus.Agents/Tools/DaedalusRepoTools.cs`, `src/Daedalus.Agents/Tools/DaedalusRepoActionTools.cs`
- Test: `tests/Daedalus.Tests.Unit/Agents/Tools/DaedalusRepoToolsTests.cs`

**Interfaces:**
- Consumes: `IGitHubReader`, `IGitHubWriter`, `RepoRef`, the Task 2 records.
- Produces: `DaedalusRepoTools` with `[ThalosTool("repo_activity")]` and `[ThalosTool("repo_default_branch")]`.
- Produces: `DaedalusRepoActionTools` with `[ThalosTool("comment_on_issue")]`, `[ThalosTool("add_label")]`, `[ThalosTool("close_issue")]`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task An_unchecked_category_is_stated_in_the_output_not_omitted()
{
    var reader = Substitute.For<IGitHubReader>();
    reader.GetActivityAsync(Arg.Any<RepoRef>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
        .Returns(new RepoActivity(
            RepoRef.Parse("owner/repo").Value,
            new DateTime(2026, 9, 18, 7, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc),
            CategoryResult<CommitSummary>.Ok([], false),
            CategoryResult<PullRequestSummary>.Ok([], false),
            CategoryResult<PullRequestSummary>.Ok([], false),
            CategoryResult<IssueSummary>.Ok([], false),
            CategoryResult<WorkflowRunSummary>.Failed("500 from the actions endpoint")));

    var output = await new DaedalusRepoTools(reader).RepoActivity("owner/repo", null);

    output.Should().ContainEquivalentOf("could not");
    output.Should().ContainEquivalentOf("CI");
    output.Should().NotContainEquivalentOf("no failed CI runs",
        "a category we could not read must never be reported as clean");
}

[Fact]
public async Task The_window_actually_covered_is_stated()
{
    var reader = StubActivityReader();

    var output = await new DaedalusRepoTools(reader).RepoActivity("owner/repo", null);

    output.Should().Contain("2026-09-18");
    output.Should().Contain("2026-09-19");
}

[Fact]
public async Task Truncation_is_stated()
{
    var reader = Substitute.For<IGitHubReader>();
    reader.GetActivityAsync(Arg.Any<RepoRef>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
        .Returns(ActivityWith(CategoryResult<CommitSummary>.Ok(
            [new CommitSummary("abc1234", "a change", "someone", new DateTime(2026, 9, 19, 6, 0, 0, DateTimeKind.Utc))],
            truncated: true)));

    var output = await new DaedalusRepoTools(reader).RepoActivity("owner/repo", null);

    output.Should().ContainEquivalentOf("more than");
}

[Fact]
public async Task A_malformed_repository_argument_is_refused_without_calling_the_reader()
{
    var reader = Substitute.For<IGitHubReader>();

    var output = await new DaedalusRepoTools(reader).RepoActivity("not-a-repo", null);

    output.Should().ContainEquivalentOf("owner/name");
    await reader.DidNotReceiveWithAnyArgs().GetActivityAsync(default!, default, default);
}

[Fact]
public async Task A_write_failure_is_reported_to_the_agent_rather_than_swallowed()
{
    var writer = Substitute.For<IGitHubWriter>();
    writer.CommentAsync(Arg.Any<RepoRef>(), 7, Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(Result.Failure<string>("422 Validation Failed"));

    var output = await new DaedalusRepoActionTools(writer).CommentOnIssue("owner/repo", 7, "a note");

    output.Should().ContainEquivalentOf("422");
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter DaedalusRepoToolsTests`
Expected: FAIL — neither tool type exists.

- [ ] **Step 3: Implement both tool types**

Follow `DaedalusScheduleTools` exactly for shape: `[ThalosToolType]` on a `sealed class` with primary-constructor injection, `[ThalosTool("name")]` plus `[Description]` on each method, `[Description]` on every parameter, `CancellationToken ct = default` last.

`DaedalusRepoTools` injects **only** `IGitHubReader`. `DaedalusRepoActionTools` injects **only** `IGitHubWriter`. Neither takes both, and neither takes `GitHubApi` directly.

`RepoActivity(string repo, string? since, CancellationToken ct = default)` parses `repo` via `RepoRef.Parse` and returns the parse error as the tool's output when it fails. It parses `since` as a round-trip UTC instant when supplied.

The rendering rules, which the tests above pin:

- Name the window explicitly — "between {start:yyyy-MM-dd HH:mm} and {end:yyyy-MM-dd HH:mm} UTC".
- For a **checked and empty** category, say so plainly: "no commits".
- For an **unchecked** category, say it could not be read and give the reason. **Never** emit the empty-category wording for it.
- For a **truncated** category, say "more than N", not a bare count.

Tool descriptions are functional, not documentation: a model reads them to choose a tool and supply arguments. Say that `repo` is `owner/name`, that `since` defaults to the configured lookback when omitted, and — on the action tools — that they modify a real repository.

- [ ] **Step 4: Run them and watch them pass**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter DaedalusRepoToolsTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Mutation-check the unchecked-category rendering**

Temporarily change the renderer to treat `Checked == false` the same as an empty list. Run `An_unchecked_category_is_stated_in_the_output_not_omitted` and confirm it **fails**. Restore, confirm it passes, quote both runs.

- [ ] **Step 6: Commit**

```bash
git add src/Daedalus.Agents/Tools tests/Daedalus.Tests.Unit/Agents/Tools
git commit -m "feat(agents): add repo activity tools that never fake a category"
```

---

### Task 6: Registration, the second tool source, and the policy that enforces the boundary

The security task. Everything above is inert until this wires it, and **this is where the boundary is actually made real**.

**Files:**
- Modify: `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs`
- Modify: `src/Daedalus.Api/appsettings.json`
- Test: `tests/Daedalus.Tests.Integration/Agents/RepoToolBoundaryTests.cs`

**Interfaces:**
- Produces: `public const string RepoActionToolSourceName = "repoaction";` on `DaedalusAgentsServiceCollectionExtensions`, beside the existing `KnowledgeToolSourceName = "daedalus"`.

- [ ] **Step 1: Write the failing boundary tests**

```csharp
[Fact]
public void The_read_tools_are_exposed_under_the_existing_daedalus_source()
{
    ToolNames().Should().Contain("daedalus__repo_activity",
        "the scout already allows daedalus__*, so reads need no config change");
}

[Fact]
public void The_write_tools_are_exposed_under_a_separate_source()
{
    ToolNames().Should().Contain("repoaction__comment_on_issue");
}

[Fact]
public void The_write_source_name_cannot_be_matched_by_the_scout_glob()
{
    foreach (var name in ToolNames().Where(n => n.StartsWith("repoaction__", StringComparison.Ordinal)))
    {
        GlobMatches("daedalus__*", name).Should().BeFalse(
            "a write tool one character away from the read prefix would be matched by accident");
    }
}

[Fact]
public void The_scouts_configured_patterns_match_no_write_tool()
{
    var scout = AgentDefinitions().Single(a => a.Name == "scout");
    var writeTools = ToolNames().Where(n => n.StartsWith("repoaction__", StringComparison.Ordinal)).ToList();

    writeTools.Should().NotBeEmpty("otherwise this test passes vacuously");

    foreach (var tool in writeTools)
    {
        scout.Tools.Any(p => GlobMatches(p, tool)).Should().BeFalse(
            $"the unattended scout must not reach {tool}");
    }
}

[Fact]
public void Every_write_tool_is_bound_to_the_developer_policy()
{
    var policies = ToolPolicyBindings();
    var writeTools = ToolNames().Where(n => n.StartsWith("repoaction__", StringComparison.Ordinal)).ToList();

    writeTools.Should().NotBeEmpty();

    foreach (var tool in writeTools)
    {
        policies.Any(b => GlobMatches(b.Pattern, tool) && b.Policy == DeveloperPolicy.PolicyName)
            .Should().BeTrue($"{tool} must be denied at the authorizer, not only by a tool list");
    }
}

[Fact]
public async Task The_scheduled_run_principal_fails_the_developer_policy()
{
    var scheduled = new DetachedPrincipal("schedule:daedalus", ["reader"]);

    var result = await new DeveloperPolicy().EvaluateAsync(scheduled);

    result.IsFailure.Should().BeTrue(
        "this is the layer that holds when someone widens a tool list by accident");
}
```

Implement the three helpers — `ToolNames()`, `AgentDefinitions()`, `ToolPolicyBindings()` — by building the host the way `ApiHostSchedulingWiringTests` does and reading the registered tool sources, the agent catalog and the bound options. Do not hand-roll a second copy of the configuration.

`GlobMatches` must use the **same matcher Thalos uses** for tool allow-lists, resolved from the container rather than reimplemented. If no matcher type is reachable, report BLOCKED rather than writing your own — a bespoke glob that disagrees with the real one makes this whole test file lie.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter RepoToolBoundaryTests`
Expected: FAIL — no repo tools are registered yet.

- [ ] **Step 3: Register the client, the interfaces, and the second source**

In `DaedalusAgentsServiceCollectionExtensions`:

```csharp
public const string RepoActionToolSourceName = "repoaction";
```

Bind `GitHubOptions` from `GitHubOptions.SectionName`. Register `IGitHubTokenSource` as a singleton. Register `GitHubApi` through `AddHttpClient` so it gets a pooled, properly-disposed `HttpClient`, and expose it as **both** interfaces:

```csharp
services.AddHttpClient<GitHubApi>();
services.AddScoped<IGitHubReader>(sp => sp.GetRequiredService<GitHubApi>());
services.AddScoped<IGitHubWriter>(sp => sp.GetRequiredService<GitHubApi>());
```

Add the read tools to the **existing** source and the write tools to the new one:

```csharp
.AddLocalTools(KnowledgeToolSourceName, typeof(DaedalusKnowledgeTools), typeof(DaedalusScheduleTools), typeof(DaedalusRepoTools))
.AddLocalTools(RepoActionToolSourceName, typeof(DaedalusRepoActionTools))
```

- [ ] **Step 4: Add the policy binding and the GitHub options to configuration**

In `src/Daedalus.Api/appsettings.json`, extend `Thalos:ToolPolicies`, which already binds mutating Roslyn tools:

```json
"ToolPolicies": [
  { "Pattern": "roslyn__apply_*", "Policy": "developer" },
  { "Pattern": "roslyn__rename_*", "Policy": "developer" },
  { "Pattern": "repoaction__*", "Policy": "developer" }
],
```

Add the GitHub options under `ExternalServices:Platforms:GitHub`, leaving `AuthToken` as it is — the token comes from the environment, not from configuration, so it is never committed.

Add `repoaction__*` to the **Daedalus Architect** agent's `Tools` list, and **leave the scout's list untouched**. The Architect is interactive; the scout is not.

- [ ] **Step 5: Run them and watch them pass**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter RepoToolBoundaryTests`
Expected: PASS, 6 tests.

- [ ] **Step 6: Mutation-check the boundary, both layers**

Two separate runs, each reverted before the next:

1. Add `repoaction__*` to the scout's `Tools` list. Confirm `The_scouts_configured_patterns_match_no_write_tool` **fails**. Revert.
2. Remove the `repoaction__*` policy binding. Confirm `Every_write_tool_is_bound_to_the_developer_policy` **fails**. Revert.

Quote all four runs. A boundary test that cannot fail is not a boundary.

- [ ] **Step 7: Commit**

```bash
git add src/Daedalus.Agents src/Daedalus.Api/appsettings.json tests/Daedalus.Tests.Integration
git commit -m "feat(agents): register repo tools and deny writes to unattended runs"
```

---

### Task 7: Wire the digest — the repository and the real window

The last functional task. It turns the tools into an actual 7am message.

**Files:**
- Modify: `src/Daedalus.Agents/Scheduling/RepoDigestPrompts.cs`
- Modify: `src/Daedalus.Agents/Scheduling/RunSteps.cs`
- Modify: `src/Daedalus.Api/appsettings.json`
- Test: `tests/Daedalus.Tests.Integration/Scheduling/RepoDigestWindowTests.cs`

**Interfaces:**
- Produces: `RepoDigestPrompts.ScoutTask(string repo, DateTime sinceUtc)` replacing the `const string ScoutTask`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task The_window_starts_at_the_previous_occurrence_not_a_fixed_lookback()
{
    var schedule = await SeedScheduleAsync();
    await SeedCompletedExecutionAsync(schedule.Id, occurrenceAt: Now.AddHours(-25));

    var since = await ResolveWindowStartAsync(schedule.Id);

    since.Should().Be(Now.AddHours(-25),
        "a run missed at 07:00 and caught at 08:00 must not silently drop the missing hour");
}

[Fact]
public async Task A_schedule_with_no_previous_occurrence_uses_the_configured_lookback()
{
    var schedule = await SeedScheduleAsync();

    var since = await ResolveWindowStartAsync(schedule.Id);

    since.Should().Be(Now - new GitHubOptions().DefaultLookback);
}

[Fact]
public void The_scout_task_names_the_repository_and_the_window()
{
    var task = RepoDigestPrompts.ScoutTask("owner/repo", new DateTime(2026, 9, 18, 7, 0, 0, DateTimeKind.Utc));

    task.Should().Contain("owner/repo");
    task.Should().Contain("2026-09-18");
    task.Should().NotContainEquivalentOf("this repository",
        "the old wording had nothing resolving it and left the scout guessing");
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter RepoDigestWindowTests`
Expected: FAIL — `ScoutTask` is still a constant.

- [ ] **Step 3: Make `ScoutTask` a function**

```csharp
/// <summary>The task handed to <see cref="ScoutAgent"/> for one scheduled run, over one repository and one window.</summary>
/// <remarks>
///     This was a constant reading "this repository", with nothing resolving which one. The tools take an explicit
///     <c>owner/name</c>, so the prompt has to name it.
/// </remarks>
public static string ScoutTask(string repo, DateTime sinceUtc) =>
    $"""
     Sweep the repository {repo} for everything that changed since {sinceUtc:yyyy-MM-dd HH:mm} UTC: new commits on
     the default branch, merged and still-open pull requests, issues opened or closed, and any CI runs that failed.
     Use the daedalus__repo_activity tool with repo "{repo}" and since "{sinceUtc:O}" — do not guess at activity you
     have not retrieved.
     For each item, note what changed, who it affects, and whether it needs a human's attention before the next
     digest. Do not write prose yet — list findings as short, factual bullet points a second agent will turn into a
     summary. If a category is empty, say so explicitly rather than omitting it. If the tool reports that a category
     could not be read, say that instead — never report an unreadable category as quiet.
     """;
```

- [ ] **Step 4: Resolve the window in `RunSteps` and pass the repository**

The scout step already builds its task. Change it to resolve the window start as: the `OccurrenceAt` of the most recent **completed** execution for this schedule before the current occurrence, or `now - GitHubOptions.DefaultLookback` when there is none. Pass that and the configured repository into `ScoutTask`.

The repository comes from the schedule's configuration. Add a `Repository` field to the `ScheduledRuns` config entry and read it here. A `RepoDigest` schedule with no repository configured is a startup validation failure, not a silent no-op — follow how the existing startup validator reports a bad agent name.

- [ ] **Step 5: Configure the digest's repository**

In `appsettings.json`, add `"Repository": "MarcelRoozekrans/Daedalus.NET"` to the `daily-digest` entry. Leave `Enabled: false`; turning it on is the user's decision and needs a real `ConversationId` anyway.

- [ ] **Step 6: Run them and watch them pass, then commit**

Run: `dotnet test tests/Daedalus.Tests.Integration --filter RepoDigestWindowTests`
Expected: PASS, 3 tests.

```bash
git add src/Daedalus.Agents/Scheduling src/Daedalus.Api/appsettings.json tests/Daedalus.Tests.Integration
git commit -m "feat(scheduling): give the digest a repository and a real window"
```

---

### Task 8: Verification

Produces an honest account, not a green tick.

**Files:** none in `src/`.

- [ ] **Step 1: Full suite against the baseline**

Run the unit suites and `Daedalus.Tests.Integration`. **Skip both Playwright projects.**

The baseline entering this phase is **Unit 156 + 313 + 396 + 130 passing; Integration 476 passed / 9 failed**. The 9 are the known `AuthenticationFlowTests`. Report per-project numbers and attribute the delta. **Any pre-existing failure whose status changed is a finding.**

- [ ] **Step 2: Prove the host still boots**

`dotnet run --project src/Daedalus.AppHost`. Confirm the API comes up and `/health` returns 200. The new `AddHttpClient` registration and the second tool source both run at startup, so a DI mistake here is a boot failure rather than a test failure.

Note: bare `dotnet run --project src/Daedalus.Api` needs `ConnectionStrings__daedalus` and still has no Keycloak. Use the AppHost.

- [ ] **Step 3: Exercise one real read, and say whether you could**

With a `GITHUB_TOKEN` in the environment, call the reader against a public repository and confirm it returns real data. **If no token is available, say so and report this step as not performed** — do not simulate it and do not describe a stub run as though it were real.

- [ ] **Step 4: Confirm the token never leaks**

Grep the captured startup and test output for the token value. Confirm no log line contains it.

- [ ] **Step 5: Write the report**

State: per-project numbers and the delta; whether the host booted; whether a real read was performed; and an explicit list of what remains unverified. An honest "not performed" is worth more than an unsupported claim.

---

## Self-Review

**Spec coverage.** Every section of the design maps to a task. The five reads and the default-branch resolution are Task 3; the three writes are Task 4; the two interfaces and both tool types are Tasks 3–5; the three-layer boundary is Task 6; the repository argument and the previous-occurrence window are Task 7; every error-handling row in the spec's table has a test in Task 3, 4 or 5. The spec's "known narrowing" about no in-Daedalus audit trail is deliberately **not** implemented — it is recorded as accepted.

**Placeholder scan.** No TBD, no "add error handling", no "similar to Task N". Task 3 step 4 and Task 7 step 4 describe behaviour in prose rather than a full code block; both are bounded by tests written in the preceding step, and both name every endpoint, parameter and rule concretely. Task 6's `GlobMatches` deliberately has no implementation given, with an explicit instruction to resolve Thalos's own matcher and report BLOCKED rather than invent one — a hand-rolled glob that disagrees with the real matcher would make the boundary tests lie.

**Type consistency.** `CategoryResult<T>`, `RepoActivity`, `RepoRef`, `IGitHubReader`, `IGitHubWriter`, `IGitHubTokenSource`, `GitHubOptions` and `RepoActionToolSourceName` are spelled identically everywhere they appear. `GetActivityAsync` and `GetDefaultBranchAsync` keep their signatures from Task 3 through Tasks 5 and 7. Tool names — `daedalus__repo_activity`, `repoaction__comment_on_issue` — are consistent between Tasks 5, 6 and 7.

**One risk worth naming.** Task 6 depends on being able to reach Thalos's tool-name matcher and its bound tool policies from a built host. That was inferred from `DaedalusAgentsOptions.ToolPolicies` and the wiring at `DaedalusAgentsServiceCollectionExtensions.cs:135`, but the *retrieval* path from a built container was not executed while writing this plan. If it turns out not to be reachable, Task 6 reports BLOCKED and the boundary must be proven another way before this phase can claim it — the policy layer is the whole security argument, and asserting it without a test would repeat exactly the mistake phase 1.6's review flagged.
