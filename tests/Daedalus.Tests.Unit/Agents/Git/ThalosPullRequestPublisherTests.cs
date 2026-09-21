#pragma warning disable CA1054 // URI-like parameters should not be strings — matches ThalosPullRequestPublisher's own convention

using LibGit2Sharp;
using Thalos;
using Thalos.Git;
using Daedalus.Agents.Git;
using Daedalus.Application.Services.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

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
    }

    private void InitRepositoryWithOriginRemote(string remoteUrl)
    {
        Repository.Init(_repositoryPath);
        using var repo = new Repository(_repositoryPath);
        repo.Network.Remotes.Add("origin", remoteUrl);
    }
}
