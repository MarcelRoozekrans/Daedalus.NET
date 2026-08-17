using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Tests.Integration.Builders;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Integration tests for RepositoryConfigurationRepository.
///     Uses the EF InMemory provider (via InMemoryDbContextFactory) for speed and isolation.
/// </summary>
[Collection("Database collection")]
public class RepositoryConfigurationRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly ApplicationDbContext _dbContext;
    private readonly RepositoryConfigurationRepository _repository;

    public RepositoryConfigurationRepositoryIntegrationTests()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        var logger = Substitute.For<ILogger<RepositoryConfigurationRepository>>();
        _repository = new RepositoryConfigurationRepository(_dbContext, logger);
    }

    public async Task InitializeAsync() => await _dbContext.Database.EnsureCreatedAsync();

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    #region AddAsync

    [Fact]
    public async Task AddAsync_WithValidEntity_SavesAndReturns()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(
            name: "Integration Repo",
            url: "https://github.com/org/integration-repo");

        // Act
        var result = await _repository.AddAsync(entity);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(entity.Id);
        result.Value.Name.Should().Be("Integration Repo");
        result.Value.Url.Should().Be("https://github.com/org/integration-repo");
    }

    [Fact]
    public async Task AddAsync_PersistsAllProperties()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(
            name: "Full Repo",
            url: "https://gitlab.com/org/repo",
            platform: "GitLab",
            defaultBranch: "develop",
            authenticationMethod: "PAT",
            credentialIdentifier: "vault://token",
            description: "A full repository");

        // Act
        var result = await _repository.AddAsync(entity);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Platform.Should().Be("GitLab");
        result.Value.DefaultBranch.Should().Be("develop");
        result.Value.AuthenticationMethod.Should().Be("PAT");
        result.Value.CredentialIdentifier.Should().Be("vault://token");
        result.Value.Description.Should().Be("A full repository");
        result.Value.IsActive.Should().BeTrue();
        result.Value.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AddAsync_MultiplEntities_AllPersisted()
    {
        // Arrange
        var entity1 = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 1");
        var entity2 = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 2");

        // Act
        await _repository.AddAsync(entity1);
        await _repository.AddAsync(entity2);

        // Assert
        var all = await _repository.GetAllAsync();
        all.IsSuccess.Should().BeTrue();
        all.Value.Should().HaveCount(2);
    }

    #endregion

    #region GetByIdAsync

    [Fact]
    public async Task GetByIdAsync_WithExistingId_ReturnsEntity()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Find Me");
        await _repository.AddAsync(entity);

        // Act
        var result = await _repository.GetByIdAsync(entity.Id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(entity.Id);
        result.Value.Name.Should().Be("Find Me");
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistingId_ReturnsFailure()
    {
        // Arrange
        var nonExistingId = Guid.NewGuid();

        // Act
        var result = await _repository.GetByIdAsync(nonExistingId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsCorrectEntityAmongMultiple()
    {
        // Arrange
        var entity1 = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 1");
        var entity2 = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 2");
        var entity3 = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 3");
        await _repository.AddAsync(entity1);
        await _repository.AddAsync(entity2);
        await _repository.AddAsync(entity3);

        // Act
        var result = await _repository.GetByIdAsync(entity2.Id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Repo 2");
    }

    #endregion

    #region GetByIdTrackingAsync

    [Fact]
    public async Task GetByIdTrackingAsync_WithExistingId_ReturnsEntity()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Tracked");
        await _repository.AddAsync(entity);

        // Act
        var result = await _repository.GetByIdTrackingAsync(entity.Id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(entity.Id);
        result.Value.Name.Should().Be("Tracked");
    }

    [Fact]
    public async Task GetByIdTrackingAsync_WithNonExistingId_ReturnsFailure()
    {
        // Arrange
        var nonExistingId = Guid.NewGuid();

        // Act
        var result = await _repository.GetByIdTrackingAsync(nonExistingId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task GetByIdTrackingAsync_EntityIsTracked_CanBeUpdated()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Original");
        await _repository.AddAsync(entity);

        // Act
        var tracked = await _repository.GetByIdTrackingAsync(entity.Id);
        tracked.Value.Update("Updated Name", "https://github.com/org/repo", "main",
            null, null, true, null);
        await _repository.UpdateAsync(tracked.Value);

        // Assert
        var refreshed = await _repository.GetByIdAsync(entity.Id);
        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Name.Should().Be("Updated Name");
    }

    #endregion

    #region GetAllAsync

    [Fact]
    public async Task GetAllAsync_WithNoEntities_ReturnsEmptyList()
    {
        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_WithEntities_ReturnsAllEntities()
    {
        // Arrange
        await _repository.AddAsync(IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 1"));
        await _repository.AddAsync(IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 2"));
        await _repository.AddAsync(IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 3"));

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetAllAsync_OrdersByCreatedAtDescending()
    {
        // Arrange
        var older = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Older",
            "https://github.com/org/older", createdAt: DateTime.UtcNow.AddHours(-2)).Value;
        var newer = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Newer",
            "https://github.com/org/newer", createdAt: DateTime.UtcNow.AddHours(-1)).Value;
        var newest = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Newest",
            "https://github.com/org/newest", createdAt: DateTime.UtcNow).Value;

        await _repository.AddAsync(older);
        await _repository.AddAsync(newer);
        await _repository.AddAsync(newest);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value[0].Name.Should().Be("Newest");
        result.Value[1].Name.Should().Be("Newer");
        result.Value[2].Name.Should().Be("Older");
    }

    #endregion

    #region UpdateAsync

    [Fact]
    public async Task UpdateAsync_WithTrackedEntity_PersistsChanges()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Before Update");
        await _repository.AddAsync(entity);

        var tracked = await _repository.GetByIdTrackingAsync(entity.Id);
        tracked.Value.Update("After Update", "https://github.com/org/updated", "develop",
            "SSH", "vault://key", true, "Updated desc");

        // Act
        var result = await _repository.UpdateAsync(tracked.Value);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var refreshed = await _repository.GetByIdAsync(entity.Id);
        refreshed.Value.Name.Should().Be("After Update");
        refreshed.Value.Url.Should().Be("https://github.com/org/updated");
        refreshed.Value.DefaultBranch.Should().Be("develop");
        refreshed.Value.AuthenticationMethod.Should().Be("SSH");
        refreshed.Value.Description.Should().Be("Updated desc");
        refreshed.Value.ModifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAsync_CanDeactivateEntity()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Active Repo");
        await _repository.AddAsync(entity);

        var tracked = await _repository.GetByIdTrackingAsync(entity.Id);
        tracked.Value.Update("Active Repo", "https://github.com/org/repo", "main",
            null, null, false, null);

        // Act
        var result = await _repository.UpdateAsync(tracked.Value);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var refreshed = await _repository.GetByIdAsync(entity.Id);
        refreshed.Value.IsActive.Should().BeFalse();
    }

    #endregion

    #region DeleteAsync

    [Fact]
    public async Task DeleteAsync_WithExistingId_RemovesEntity()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "To Delete");
        await _repository.AddAsync(entity);

        // Act
        var result = await _repository.DeleteAsync(entity.Id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var find = await _repository.GetByIdAsync(entity.Id);
        find.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistingId_ReturnsFailure()
    {
        // Arrange
        var nonExistingId = Guid.NewGuid();

        // Act
        var result = await _repository.DeleteAsync(nonExistingId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task DeleteAsync_DoesNotAffectOtherEntities()
    {
        // Arrange
        var entity1 = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Keep");
        var entity2 = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Delete");
        await _repository.AddAsync(entity1);
        await _repository.AddAsync(entity2);

        // Act
        await _repository.DeleteAsync(entity2.Id);

        // Assert
        var all = await _repository.GetAllAsync();
        all.Value.Should().HaveCount(1);
        all.Value[0].Name.Should().Be("Keep");
    }

    #endregion

    #region Builder Pattern Tests

    [Fact]
    public async Task Builder_CreatesActiveEntity_ByDefault()
    {
        // Arrange
        var entity = new RepositoryConfigurationTestBuilder()
            .WithName("Builder Repo")
            .WithPlatform("GitLab")
            .Build();

        // Act
        var result = await _repository.AddAsync(entity);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Builder Repo");
        result.Value.Platform.Should().Be("GitLab");
        result.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Builder_CreatesInactiveEntity()
    {
        // Arrange
        var entity = new RepositoryConfigurationTestBuilder()
            .WithName("Inactive Repo")
            .AsInactive()
            .Build();

        // Act
        var result = await _repository.AddAsync(entity);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Builder_WithAuthentication_PersistsCredentials()
    {
        // Arrange
        var entity = new RepositoryConfigurationTestBuilder()
            .WithAuthentication("PAT", "vault://github-token")
            .Build();

        // Act
        var result = await _repository.AddAsync(entity);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var fetched = await _repository.GetByIdAsync(entity.Id);
        fetched.Value.AuthenticationMethod.Should().Be("PAT");
        fetched.Value.CredentialIdentifier.Should().Be("vault://github-token");
    }

    #endregion
}
