using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for RepositoryConfiguration aggregate root.
/// </summary>
public class RepositoryConfigurationEntityTests
{
    #region Create - Success Cases

    [Fact]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        // Arrange
        var id = Guid.NewGuid();

        // Act
        var result = RepositoryConfiguration.Create(id, "My Repo", "https://github.com/org/repo");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(id);
        result.Value.Name.Should().Be("My Repo");
        result.Value.Url.Should().Be("https://github.com/org/repo");
        result.Value.Platform.Should().Be("GitHub");
        result.Value.DefaultBranch.Should().Be("main");
        result.Value.IsActive.Should().BeTrue();
        result.Value.AuthenticationMethod.Should().BeNull();
        result.Value.CredentialIdentifier.Should().BeNull();
        result.Value.Description.Should().BeNull();
        result.Value.ModifiedAt.Should().BeNull();
        result.Value.LastUsedAt.Should().BeNull();
    }

    [Fact]
    public void Create_WithAllParameters_ShouldSucceed()
    {
        // Arrange
        var id = Guid.NewGuid();
        var createdAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var result = RepositoryConfiguration.Create(
            id, "Full Repo", "https://gitlab.com/org/repo",
            "GitLab", "develop", "PAT", "vault://tokens/gitlab",
            "A test repository", createdAt);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var entity = result.Value;
        entity.Name.Should().Be("Full Repo");
        entity.Url.Should().Be("https://gitlab.com/org/repo");
        entity.Platform.Should().Be("GitLab");
        entity.DefaultBranch.Should().Be("develop");
        entity.AuthenticationMethod.Should().Be("PAT");
        entity.CredentialIdentifier.Should().Be("vault://tokens/gitlab");
        entity.Description.Should().Be("A test repository");
        entity.CreatedAt.Should().Be(createdAt);
    }

    [Fact]
    public void Create_SetsCreatedAtToUtcNow_WhenNotProvided()
    {
        // Arrange
        var beforeCreation = DateTime.UtcNow;

        // Act
        var result = RepositoryConfiguration.Create(Guid.NewGuid(), "Repo", "https://github.com/org/repo");
        var afterCreation = DateTime.UtcNow;

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CreatedAt.Should().BeOnOrAfter(beforeCreation);
        result.Value.CreatedAt.Should().BeOnOrBefore(afterCreation);
    }

    [Fact]
    public void Create_TrimsInputs()
    {
        // Arrange & Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(),
            "  My Repo  ",
            "  https://github.com/org/repo  ",
            "GitHub",
            "  main  ",
            "  PAT  ",
            "  cred-id  ",
            "  A description  ");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("My Repo");
        result.Value.Url.Should().Be("https://github.com/org/repo");
        result.Value.DefaultBranch.Should().Be("main");
        result.Value.AuthenticationMethod.Should().Be("PAT");
        result.Value.CredentialIdentifier.Should().Be("cred-id");
        result.Value.Description.Should().Be("A description");
    }

    [Fact]
    public void Create_WithEmptyDefaultBranch_DefaultsToMain()
    {
        // Arrange & Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://github.com/org/repo",
            defaultBranch: "");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.DefaultBranch.Should().Be("main");
    }

    [Fact]
    public void Create_WithWhitespaceDefaultBranch_DefaultsToMain()
    {
        // Arrange & Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://github.com/org/repo",
            defaultBranch: "   ");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.DefaultBranch.Should().Be("main");
    }

    [Fact]
    public void Create_WithNullAuthenticationMethod_ShouldBeNull()
    {
        // Arrange & Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://github.com/org/repo",
            authenticationMethod: null);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.AuthenticationMethod.Should().BeNull();
    }

    [Fact]
    public void Create_InitializesIsActiveToTrue()
    {
        // Arrange & Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://github.com/org/repo");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsActive.Should().BeTrue();
    }

    #endregion

    #region Create - Platform Validation

    [Theory]
    [InlineData("GitHub")]
    [InlineData("GitLab")]
    [InlineData("AzureDevOps")]
    [InlineData("Bitbucket")]
    [InlineData("Gitea")]
    public void Create_WithValidPlatform_ShouldSucceed(string platform)
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://example.com/repo", platform);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Platform.Should().Be(platform);
    }

    [Fact]
    public void Create_WithInvalidPlatform_ShouldFail()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://example.com/repo", "Subversion");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Platform must be one of");
    }

    [Fact]
    public void Create_WithEmptyPlatform_ShouldFail()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://example.com/repo", "");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Platform cannot be empty");
    }

    [Fact]
    public void Create_WithWhitespacePlatform_ShouldFail()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://example.com/repo", "   ");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Platform cannot be empty");
    }

    #endregion

    #region Create - Name Validation

    [Fact]
    public void Create_WithNullName_ShouldFail()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), null!, "https://example.com/repo");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name cannot be empty");
    }

    [Fact]
    public void Create_WithEmptyName_ShouldFail()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "", "https://example.com/repo");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name cannot be empty");
    }

    [Fact]
    public void Create_WithWhitespaceName_ShouldFail()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "   ", "https://example.com/repo");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name cannot be empty");
    }

    [Fact]
    public void Create_WithNameExceeding256Characters_ShouldFail()
    {
        // Arrange
        var longName = new string('a', 257);

        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), longName, "https://example.com/repo");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name cannot exceed 256 characters");
    }

    [Fact]
    public void Create_WithNameAtMaxLength_ShouldSucceed()
    {
        // Arrange
        var maxName = new string('a', 256);

        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), maxName, "https://example.com/repo");

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region Create - URL Validation

    [Fact]
    public void Create_WithNullUrl_ShouldFail()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", null!);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("URL cannot be empty");
    }

    [Fact]
    public void Create_WithEmptyUrl_ShouldFail()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("URL cannot be empty");
    }

    [Fact]
    public void Create_WithWhitespaceUrl_ShouldFail()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "   ");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("URL cannot be empty");
    }

    [Fact]
    public void Create_WithUrlExceeding1024Characters_ShouldFail()
    {
        // Arrange
        var longUrl = "https://example.com/" + new string('a', 1005);

        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", longUrl);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("URL cannot exceed 1024 characters");
    }

    #endregion

    #region Create - Description Validation

    [Fact]
    public void Create_WithDescriptionExceeding2000Characters_ShouldFail()
    {
        // Arrange
        var longDescription = new string('a', 2001);

        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://example.com/repo",
            description: longDescription);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Description cannot exceed 2000 characters");
    }

    [Fact]
    public void Create_WithDescriptionAtMaxLength_ShouldSucceed()
    {
        // Arrange
        var maxDescription = new string('a', 2000);

        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://example.com/repo",
            description: maxDescription);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_WithNullDescription_ShouldSucceed()
    {
        // Act
        var result = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Repo", "https://example.com/repo",
            description: null);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Description.Should().BeNull();
    }

    #endregion

    #region Update

    [Fact]
    public void Update_WithValidParameters_ShouldSucceed()
    {
        // Arrange
        var entity = CreateDefaultEntity();

        // Act
        var result = entity.Update(
            "Updated Repo", "https://gitlab.com/org/updated", "develop",
            "SSH", "vault://ssh-key", true, "Updated description");

        // Assert
        result.IsSuccess.Should().BeTrue();
        entity.Name.Should().Be("Updated Repo");
        entity.Url.Should().Be("https://gitlab.com/org/updated");
        entity.DefaultBranch.Should().Be("develop");
        entity.AuthenticationMethod.Should().Be("SSH");
        entity.CredentialIdentifier.Should().Be("vault://ssh-key");
        entity.IsActive.Should().BeTrue();
        entity.Description.Should().Be("Updated description");
        entity.ModifiedAt.Should().NotBeNull();
        entity.ModifiedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Update_SetsModifiedAt()
    {
        // Arrange
        var entity = CreateDefaultEntity();
        var beforeUpdate = DateTime.UtcNow;

        // Act
        entity.Update("Updated", "https://github.com/org/repo", "main",
            null, null, true, null);
        var afterUpdate = DateTime.UtcNow;

        // Assert
        entity.ModifiedAt.Should().NotBeNull();
        entity.ModifiedAt!.Value.Should().BeOnOrAfter(beforeUpdate);
        entity.ModifiedAt!.Value.Should().BeOnOrBefore(afterUpdate);
    }

    [Fact]
    public void Update_WithEmptyName_ShouldFail()
    {
        // Arrange
        var entity = CreateDefaultEntity();

        // Act
        var result = entity.Update("", "https://github.com/org/repo", "main",
            null, null, true, null);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name cannot be empty");
    }

    [Fact]
    public void Update_WithNameExceeding256Characters_ShouldFail()
    {
        // Arrange
        var entity = CreateDefaultEntity();
        var longName = new string('a', 257);

        // Act
        var result = entity.Update(longName, "https://github.com/org/repo", "main",
            null, null, true, null);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name cannot exceed 256 characters");
    }

    [Fact]
    public void Update_WithEmptyUrl_ShouldFail()
    {
        // Arrange
        var entity = CreateDefaultEntity();

        // Act
        var result = entity.Update("Repo", "", "main", null, null, true, null);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("URL cannot be empty");
    }

    [Fact]
    public void Update_WithUrlExceeding1024Characters_ShouldFail()
    {
        // Arrange
        var entity = CreateDefaultEntity();
        var longUrl = "https://example.com/" + new string('a', 1005);

        // Act
        var result = entity.Update("Repo", longUrl, "main", null, null, true, null);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("URL cannot exceed 1024 characters");
    }

    [Fact]
    public void Update_WithDescriptionExceeding2000Characters_ShouldFail()
    {
        // Arrange
        var entity = CreateDefaultEntity();
        var longDescription = new string('a', 2001);

        // Act
        var result = entity.Update("Repo", "https://github.com/org/repo", "main",
            null, null, true, longDescription);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Description cannot exceed 2000 characters");
    }

    [Fact]
    public void Update_TrimsInputs()
    {
        // Arrange
        var entity = CreateDefaultEntity();

        // Act
        entity.Update("  Updated  ", "  https://github.com/org/repo  ", "  develop  ",
            "  PAT  ", "  cred  ", true, "  desc  ");

        // Assert
        entity.Name.Should().Be("Updated");
        entity.Url.Should().Be("https://github.com/org/repo");
        entity.DefaultBranch.Should().Be("develop");
        entity.AuthenticationMethod.Should().Be("PAT");
        entity.CredentialIdentifier.Should().Be("cred");
        entity.Description.Should().Be("desc");
    }

    [Fact]
    public void Update_WithEmptyDefaultBranch_DefaultsToMain()
    {
        // Arrange
        var entity = CreateDefaultEntity();

        // Act
        entity.Update("Repo", "https://github.com/org/repo", "",
            null, null, true, null);

        // Assert
        entity.DefaultBranch.Should().Be("main");
    }

    [Fact]
    public void Update_CanDeactivate()
    {
        // Arrange
        var entity = CreateDefaultEntity();

        // Act
        entity.Update("Repo", "https://github.com/org/repo", "main",
            null, null, false, null);

        // Assert
        entity.IsActive.Should().BeFalse();
    }

    #endregion

    #region RecordUsage

    [Fact]
    public void RecordUsage_SetsLastUsedAt()
    {
        // Arrange
        var entity = CreateDefaultEntity();
        var beforeUsage = DateTime.UtcNow;

        // Act
        entity.RecordUsage();
        var afterUsage = DateTime.UtcNow;

        // Assert
        entity.LastUsedAt.Should().NotBeNull();
        entity.LastUsedAt!.Value.Should().BeOnOrAfter(beforeUsage);
        entity.LastUsedAt!.Value.Should().BeOnOrBefore(afterUsage);
    }

    [Fact]
    public void RecordUsage_UpdatesLastUsedAt_OnSubsequentCalls()
    {
        // Arrange
        var entity = CreateDefaultEntity();
        entity.RecordUsage();
        var firstUsage = entity.LastUsedAt;

        // Act (small delay to ensure timestamp difference)
        entity.RecordUsage();

        // Assert
        entity.LastUsedAt.Should().BeOnOrAfter(firstUsage!.Value);
    }

    #endregion

    #region Deactivate

    [Fact]
    public void Deactivate_WhenActive_ShouldSucceed()
    {
        // Arrange
        var entity = CreateDefaultEntity();

        // Act
        var result = entity.Deactivate();

        // Assert
        result.IsSuccess.Should().BeTrue();
        entity.IsActive.Should().BeFalse();
        entity.ModifiedAt.Should().NotBeNull();
        entity.ModifiedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Deactivate_WhenAlreadyInactive_ShouldFail()
    {
        // Arrange
        var entity = CreateDefaultEntity();
        entity.Deactivate();

        // Act
        var result = entity.Deactivate();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already inactive");
    }

    [Fact]
    public void Deactivate_SetsModifiedAt()
    {
        // Arrange
        var entity = CreateDefaultEntity();
        var beforeDeactivation = DateTime.UtcNow;

        // Act
        entity.Deactivate();
        var afterDeactivation = DateTime.UtcNow;

        // Assert
        entity.ModifiedAt.Should().NotBeNull();
        entity.ModifiedAt!.Value.Should().BeOnOrAfter(beforeDeactivation);
        entity.ModifiedAt!.Value.Should().BeOnOrBefore(afterDeactivation);
    }

    #endregion

    #region Helpers

    private static RepositoryConfiguration CreateDefaultEntity()
    {
        return RepositoryConfiguration.Create(
            Guid.NewGuid(),
            "Test Repo",
            "https://github.com/org/repo"
        ).Value;
    }

    #endregion
}
