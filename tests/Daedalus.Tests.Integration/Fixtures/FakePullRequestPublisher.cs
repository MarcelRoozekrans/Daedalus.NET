using System.Collections.Concurrent;
using Thalos;
using Thalos.Git;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     A pull-request host that remembers what it opened. Implements both interfaces, as
///     <c>ThalosPullRequestPublisher</c> does (ruling R28a), so a test host registers the one instance as both:
///     <c>services.AddSingleton(fake); services.AddSingleton&lt;IPullRequestPublisher&gt;(fake);
///     services.AddSingleton&lt;IOpenPullRequestLookup&gt;(fake);</c>. A lookup finds a branch only when this same
///     instance opened a pull request from it, so a redelivery finds the pull request the first delivery opened.
/// </summary>
internal sealed class FakePullRequestPublisher : IPullRequestPublisher, IOpenPullRequestLookup
{
    /// <summary>The URL every pull request this fake opens is reported at.</summary>
    public const string Url = "https://github.com/MarcelRoozekrans/daedalus-sandbox/pull/1";

    private readonly ConcurrentDictionary<string, PullRequestResult> _open = new(StringComparer.Ordinal);
    private int _openCount;

    /// <summary>How many times <see cref="OpenPullRequestAsync"/> was called.</summary>
    public int OpenCount => Volatile.Read(ref _openCount);

    /// <summary>The body of the most recent <see cref="OpenPullRequestAsync"/> call, or null when none was made.</summary>
    public string? LastBody { get; private set; }

    /// <summary>The title of the most recent <see cref="OpenPullRequestAsync"/> call, or null when none was made.</summary>
    public string? LastTitle { get; private set; }

    /// <summary>The target branch of the most recent <see cref="OpenPullRequestAsync"/> call, or null when none was made.</summary>
    public string? LastTargetBranch { get; private set; }

    /// <summary>Records the call, remembers <paramref name="sourceBranch"/> as open, and reports <see cref="Url"/>.</summary>
    public ValueTask<Result<PullRequestResult, AgentError>> OpenPullRequestAsync(
        string repositoryPath, string sourceBranch, string targetBranch, string title, string body, CancellationToken ct)
    {
        Interlocked.Increment(ref _openCount);
        LastBody = body;
        LastTitle = title;
        LastTargetBranch = targetBranch;
        var result = new PullRequestResult(Url, "1");
        _open[sourceBranch] = result;
        return ValueTask.FromResult(Result<PullRequestResult, AgentError>.Success(result));
    }

    /// <summary>The pull request this instance opened from <paramref name="sourceBranch"/>, or null.</summary>
    public ValueTask<Result<PullRequestResult?, AgentError>> FindOpenPullRequestAsync(
        string remoteUrl, string sourceBranch, CancellationToken ct) =>
        ValueTask.FromResult(Result<PullRequestResult?, AgentError>.Success(
            _open.TryGetValue(sourceBranch, out var found) ? found : null));
}
