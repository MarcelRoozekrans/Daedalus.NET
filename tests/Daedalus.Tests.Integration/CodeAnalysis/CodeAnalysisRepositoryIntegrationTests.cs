using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Integration.CodeAnalysis;

/// <summary>
///     Integration tests for CodeAnalysisRepository
/// </summary>
[Collection("Database collection")]
public class CodeAnalysisRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<CodeAnalysisRepository> _logger;
    private readonly CodeAnalysisRepository _repository;

    public CodeAnalysisRepositoryIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _logger = Substitute.For<ILogger<CodeAnalysisRepository>>();
        _repository = new CodeAnalysisRepository(_dbContext, _logger);
    }

    public async Task InitializeAsync() => await _dbContext.Database.EnsureCreatedAsync();

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_WithValidRequest_SavesAndReturns()
    {
        // Arrange
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test Analysis",
            "Test description",
            "https://github.com/org/repo",
            10,
            AnalysisType.Refactor,
            "src/test.cs",
            "[]");
        createResult.IsSuccess.Should().BeTrue();
        var request = createResult.Value;

        // Act
        var result = await _repository.CreateAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().NotBeEmpty();
        result.Value.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.Value.Status.Should().Be(AnalysisStatus.Pending);
    }

    [Fact]
    public async Task GetByIdAsync_WithExistingId_ReturnsRequest()
    {
        // Arrange
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test Analysis",
            "Test description",
            "https://github.com/org/repo",
            10,
            AnalysisType.BugFix,
            "src/test.cs");
        createResult.IsSuccess.Should().BeTrue();
        var request = createResult.Value;

        var createdResult = await _repository.CreateAsync(request);
        var requestId = createdResult.Value.Id;

        // Act
        var result = await _repository.GetByIdAsync(requestId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(requestId);
        result.Value.Title.Should().Be("Test Analysis");
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
    public async Task GetPendingAsync_ReturnsPendingRequests()
    {
        // Arrange
        var createResult1 = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Analysis 1",
            "Description",
            "https://github.com/org/repo1",
            10,
            AnalysisType.Refactor,
            "src/test1.cs");
        createResult1.IsSuccess.Should().BeTrue();
        var request1 = createResult1.Value;

        var createResult2 = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Analysis 2",
            "Description",
            "https://github.com/org/repo2",
            10,
            AnalysisType.BugFix,
            "src/test2.cs");
        createResult2.IsSuccess.Should().BeTrue();
        var request2 = createResult2.Value;

        await _repository.CreateAsync(request1);
        await _repository.CreateAsync(request2);

        // Act
        var result = await _repository.GetPendingAsync(10);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].Priority.Should().Be(0); // Default priority from factory
    }

    [Fact]
    public async Task UpdateStatusAsync_ChangesStatus()
    {
        // Arrange
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test",
            "Description",
            "https://github.com/org/repo",
            10,
            AnalysisType.Refactor,
            "src/test.cs");
        createResult.IsSuccess.Should().BeTrue();
        var request = createResult.Value;

        var createdResult = await _repository.CreateAsync(request);
        var requestId = createdResult.Value.Id;

        // Transition through domain state machine: Pending → Ready (required before AnalysisInProgress)
        request.InitializeRepository("/tmp/worktree", "feature/analysis");
        await _dbContext.SaveChangesAsync();

        // Act
        await _repository.UpdateStatusAsync(requestId, AnalysisStatus.AnalysisInProgress);
        var updated = await _repository.GetByIdAsync(requestId);

        // Assert
        updated.IsSuccess.Should().BeTrue();
        updated.Value.Status.Should().Be(AnalysisStatus.AnalysisInProgress);
    }

    [Fact]
    public async Task UpdateIterationAsync_RecordsIteration()
    {
        // Arrange
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test",
            "Description",
            "https://github.com/org/repo",
            10,
            AnalysisType.Refactor,
            "src/test.cs");
        createResult.IsSuccess.Should().BeTrue();
        var request = createResult.Value;

        var createdResult = await _repository.CreateAsync(request);
        var requestId = createdResult.Value.Id;

        // Transition through domain state machine: Pending → Ready → AnalysisInProgress
        request.InitializeRepository("/tmp/worktree", "feature/analysis");
        request.StartAnalysis();
        await _dbContext.SaveChangesAsync();

        const string prompt = "Optimize the code";
        const string response = "Here's the optimized version...";

        // Act
        await _repository.UpdateIterationAsync(requestId, 1, prompt, response);
        var updated = await _repository.GetByIdAsync(requestId);

        // Assert
        updated.IsSuccess.Should().BeTrue();
        updated.Value.CurrentIteration.Should().Be(1);
        updated.Value.Iterations.Should().HaveCount(1);
        updated.Value.Iterations[0].PromptSent.Should().Be(prompt);
        updated.Value.Iterations[0].AiResponse.Should().Be(response);
    }

    [Fact]
    public async Task CompleteAsync_MarksAsCompleted()
    {
        // Arrange
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test",
            "Description",
            "https://github.com/org/repo",
            10,
            AnalysisType.Refactor,
            "src/test.cs");
        createResult.IsSuccess.Should().BeTrue();
        var request = createResult.Value;

        var createdResult = await _repository.CreateAsync(request);
        var requestId = createdResult.Value.Id;

        // Transition through domain state machine: Pending → Ready → AnalysisInProgress
        request.InitializeRepository("/tmp/worktree", "feature/analysis");
        request.StartAnalysis();
        await _dbContext.SaveChangesAsync();

        // Act
        await _repository.CompleteAsync(requestId, "https://github.com/org/repo/pull/1", "abc123def456");
        var completed = await _repository.GetByIdAsync(requestId);

        // Assert
        completed.IsSuccess.Should().BeTrue();
        completed.Value.Status.Should().Be(AnalysisStatus.Completed);
        completed.Value.Outcome.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/1");
        completed.Value.Outcome.CommitShaFinal.Should().Be("abc123def456");
        completed.Value.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task FailAsync_MarksAsFailed()
    {
        // Arrange
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test",
            "Description",
            "https://github.com/org/repo",
            10,
            AnalysisType.Refactor,
            "src/test.cs");
        createResult.IsSuccess.Should().BeTrue();
        var request = createResult.Value;

        var createdResult = await _repository.CreateAsync(request);
        var requestId = createdResult.Value.Id;

        // Act
        await _repository.FailAsync(requestId, "Test failure reason");
        var failed = await _repository.GetByIdAsync(requestId);

        // Assert
        failed.IsSuccess.Should().BeTrue();
        failed.Value.Status.Should().Be(AnalysisStatus.Failed);
        failed.Value.FailureReason.Should().Be("Test failure reason");
    }

    [Fact]
    public async Task CancelAsync_MarksAsCancelled()
    {
        // Arrange
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test",
            "Description",
            "https://github.com/org/repo",
            10,
            AnalysisType.Refactor,
            "src/test.cs");
        createResult.IsSuccess.Should().BeTrue();
        var request = createResult.Value;

        var createdResult = await _repository.CreateAsync(request);
        var requestId = createdResult.Value.Id;

        // Act
        await _repository.CancelAsync(requestId);
        var cancelled = await _repository.GetByIdAsync(requestId);

        // Assert
        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value.Status.Should().Be(AnalysisStatus.Cancelled);
    }

    [Fact]
    public async Task GetByRepositoryAsync_ReturnsRequestsForRepository()
    {
        // Arrange
        const string repoUrl = "https://github.com/org/repo";

        var createResult1 = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Analysis 1",
            "Description",
            repoUrl,
            10,
            AnalysisType.Refactor,
            "src/test1.cs");
        createResult1.IsSuccess.Should().BeTrue();
        var request1 = createResult1.Value;

        var createResult2 = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Analysis 2",
            "Description",
            "https://github.com/other/repo",
            10,
            AnalysisType.BugFix,
            "src/test2.cs");
        createResult2.IsSuccess.Should().BeTrue();
        var request2 = createResult2.Value;

        await _repository.CreateAsync(request1);
        await _repository.CreateAsync(request2);

        // Act
        var result = await _repository.GetByRepositoryAsync(repoUrl);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Repository.Url.Should().Be(repoUrl);
    }

    [Fact]
    public async Task GetByStatusAsync_ReturnsRequestsByStatus()
    {
        // Arrange
        var createResult1 = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Analysis 1",
            "Description",
            "https://github.com/org/repo1",
            10,
            AnalysisType.Refactor,
            "src/test1.cs");
        createResult1.IsSuccess.Should().BeTrue();
        var request1 = createResult1.Value;

        var createResult2 = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Analysis 2",
            "Description",
            "https://github.com/org/repo2",
            10,
            AnalysisType.BugFix,
            "src/test2.cs");
        createResult2.IsSuccess.Should().BeTrue();
        var request2 = createResult2.Value;

        var created1 = await _repository.CreateAsync(request1);
        await _repository.CreateAsync(request2);

        // Transition request1 through domain state machine: Pending → Ready → AnalysisInProgress → Completed
        request1.InitializeRepository("/tmp/worktree", "feature/analysis");
        request1.StartAnalysis();
        await _dbContext.SaveChangesAsync();

        await _repository.CompleteAsync(created1.Value.Id);

        // Act
        var result = await _repository.GetByStatusAsync(AnalysisStatus.Completed);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Status.Should().Be(AnalysisStatus.Completed);
    }
}
