using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Tests.Unit.Application.CodeAnalysis;

/// <summary>
///     Unit tests for repository value objects
/// </summary>
public class RepositoryInfoTests
{
    [Fact]
    public void RepositoryInfo_WithGitHubData_IsValid()
    {
        // Arrange & Act
        var info = new RepositoryInfo
        {
            Platform = RepositoryPlatform.GitHub,
            Owner = "org",
            Repository = "repo",
            HttpsUrl = "https://github.com/org/repo.git",
            WebUrl = "https://github.com/org/repo"
        };

        // Assert
        info.Platform.Should().Be(RepositoryPlatform.GitHub);
        info.Owner.Should().Be("org");
        info.Repository.Should().Be("repo");
    }

    [Fact]
    public void RepositoryInfo_Equality_WorksCorrectly()
    {
        // Arrange
        var info1 = new RepositoryInfo { Platform = RepositoryPlatform.GitHub, Owner = "org", Repository = "repo" };

        var info2 = new RepositoryInfo { Platform = RepositoryPlatform.GitHub, Owner = "org", Repository = "repo" };

        // Act & Assert
        info1.Should().Be(info2);
    }
}
