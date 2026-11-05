using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Tests for <see cref="BrainstormRepository" />, moved from <c>Daedalus.Tests.Unit.Infrastructure</c>, where they ran
///     on EF Core's InMemory provider, when the repository moved to ZeroAlloc.ORM (ruling R82). They run against the
///     migrated schema; the assertions are the ones they had.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class BrainstormRepositoryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly BrainstormRepository _repository =
        new(fixture.Db, Substitute.For<ILogger<BrainstormRepository>>());

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region AddAsync

    [Fact]
    public async Task AddAsync_WithValidSession_ShouldPersist()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;

        // Act
        var result = await _repository.AddAsync(session, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var stored = await FindAsync(session.Id);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task AddAsync_PersistsAllProperties()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var session = BrainstormSession.Create(projectId).Value;

        // Act
        var result = await _repository.AddAsync(session, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(session.Id);
        result.Value.ProjectId.Should().Be(projectId);
        result.Value.Phase.Should().Be(BrainstormPhase.ContextGathering);
        result.Value.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AddAsync_MultipleEntities_AllPersisted()
    {
        // Arrange
        var session1 = BrainstormSession.Create(Guid.NewGuid()).Value;
        var session2 = BrainstormSession.Create(Guid.NewGuid()).Value;

        // Act
        await _repository.AddAsync(session1, CancellationToken.None);
        await _repository.AddAsync(session2, CancellationToken.None);

        // Assert
        var count = await CountSessionsAsync();
        count.Should().Be(2);
    }

    #endregion

    #region GetByIdAsync

    [Fact]
    public async Task GetByIdAsync_WithExistingSession_ShouldReturnSession()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        await StoreAsync(session);

        // Act
        var result = await _repository.GetByIdAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(session.Id);
    }

    [Fact]
    public async Task GetByIdAsync_WithExistingSession_ShouldReturnWithMessages()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        session.AddMessage(MessageRole.Assistant, "Hello!");
        await StoreAsync(session);

        // Act
        var result = await _repository.GetByIdAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Messages.Should().HaveCount(1);
        result.Value.Messages[0].Content.Should().Be("Hello!");
        result.Value.Messages[0].Role.Should().Be(MessageRole.Assistant);
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistentId_ShouldReturnFailure()
    {
        // Act
        var result = await _repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsCorrectSessionAmongMultiple()
    {
        // Arrange
        var session1 = BrainstormSession.Create(Guid.NewGuid()).Value;
        var session2 = BrainstormSession.Create(Guid.NewGuid()).Value;
        var session3 = BrainstormSession.Create(Guid.NewGuid()).Value;
        await StoreAsync(session1, session2, session3);

        // Act
        var result = await _repository.GetByIdAsync(session2.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(session2.Id);
    }

    #endregion

    #region GetByProjectIdAsync

    [Fact]
    public async Task GetByProjectIdAsync_ShouldReturnSessionsForProject()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var session1 = BrainstormSession.Create(projectId).Value;
        var session2 = BrainstormSession.Create(projectId).Value;
        var otherSession = BrainstormSession.Create(Guid.NewGuid()).Value;
        await StoreAsync(session1, session2, otherSession);

        // Act
        var result = await _repository.GetByProjectIdAsync(projectId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().AllSatisfy(s => s.ProjectId.Should().Be(projectId));
    }

    [Fact]
    public async Task GetByProjectIdAsync_WithNoSessions_ShouldReturnEmptyList()
    {
        // Act
        var result = await _repository.GetByProjectIdAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByProjectIdAsync_ShouldOrderByCreatedAtDescending()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var session1 = BrainstormSession.Create(projectId).Value;
        var session2 = BrainstormSession.Create(projectId).Value;
        await StoreAsync(session1, session2);

        // Act
        var result = await _repository.GetByProjectIdAsync(projectId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    #endregion

    #region UpdateAsync

    [Fact]
    public async Task UpdateAsync_ShouldPersistChanges()
    {
        // Arrange
        var added = BrainstormSession.Create(Guid.NewGuid()).Value;
        await _repository.AddAsync(added, CancellationToken.None);
        var session = await ReadAsync(added.Id);

        session.SetDesignDocument("# Design\nInitial design.");
        session.SetImplementationPlan("# Plan\nPhase 1.");

        // Act
        var result = await _repository.UpdateAsync(session, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var refreshed = await FindAsync(session.Id);
        refreshed!.DesignDocument.Should().Be("# Design\nInitial design.");
        refreshed!.ImplementationPlan.Should().Be("# Plan\nPhase 1.");
    }

    [Fact]
    public async Task UpdateAsync_WithDesignDocument_ShouldPersist()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        await StoreAsync(session);
        session = await ReadAsync(session.Id);

        session.SetDesignDocument("# Design\nSome design content.");

        // Act
        var result = await _repository.UpdateAsync(session, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var refreshed = await FindAsync(session.Id);
        refreshed!.DesignDocument.Should().Be("# Design\nSome design content.");
    }

    [Fact]
    public async Task UpdateAsync_WithImplementationPlan_ShouldPersist()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        await StoreAsync(session);
        session = await ReadAsync(session.Id);

        session.SetImplementationPlan("# Plan\nStep 1: Do things.");

        // Act
        var result = await _repository.UpdateAsync(session, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var refreshed = await FindAsync(session.Id);
        refreshed!.ImplementationPlan.Should().Be("# Plan\nStep 1: Do things.");
    }

    #endregion

    /// <summary>Stores the sessions through the repository, where the InMemory tests added them to the context.</summary>
    private async Task StoreAsync(params BrainstormSession[] sessions)
    {
        foreach (var session in sessions)
        {
            (await _repository.AddAsync(session, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }
    }

    /// <summary>
    ///     Reads the stored session back, with the row version its guarded save needs: an add reads none back (rulings
    ///     R58, R77), where EF's context tracked the added instance.
    /// </summary>
    private async Task<BrainstormSession> ReadAsync(Guid id) =>
        (await _repository.GetByIdAsync(id, CancellationToken.None)).Value;

    /// <summary>The stored session, or null, where the InMemory tests used the context's <c>FindAsync</c>.</summary>
    private async Task<BrainstormSession?> FindAsync(Guid id)
    {
        var result = await _repository.GetByIdAsync(id, CancellationToken.None);
        return result.IsSuccess ? result.Value : null;
    }

    private async Task<long> CountSessionsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"BrainstormSessions\"", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
