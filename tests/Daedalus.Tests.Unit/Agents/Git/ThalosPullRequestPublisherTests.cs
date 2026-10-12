#pragma warning disable CA1054 // URI-like parameters should not be strings — matches ThalosPullRequestPublisher's own convention

using System.Net;
using System.Text;
using Daedalus.Agents.Git;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;
using Thalos.Git;

namespace Daedalus.Tests.Unit.Agents.Git;

/// <summary>
///     Guards <see cref="ThalosPullRequestPublisher"/>, the seam between Thalos's working-tree-path contract
///     (<see cref="IPullRequestPublisher"/>) and Daedalus's hosting-URL contract (<see cref="IPullRequestFactory"/>).
/// </summary>
/// <remarks>
///     <see cref="ThalosPullRequestPublisher"/> does no platform detection itself — that stays entirely inside
///     <see cref="IPullRequestFactory"/>'s own dispatch (guarded separately by
///     <c>PullRequestFactoryDispatchTests</c> in Daedalus.Tests.Unit.Infrastructure), which is the whole point of
///     implementing over the factory rather than beside it. What these tests guard is the seam: that the
///     <c>origin</c> remote read from a real, local git working tree via LibGit2Sharp reaches
///     <see cref="IPullRequestFactory"/> unmodified, for a GitHub remote and an Azure DevOps remote alike, and that
///     both success and failure map onto Thalos's contract correctly.
/// </remarks>
public sealed class ThalosPullRequestPublisherTests : IDisposable
{
    private readonly string _repositoryPath =
        Path.Combine(Path.GetTempPath(), $"thalos-pr-publisher-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_repositoryPath))
        {
            return;
        }

        // LibGit2Sharp marks its pack/object files read-only; clear that before deleting or Directory.Delete throws.
        foreach (var file in Directory.EnumerateFiles(_repositoryPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_repositoryPath, recursive: true);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("https://dev.azure.com/org/project/_git/repo")]
    public async Task Forwards_the_origin_remote_url_to_the_pull_request_factory_unmodified(string remoteUrl)
    {
        InitRepositoryWithOriginRemote(remoteUrl);
        var factory = Substitute.For<IPullRequestFactory>();
        factory.CreatePullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.CodeAnalysis.PullRequestResult>.Success(
                new Daedalus.Domain.CodeAnalysis.PullRequestResult
                {
                    PullRequestId = "42",
                    WebUrl = "https://example.invalid/pr/42",
                }));
        var sut = new ThalosPullRequestPublisher(factory, NullLogger<ThalosPullRequestPublisher>.Instance);

        var result = await sut.OpenPullRequestAsync(
            _repositoryPath, "feature", "main", "title", "body", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Url.Should().Be("https://example.invalid/pr/42");
        result.Value.Id.Should().Be("42");
        await factory.Received(1).CreatePullRequestAsync(
            remoteUrl, "feature", "main", "title", "body", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_repository_with_no_origin_remote_fails_without_calling_the_factory()
    {
        Repository.Init(_repositoryPath);
        var factory = Substitute.For<IPullRequestFactory>();
        var sut = new ThalosPullRequestPublisher(factory, NullLogger<ThalosPullRequestPublisher>.Instance);

        var result = await sut.OpenPullRequestAsync(
            _repositoryPath, "feature", "main", "title", "body", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.GitRepositoryNotFound);
        await factory.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_path_that_is_not_a_git_repository_fails_without_calling_the_factory()
    {
        Directory.CreateDirectory(_repositoryPath);
        var factory = Substitute.For<IPullRequestFactory>();
        var sut = new ThalosPullRequestPublisher(factory, NullLogger<ThalosPullRequestPublisher>.Instance);

        var result = await sut.OpenPullRequestAsync(
            _repositoryPath, "feature", "main", "title", "body", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.GitRepositoryNotFound);
        await factory.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Final review M4: a repository LibGit2Sharp finds but cannot read, here one whose config does not parse, is a
    ///     failed result, never an exception out of the publish. Red: catching only <c>RepositoryNotFoundException</c>,
    ///     as before, lets the <c>LibGit2SharpException</c> escape.
    /// </summary>
    [Fact]
    public async Task A_repository_git_cannot_read_is_a_GitOperationFailed_agent_error_without_calling_the_factory()
    {
        InitRepositoryWithOriginRemote("https://github.com/owner/repo.git");
        await File.WriteAllTextAsync(Path.Combine(_repositoryPath, ".git", "config"), "[core\n\tbroken = = =\n");
        var factory = Substitute.For<IPullRequestFactory>();
        var sut = new ThalosPullRequestPublisher(factory, NullLogger<ThalosPullRequestPublisher>.Instance);

        var result = await sut.OpenPullRequestAsync(
            _repositoryPath, "feature", "main", "title", "body", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.GitOperationFailed);
        result.Error.Message.Should().Contain(_repositoryPath);
        await factory.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Red: map the failure to another code; the code assertion fails.
    ///     Red: drop the text from <c>Detail</c>; the detail assertion fails.
    ///     Red: keep the fixed "Failed to open pull request" message; the message assertion fails.
    /// </summary>
    [Fact]
    public async Task A_factory_failure_maps_to_a_GitOperationFailed_agent_error()
    {
        InitRepositoryWithOriginRemote("https://github.com/owner/repo.git");
        var factory = Substitute.For<IPullRequestFactory>();
        factory.CreatePullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.CodeAnalysis.PullRequestResult>.Failure("platform rejected the request"));
        var sut = new ThalosPullRequestPublisher(factory, NullLogger<ThalosPullRequestPublisher>.Instance);

        var result = await sut.OpenPullRequestAsync(
            _repositoryPath, "feature", "main", "title", "body", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.GitOperationFailed);
        result.Error.Detail.Should().Be("platform rejected the request");
        result.Error.Message.Should().Be("Failed to open pull request: platform rejected the request");
    }

    /// <summary>
    ///     Red: keep the fixed "Failed to look up an open pull request" message; the message assertion fails.
    ///     Red: drop the text from <c>Detail</c>; the detail assertion fails.
    /// </summary>
    [Fact]
    public async Task A_lookup_failure_says_why_in_the_message()
    {
        var factory = Substitute.For<IPullRequestFactory>();
        factory.FindOpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.CodeAnalysis.PullRequestResult?>.Failure("GitHub answered 429: rate-limited until 14:05 UTC."));
        var sut = new ThalosPullRequestPublisher(factory, NullLogger<ThalosPullRequestPublisher>.Instance);

        var result = await sut.FindOpenPullRequestAsync("https://github.com/o/r.git", "manufacture/abc", CancellationToken.None);

        result.Error.Message.Should().Be("Failed to look up an open pull request: GitHub answered 429: rate-limited until 14:05 UTC.");
        result.Error.Detail.Should().Be("GitHub answered 429: rate-limited until 14:05 UTC.");
    }

    /// <summary>
    ///     Through the real factory chain, GitHub's own words never reach the publisher's message, which the run shows and
    ///     Thalos.NET#292 puts in span status (P9), while the body-free reason does.
    ///     Red: keep the fixed "Failed to look up an open pull request" message; the 403 text is missing.
    ///     Red: pass <c>found.Error.ForAgent()</c> on in <c>GitHubPullRequestFactory.FindOpenPullRequestAsync</c>; the
    ///     body's text appears.
    /// </summary>
    [Fact]
    public async Task What_GitHub_said_never_reaches_the_publishers_message()
    {
        var handler = new Daedalus.Tests.Unit.Agents.GitHub.StubHandler().Route("/pulls", () => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message":"zqx-said"}""", Encoding.UTF8, "application/json"),
        });
        var github = new GitHubPullRequestFactory(
            Daedalus.Tests.Unit.Agents.GitHub.GitHubApiTestSupport.Build(handler), NullLogger<GitHubPullRequestFactory>.Instance);
        var factory = new PullRequestFactory(
            github,
            new AzureDevOpsPullRequestFactory(
                new HttpClient(handler), Substitute.For<IRepositoryAuthenticationProvider>(), NullLogger<AzureDevOpsPullRequestFactory>.Instance),
            new RepositoryPlatformDetector(NullLogger<RepositoryPlatformDetector>.Instance),
            NullLogger<PullRequestFactory>.Instance);
        var sut = new ThalosPullRequestPublisher(factory, NullLogger<ThalosPullRequestPublisher>.Instance);

        var result = await sut.FindOpenPullRequestAsync("https://github.com/owner/repo.git", "manufacture/abc", CancellationToken.None);

        result.Error.Message.Should().Contain("GitHub answered 403");
        result.Error.Message.Should().NotContain("zqx-said");
    }

    /// <summary>
    ///     The origin remote can carry credentials in its user info, so no log line repeats it.
    ///     Red: log <c>repositoryUrl</c> in the open failure again, as before; a line contains "SECRET".
    ///     Red: log <c>remoteUrl</c> in <c>LogLookupFailed</c> again, as before; a line contains "SECRET".
    /// </summary>
    [Fact]
    public async Task A_failure_never_logs_the_remote_url()
    {
        InitRepositoryWithOriginRemote("https://u:SECRET@github.com/owner/repo.git");
        var factory = Substitute.For<IPullRequestFactory>();
        factory.CreatePullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.CodeAnalysis.PullRequestResult>.Failure("refused"));
        factory.FindOpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.CodeAnalysis.PullRequestResult?>.Failure("refused"));
        var logger = new CapturingLogger<ThalosPullRequestPublisher>();
        var sut = new ThalosPullRequestPublisher(factory, logger);

        await sut.OpenPullRequestAsync(_repositoryPath, "feature", "main", "title", "body", CancellationToken.None);
        await sut.FindOpenPullRequestAsync("https://u:SECRET@github.com/owner/repo.git", "manufacture/abc", CancellationToken.None);

        logger.Lines.Should().Contain(line => line.Contains("refused", StringComparison.Ordinal));
        logger.Lines.Should().NotContain(line => line.Contains("SECRET", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The lookup takes the configured remote and never opens a working tree: nothing on disk exists for this
    ///     test. Falsifiable per assertion: passing a hard-coded URL, or reading <c>origin</c> from a repository
    ///     instead of <c>remoteUrl</c>, fails the <c>Received</c> check, and mapping <c>PullRequestUrl</c> instead of
    ///     <c>WebUrl</c> fails the URL assertion.
    /// </summary>
    [Fact]
    public async Task The_open_pull_request_lookup_asks_the_factory_for_the_given_remote_and_branch()
    {
        var factory = Substitute.For<IPullRequestFactory>();
        factory.FindOpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.CodeAnalysis.PullRequestResult?>.Success(
                new Daedalus.Domain.CodeAnalysis.PullRequestResult
                {
                    PullRequestId = "7",
                    PullRequestUrl = "https://api.github.com/repos/o/r/pulls/7",
                    WebUrl = "https://github.com/o/r/pull/7",
                }));
        var sut = new ThalosPullRequestPublisher(factory, NullLogger<ThalosPullRequestPublisher>.Instance);

        var result = await sut.FindOpenPullRequestAsync("https://github.com/o/r.git", "manufacture/abc", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Url.Should().Be("https://github.com/o/r/pull/7");
        await factory.Received(1).FindOpenPullRequestAsync(
            "https://github.com/o/r.git", "manufacture/abc", Arg.Any<CancellationToken>());
    }

    /// <summary>Falsifiable: mapping a factory's "none open" to a failure, or to an empty result, turns this red.</summary>
    [Fact]
    public async Task No_open_pull_request_maps_to_null()
    {
        var factory = Substitute.For<IPullRequestFactory>();
        factory.FindOpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Daedalus.Domain.CodeAnalysis.PullRequestResult?>.Success(null));
        var sut = new ThalosPullRequestPublisher(factory, NullLogger<ThalosPullRequestPublisher>.Instance);

        var result = await sut.FindOpenPullRequestAsync("https://github.com/o/r.git", "manufacture/abc", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    private void InitRepositoryWithOriginRemote(string remoteUrl)
    {
        Repository.Init(_repositoryPath);
        using var repo = new Repository(_repositoryPath);
        repo.Network.Remotes.Add("origin", remoteUrl);
    }

    /// <summary>Every formatted message and every structured value, so a value logged as a parameter is caught too.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _lines.Enqueue(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                    _lines.Enqueue(value.Value?.ToString() ?? "");
            }
        }
    }
}
