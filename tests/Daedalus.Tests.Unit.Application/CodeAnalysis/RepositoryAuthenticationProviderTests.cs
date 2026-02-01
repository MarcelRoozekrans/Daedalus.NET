using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Application.CodeAnalysis;

/// <summary>
///     Unit tests for repository authentication provider
/// </summary>
public class RepositoryAuthenticationProviderTests
{
    private readonly ILogger<RepositoryAuthenticationProvider> _logger;
    private readonly RepositoryAuthenticationProvider _provider;

    public RepositoryAuthenticationProviderTests()
    {
        _logger = Substitute.For<ILogger<RepositoryAuthenticationProvider>>();
        _provider = new RepositoryAuthenticationProvider(_logger);
    }

    [Fact]
    public async Task GetAuthTokenAsync_WithGitHubPlatform_ReturnsToken()
    {
        // Arrange
        const string token = "ghp_test_token_123456";
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", token);

        // Act
        var result = await _provider.GetAuthTokenAsync(RepositoryPlatform.GitHub);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(token);
    }

    [Fact]
    public async Task GetAuthTokenAsync_WithAzureDevOpsPlatform_ReturnsToken()
    {
        // Arrange
        const string token = "pattoken123456";
        Environment.SetEnvironmentVariable("AZURE_DEVOPS_TOKEN", token);

        // Act
        var result = await _provider.GetAuthTokenAsync(RepositoryPlatform.AzureDevOps);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(token);
    }

    [Fact]
    public async Task GetAuthTokenAsync_WithMissingToken_ReturnsFailure()
    {
        // Arrange
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        // Act
        var result = await _provider.GetAuthTokenAsync(RepositoryPlatform.GitHub);

        // Assert
        result.IsFailure.Should().BeTrue();
    }
}
