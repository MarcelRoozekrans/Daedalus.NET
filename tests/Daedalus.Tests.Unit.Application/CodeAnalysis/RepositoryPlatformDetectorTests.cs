using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Application.CodeAnalysis;

/// <summary>
///     Unit tests for repository platform detection
/// </summary>
public class RepositoryPlatformDetectorTests
{
    private readonly RepositoryPlatformDetector _detector;
    private readonly ILogger<RepositoryPlatformDetector> _logger;

    public RepositoryPlatformDetectorTests()
    {
        _logger = Substitute.For<ILogger<RepositoryPlatformDetector>>();
        _detector = new RepositoryPlatformDetector(_logger);
    }

    [Fact]
    public void DetectPlatform_WithGitHubUrl_ReturnsGitHub()
    {
        // Arrange
        var url = "https://github.com/org/repo.git";

        // Act
        var platform = _detector.DetectPlatform(url);

        // Assert
        platform.Should().Be(RepositoryPlatform.GitHub);
    }

    [Fact]
    public void DetectPlatform_WithGitHubSshUrl_ReturnsGitHub()
    {
        // Arrange
        var url = "git@github.com:org/repo.git";

        // Act
        var platform = _detector.DetectPlatform(url);

        // Assert
        platform.Should().Be(RepositoryPlatform.GitHub);
    }

    [Fact]
    public void DetectPlatform_WithAzureDevOpsUrl_ReturnsAzureDevOps()
    {
        // Arrange
        var url = "https://dev.azure.com/org/project/_git/repo";

        // Act
        var platform = _detector.DetectPlatform(url);

        // Assert
        platform.Should().Be(RepositoryPlatform.AzureDevOps);
    }

    [Fact]
    public void DetectPlatform_WithGitLabUrl_ReturnsGitLab()
    {
        // Arrange
        var url = "https://gitlab.com/org/repo.git";

        // Act
        var platform = _detector.DetectPlatform(url);

        // Assert
        platform.Should().Be(RepositoryPlatform.GitLab);
    }

    [Fact]
    public void DetectPlatform_WithEmptyUrl_ReturnsGitHub()
    {
        // Arrange
        var url = string.Empty;

        // Act
        var platform = _detector.DetectPlatform(url);

        // Assert
        platform.Should().Be(RepositoryPlatform.GitHub);
    }

    [Fact]
    public async Task ParseRepositoryUrlAsync_WithGitHubUrl_ParsesCorrectly()
    {
        // Arrange
        var url = "https://github.com/myorg/myrepo.git";

        // Act
        var result = await _detector.ParseRepositoryUrlAsync(url);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Platform.Should().Be(RepositoryPlatform.GitHub);
        result.Value.Owner.Should().Be("myorg");
        result.Value.Repository.Should().Be("myrepo");
        result.Value.WebUrl.Should().Be("https://github.com/myorg/myrepo");
    }

    [Fact]
    public async Task ParseRepositoryUrlAsync_WithAzureDevOpsUrl_ParsesCorrectly()
    {
        // Arrange
        var url = "https://dev.azure.com/myorg/myproject/_git/myrepo";

        // Act
        var result = await _detector.ParseRepositoryUrlAsync(url);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Platform.Should().Be(RepositoryPlatform.AzureDevOps);
        result.Value.Owner.Should().Be("myorg");
        result.Value.Repository.Should().Be("myrepo");
    }

    [Fact]
    public async Task ParseRepositoryUrlAsync_WithGitLabUrl_ParsesCorrectly()
    {
        // Arrange
        var url = "https://gitlab.com/myorg/myrepo.git";

        // Act
        var result = await _detector.ParseRepositoryUrlAsync(url);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Platform.Should().Be(RepositoryPlatform.GitLab);
        result.Value.Owner.Should().Be("myorg");
        result.Value.Repository.Should().Be("myrepo");
    }

    [Fact]
    public async Task ParseRepositoryUrlAsync_WithInvalidUrl_ReturnsFailure()
    {
        // Arrange
        var url = "https://github.com/invalid-url-format";

        // Act
        var result = await _detector.ParseRepositoryUrlAsync(url);

        // Assert
        result.IsFailure.Should().BeTrue();
    }
}
