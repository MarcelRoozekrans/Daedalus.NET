using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Integration.CodeAnalysis;

/// <summary>
///     Integration tests for repository platform detection with real URLs
/// </summary>
public class RepositoryPlatformDetectionIntegrationTests
{
    private readonly RepositoryPlatformDetector _detector;

    public RepositoryPlatformDetectionIntegrationTests()
    {
        var logger = Substitute.For<ILogger<RepositoryPlatformDetector>>();
        _detector = new RepositoryPlatformDetector(logger);
    }

    [Theory]
    [InlineData("https://github.com/aspnet/AspNetCore.git")]
    [InlineData("git@github.com:aspnet/AspNetCore.git")]
    [InlineData("https://github.com/aspnet/AspNetCore")]
    public async Task ParseRepositoryUrlAsync_WithRealGitHubUrls_ParsesSuccessfully(string url)
    {
        // Act
        var result = await _detector.ParseRepositoryUrlAsync(url);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Platform.Should().Be(RepositoryPlatform.GitHub);
        result.Value.Owner.Should().Be("aspnet");
        result.Value.Repository.Should().Be("AspNetCore");
    }

    [Theory]
    [InlineData("https://dev.azure.com/microsoft/TypeScript/_git/TypeScript")]
    public async Task ParseRepositoryUrlAsync_WithRealAzureDevOpsUrls_ParsesSuccessfully(string url)
    {
        // Act
        var result = await _detector.ParseRepositoryUrlAsync(url);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Platform.Should().Be(RepositoryPlatform.AzureDevOps);
    }

    [Theory]
    [InlineData("https://gitlab.com/gitlab-org/gitlab.git")]
    [InlineData("git@gitlab.com:gitlab-org/gitlab.git")]
    public async Task ParseRepositoryUrlAsync_WithRealGitLabUrls_ParsesSuccessfully(string url)
    {
        // Act
        var result = await _detector.ParseRepositoryUrlAsync(url);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Platform.Should().Be(RepositoryPlatform.GitLab);
    }
}
