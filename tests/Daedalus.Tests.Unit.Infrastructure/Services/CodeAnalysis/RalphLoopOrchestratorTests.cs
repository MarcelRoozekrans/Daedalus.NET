using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Unit tests for RalphLoopOrchestrator
/// </summary>
public class RalphLoopOrchestratorTests
{
    private static readonly string[] requirements = new[] { "requirement1", "requirement2" };
    private static readonly string[] requirementsArray = new[] { "req1" };
    private readonly IGitChangeApplier _changeApplierMock = Substitute.For<IGitChangeApplier>();
    private readonly IRepositoryCodeExtractor _codeExtractorMock = Substitute.For<IRepositoryCodeExtractor>();
    private readonly IGitRepositoryManager _gitManagerMock = Substitute.For<IGitRepositoryManager>();
    private readonly ILogger<RalphLoopOrchestrator> _loggerMock = Substitute.For<ILogger<RalphLoopOrchestrator>>();
    private readonly IPullRequestFactory _prFactoryMock = Substitute.For<IPullRequestFactory>();
    private readonly IAnalysisPromptBuilder _promptBuilderMock = Substitute.For<IAnalysisPromptBuilder>();
    private readonly ICodeAnalysisRepository _repositoryMock = Substitute.For<ICodeAnalysisRepository>();

    private RalphLoopOrchestrator CreateOrchestrator()
    {
        return new RalphLoopOrchestrator(
            _repositoryMock,
            _gitManagerMock,
            _codeExtractorMock,
            _promptBuilderMock,
            _changeApplierMock,
            _prFactoryMock,
            _loggerMock);
    }

    #region SubmitAnalysisAsync Tests

    [Fact]
    public async Task SubmitAnalysisAsync_WithValidRequest_CreatesAnalysisAndReturnsId()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();

        // Create a request that the repository will return
        var returnedRequestId = Guid.NewGuid();
        var returnedRequestResult = CodeAnalysisRequest.Create(
            returnedRequestId,
            "Test Analysis",
            "Test Description",
            "https://github.com/owner/repo",
            15,
            AnalysisType.BugFix,
            "src/Program.cs",
            "Test requirements",
            "main");
        var returnedRequest = returnedRequestResult.Value;

        // Mock the repository to return the request (simulates persistent storage)
        _repositoryMock
            .CreateAsync(Arg.Any<CodeAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(returnedRequest)));

        // Act
        var result = await orchestrator.SubmitAnalysisAsync(
            returnedRequest.Repository.Url,
            returnedRequest.Location.FilePath,
            returnedRequest.Type,
            returnedRequest.Title,
            returnedRequest.Description,
            requirements,
            returnedRequest.Repository.Branch,
            null,
            15,
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        // The orchestrator returns the ID from the request it created, not the mocked returned request
        Assert.NotEqual(Guid.Empty, result.Value);
        await _repositoryMock.Received(1)
            .CreateAsync(Arg.Any<CodeAnalysisRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitAnalysisAsync_WithRepositoryCreateFailure_ReturnsFailure()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var errorMessage = "Database error";

        _repositoryMock
            .CreateAsync(Arg.Any<CodeAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure<CodeAnalysisRequest>(errorMessage)));

        // Act
        var result = await orchestrator.SubmitAnalysisAsync(
            "https://github.com/owner/repo",
            "src/Program.cs",
            AnalysisType.BugFix,
            "Test",
            "Description",
            requirementsArray,
            null,
            null,
            15,
            CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Contains(errorMessage, result.Error, StringComparison.Ordinal);
    }

    #endregion

    #region GetAnalysisPromptAsync Tests

    [Fact]
    public async Task GetAnalysisPromptAsync_WithCachedPrompt_ReturnsCachedPrompt()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var cachedPrompt = "Previously generated prompt";
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/org/repo",
            10);
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;
        // Set work tree path via domain method
        request.SetWorkTreePath("/tmp/repo");
        // Set last prompt via domain method with a valid response
        var recordResult = request.RecordPromptAndResponse(cachedPrompt, "Previous response");
        Assert.True(recordResult.IsSuccess);

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        // Act
        var result = await orchestrator.GetAnalysisPromptAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(cachedPrompt, result.Value);
    }

    [Fact]
    public async Task GetAnalysisPromptAsync_WithoutCachedPrompt_BuildsAndCachesPrompt()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var generatedPrompt = "Newly generated prompt";
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/org/repo",
            10);
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;
        // Set work tree path via domain method
        request.SetWorkTreePath("/tmp/repo");
        var context = new AnalysisContext();

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        _codeExtractorMock
            .BuildAnalysisContextAsync(request, "/tmp/repo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(context)));

        _promptBuilderMock
            .BuildPromptAsync(request, context, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(generatedPrompt)));

        _repositoryMock
            .UpdateLastPromptAsync(requestId, generatedPrompt, "", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        // Act
        var result = await orchestrator.GetAnalysisPromptAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(generatedPrompt, result.Value);
        await _repositoryMock.Received(1)
            .UpdateLastPromptAsync(requestId, generatedPrompt, "", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAnalysisPromptAsync_WithRepositoryNotFound_ReturnsFailure()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure<CodeAnalysisRequest>("Not found")));

        // Act
        var result = await orchestrator.GetAnalysisPromptAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task GetAnalysisPromptAsync_WithContextBuildingFailure_ReturnsFailure()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/org/repo",
            10);
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;
        // Set work tree path via domain method
        request.SetWorkTreePath("/tmp/repo");

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        _codeExtractorMock
            .BuildAnalysisContextAsync(request, "/tmp/repo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure<AnalysisContext>("Context build error")));

        // Act
        var result = await orchestrator.GetAnalysisPromptAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Contains("Context build error", result.Error, StringComparison.Ordinal);
    }

    #endregion

    #region ProcessIterationAsync Tests

    [Fact]
    public async Task ProcessIterationAsync_WithValidResponse_ProcessesAndValidates()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var aiResponse = "Here are the changes...";
        var changes = new List<CodeModification> { new() };
        var tempPath = Path.GetTempPath();
        var workTreePath = Path.Combine(tempPath, "daedalus-test-repo-" + requestId.ToString("N"));

        // Create the work tree path for the test
        Directory.CreateDirectory(workTreePath);

        try
        {
            var createResult = CodeAnalysisRequest.Create(
                requestId,
                "Test",
                "Description",
                "https://github.com/org/repo",
                15);
            Assert.True(createResult.IsSuccess);
            var request = createResult.Value;
            // Set work tree path and prompt via domain methods
            request.SetWorkTreePath(workTreePath);
            request.RecordPromptAndResponse("prompt", "");

            _repositoryMock
                .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success(request)));

            _changeApplierMock
                .ExtractChangesAsync(aiResponse, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success((IReadOnlyList<CodeModification>)changes)));

            _changeApplierMock
                .ApplyChangesAsync(workTreePath, changes, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            _repositoryMock
                .UpdateIterationAsync(requestId, 1, "prompt", aiResponse, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            _repositoryMock
                .RecordValidationAsync(requestId, Arg.Any<string>(), false, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            // Act
            var result = await orchestrator.ProcessIterationAsync(requestId, aiResponse, CancellationToken.None);

            // Assert
            Assert.True(result.IsSuccess);
            await _changeApplierMock.Received(1)
                .ExtractChangesAsync(aiResponse, Arg.Any<CancellationToken>());
            await _changeApplierMock.Received(1)
                .ApplyChangesAsync(workTreePath, Arg.Any<IReadOnlyList<CodeModification>>(),
                    Arg.Any<CancellationToken>());
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(workTreePath))
            {
                Directory.Delete(workTreePath, true);
            }
        }
    }

    [Fact]
    public async Task ProcessIterationAsync_WithFailedExtraction_RecordsValidationFailure()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var aiResponse = "Invalid response";
        var tempPath = Path.GetTempPath();
        var workTreePath = Path.Combine(tempPath, "daedalus-test-repo-" + requestId.ToString("N"));

        // Create the work tree path for the test
        Directory.CreateDirectory(workTreePath);

        try
        {
            var createResult = CodeAnalysisRequest.Create(
                requestId,
                "Test",
                "Description",
                "https://github.com/org/repo",
                15);
            Assert.True(createResult.IsSuccess);
            var request = createResult.Value;
            // Set work tree path via domain method
            request.SetWorkTreePath(workTreePath);

            _repositoryMock
                .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success(request)));

            _changeApplierMock
                .ExtractChangesAsync(aiResponse, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Failure<IReadOnlyList<CodeModification>>("Extraction failed")));

            _repositoryMock
                .RecordValidationAsync(requestId, "Extraction failed", true, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            // Act
            var result = await orchestrator.ProcessIterationAsync(requestId, aiResponse, CancellationToken.None);

            // Assert
            Assert.True(result.IsFailure);
            await _repositoryMock.Received(1)
                .RecordValidationAsync(requestId, "Extraction failed", true, Arg.Any<CancellationToken>());
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(workTreePath))
            {
                Directory.Delete(workTreePath, true);
            }
        }
    }

    #endregion

    #region FinalizeAsync Tests

    [Fact]
    public async Task FinalizeAsync_WithCreatePullRequestTrue_CreatesPullRequest()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var prUrl = "https://github.com/owner/repo/pull/1";
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Fix bug",
            "Description",
            "https://github.com/owner/repo",
            10,
            AnalysisType.BugFix,
            null,
            null,
            "main");
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;
        var prResult = new PullRequestResult { WebUrl = prUrl };

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        _repositoryMock
            .UpdateStatusAsync(requestId, AnalysisStatus.Completed, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        _prFactoryMock
            .CreatePullRequestAsync(
                "https://github.com/owner/repo",
                Arg.Any<string>(),
                "main",
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(prResult)));

        _repositoryMock
            .CompleteAsync(requestId, prUrl, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        // Act
        var result = await orchestrator.FinalizeAsync(requestId, true, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(prUrl, result.Value);
        await _prFactoryMock.Received(1)
            .CreatePullRequestAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FinalizeAsync_WithCreatePullRequestFalse_DoesNotCreatePullRequest()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/org/repo",
            10);
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        _repositoryMock
            .UpdateStatusAsync(requestId, AnalysisStatus.Completed, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        // Act
        var result = await orchestrator.FinalizeAsync(requestId, false, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        await _prFactoryMock.DidNotReceive()
            .CreatePullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region GetNextPendingAsync Tests

    [Fact]
    public async Task GetNextPendingAsync_WithPendingRequests_ReturnsFirstRequest()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/org/repo",
            10);
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;
        var requests = new List<CodeAnalysisRequest> { request };

        _repositoryMock
            .GetPendingAsync(1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success((IReadOnlyList<CodeAnalysisRequest>)requests)));

        // Act
        var result = await orchestrator.GetNextPendingAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(requestId, result.Value!.Id);
    }

    [Fact]
    public async Task GetNextPendingAsync_WithNoPendingRequests_ReturnsNull()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var emptyList = new List<CodeAnalysisRequest>();

        _repositoryMock
            .GetPendingAsync(1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success((IReadOnlyList<CodeAnalysisRequest>)emptyList)));

        // Act
        var result = await orchestrator.GetNextPendingAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    #endregion

    #region IsCompleteAsync Tests

    [Fact]
    public async Task IsCompleteAsync_WithCompletedStatus_ReturnsTrue()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/org/repo",
            10);
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;

        // Transition to AnalysisInProgress so Complete() can be called
        var initResult = request.InitializeRepository("/path/to/work", "feature-branch");
        Assert.True(initResult.IsSuccess);
        var startResult = request.StartAnalysis();
        Assert.True(startResult.IsSuccess);

        // Complete it via domain method
        var completeResult = request.Complete();
        Assert.True(completeResult.IsSuccess);

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        // Act
        var result = await orchestrator.IsCompleteAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task IsCompleteAsync_WithFailedStatus_ReturnsTrue()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/org/repo",
            10);
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;
        // Mark as failed via domain method
        request.Fail("Test failure");

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        // Act
        var result = await orchestrator.IsCompleteAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task IsCompleteAsync_WithPendingStatus_ReturnsFalse()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/org/repo",
            10);
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        // Act
        var result = await orchestrator.IsCompleteAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
    }

    #endregion

    #region GetStatusAsync Tests

    [Fact]
    public async Task GetStatusAsync_WithValidRequest_ReturnsRequest()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/org/repo",
            10);
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        // Act
        var result = await orchestrator.GetStatusAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(requestId, result.Value.Id);
    }

    [Fact]
    public async Task GetStatusAsync_WithInvalidRequest_ReturnsFailure()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure<CodeAnalysisRequest>("Not found")));

        // Act
        var result = await orchestrator.GetStatusAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
    }

    #endregion

    #region InitializeRepositoryAsync Tests

    [Fact]
    public async Task InitializeRepositoryAsync_WithValidRepository_InitializesSuccessfully()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var workTreePath = "/tmp/repo-123";
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/owner/repo",
            10,
            AnalysisType.BugFix,
            null,
            null,
            "main");
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;
        var context = new GitOperationContext
        {
            LocalWorkTreePath = workTreePath,
            RepositoryUrl = "https://github.com/owner/repo"
        };

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        _gitManagerMock
            .CloneRepositoryAsync("https://github.com/owner/repo", "main", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(context)));

        _repositoryMock
            .UpdateWorkTreePathAsync(requestId, workTreePath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        _gitManagerMock
            .CreateFeatureBranchAsync(workTreePath, Arg.Any<string>(), "main", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success($"ralph-loop/{requestId:N}")));

        _repositoryMock
            .UpdateStatusAsync(requestId, AnalysisStatus.Ready, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        // Act
        var result = await orchestrator.InitializeRepositoryAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        await _gitManagerMock.Received(1)
            .CloneRepositoryAsync("https://github.com/owner/repo", "main", null, Arg.Any<CancellationToken>());
        await _repositoryMock.Received(1)
            .UpdateWorkTreePathAsync(requestId, workTreePath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitializeRepositoryAsync_WithCloneFailure_MarksRequestAsFailed()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var requestId = Guid.NewGuid();
        var createResult = CodeAnalysisRequest.Create(
            requestId,
            "Test",
            "Description",
            "https://github.com/owner/repo",
            10,
            AnalysisType.BugFix,
            null,
            null,
            "main");
        Assert.True(createResult.IsSuccess);
        var request = createResult.Value;

        _repositoryMock
            .GetByIdAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(request)));

        _gitManagerMock
            .CloneRepositoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure<GitOperationContext>("Clone failed")));

        _repositoryMock
            .FailAsync(requestId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        // Act
        var result = await orchestrator.InitializeRepositoryAsync(requestId, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        await _repositoryMock.Received(1)
            .FailAsync(requestId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion
}
