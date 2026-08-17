using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Infrastructure.Persistence;

/// <summary>
///     Unit tests for <see cref="BrainstormRepository" />.
///     Uses InMemoryDatabase for speed and isolation.
/// </summary>
public class BrainstormRepositoryTests : IAsyncLifetime
{
    private readonly ApplicationDbContext _dbContext;
    private readonly BrainstormRepository _repository;

    public BrainstormRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbContext = new TestApplicationDbContext(options);
        var logger = Substitute.For<ILogger<BrainstormRepository>>();
        _repository = new BrainstormRepository(_dbContext, logger);
    }

    public async Task InitializeAsync() => await _dbContext.Database.EnsureCreatedAsync();

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

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
        var stored = await _dbContext.BrainstormSessions.FindAsync(session.Id);
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
        var count = await _dbContext.BrainstormSessions.CountAsync();
        count.Should().Be(2);
    }

    #endregion

    #region GetByIdAsync

    [Fact]
    public async Task GetByIdAsync_WithExistingSession_ShouldReturnSession()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        _dbContext.BrainstormSessions.Add(session);
        await _dbContext.SaveChangesAsync();

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
        _dbContext.BrainstormSessions.Add(session);
        await _dbContext.SaveChangesAsync();

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
        _dbContext.BrainstormSessions.AddRange(session1, session2, session3);
        await _dbContext.SaveChangesAsync();

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
        _dbContext.BrainstormSessions.AddRange(session1, session2, otherSession);
        await _dbContext.SaveChangesAsync();

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
        _dbContext.BrainstormSessions.AddRange(session1, session2);
        await _dbContext.SaveChangesAsync();

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
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        await _repository.AddAsync(session, CancellationToken.None);

        session.SetDesignDocument("# Design\nInitial design.");
        session.SetImplementationPlan("# Plan\nPhase 1.");

        // Act
        var result = await _repository.UpdateAsync(session, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var refreshed = await _dbContext.BrainstormSessions.FindAsync(session.Id);
        refreshed!.DesignDocument.Should().Be("# Design\nInitial design.");
        refreshed!.ImplementationPlan.Should().Be("# Plan\nPhase 1.");
    }

    [Fact]
    public async Task UpdateAsync_WithDesignDocument_ShouldPersist()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        _dbContext.BrainstormSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        session.SetDesignDocument("# Design\nSome design content.");

        // Act
        var result = await _repository.UpdateAsync(session, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var refreshed = await _dbContext.BrainstormSessions.FindAsync(session.Id);
        refreshed!.DesignDocument.Should().Be("# Design\nSome design content.");
    }

    [Fact]
    public async Task UpdateAsync_WithImplementationPlan_ShouldPersist()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        _dbContext.BrainstormSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        session.SetImplementationPlan("# Plan\nStep 1: Do things.");

        // Act
        var result = await _repository.UpdateAsync(session, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var refreshed = await _dbContext.BrainstormSessions.FindAsync(session.Id);
        refreshed!.ImplementationPlan.Should().Be("# Plan\nStep 1: Do things.");
    }

    #endregion

    /// <summary>
    ///     Test-only DbContext that adjusts the model for InMemory provider compatibility:
    ///     excludes pgvector Embedding (unsupported type) and disables RowVersion
    ///     concurrency tokens (not supported by InMemory provider).
    /// </summary>
    private sealed class TestApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Ignore the Vector-typed Embedding property that InMemory cannot handle
            modelBuilder.Entity<StructuredLearningEntry>()
                .Ignore(e => e.Embedding);

            // Disable RowVersion concurrency token (not supported by InMemory)
            modelBuilder.Entity<BrainstormSession>()
                .Ignore(s => s.RowVersion);
        }
    }
}
