using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for CodeAnalysisController endpoints.
///     Uses mocked ICodeAnalysisRepository since the controller is the SUT.
/// </summary>
public class CodeAnalysisControllerIntegrationTests
{
    private readonly ICodeAnalysisRepository _repositoryMock = Substitute.For<ICodeAnalysisRepository>();
    private readonly ILogger<CodeAnalysisController> _loggerMock = Substitute.For<ILogger<CodeAnalysisController>>();
    private readonly CodeAnalysisController _controller;

    public CodeAnalysisControllerIntegrationTests()
    {
        _controller = new CodeAnalysisController(_repositoryMock, _loggerMock);
    }

    #region SubmitAnalysis

    [Fact(Timeout = 5000)]
    public async Task SubmitAnalysis_WithValidRequest_Returns201Created()
    {
        // Arrange
        var request = new SubmitAnalysisRequest(
            "https://github.com/org/repo", "src/main.cs",
            AnalysisType.Refactor, "Test Title", "Test Description",
            new List<string> { "req1", "req2" });

        _repositoryMock.CreateAsync(Arg.Any<CodeAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result.Success(callInfo.Arg<CodeAnalysisRequest>()));

        // Act
        var result = await _controller.SubmitAnalysis(request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CreatedAtActionResult>();
        var created = (CreatedAtActionResult)result;
        created.StatusCode.Should().Be(201);
        created.Value.Should().BeOfType<SubmitAnalysisResponse>();
        var response = (SubmitAnalysisResponse)created.Value!;
        response.RequestId.Should().NotBeEmpty();
    }

    [Fact(Timeout = 5000)]
    public async Task SubmitAnalysis_WithRequirements_SerializesToJson()
    {
        // Arrange
        var request = new SubmitAnalysisRequest(
            "https://github.com/org/repo", "src/main.cs",
            AnalysisType.BugFix, "Bug Fix", "Fix the bug",
            new List<string> { "requirement 1", "requirement 2" });

        CodeAnalysisRequest? capturedRequest = null;
        _repositoryMock.CreateAsync(Arg.Any<CodeAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<CodeAnalysisRequest>();
                return Result.Success(capturedRequest);
            });

        // Act
        await _controller.SubmitAnalysis(request, CancellationToken.None);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Requirements.Should().NotBeNullOrEmpty();
        capturedRequest.Requirements.Should().Contain("requirement 1");
        capturedRequest.Requirements.Should().Contain("requirement 2");
    }

    [Fact(Timeout = 5000)]
    public async Task SubmitAnalysis_WithEmptyRequirements_PassesNullRequirements()
    {
        // Arrange
        var request = new SubmitAnalysisRequest(
            "https://github.com/org/repo", "src/main.cs",
            AnalysisType.Refactor, "Title", "Description",
            new List<string>());

        CodeAnalysisRequest? capturedRequest = null;
        _repositoryMock.CreateAsync(Arg.Any<CodeAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<CodeAnalysisRequest>();
                return Result.Success(capturedRequest);
            });

        // Act
        await _controller.SubmitAnalysis(request, CancellationToken.None);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Requirements.Should().BeNullOrEmpty();
    }

    [Fact(Timeout = 5000)]
    public async Task SubmitAnalysis_WhenRepositorySaveFails_Returns500()
    {
        // Arrange
        var request = new SubmitAnalysisRequest(
            "https://github.com/org/repo", "src/main.cs",
            AnalysisType.Refactor, "Title", "Description",
            new List<string>());

        _repositoryMock.CreateAsync(Arg.Any<CodeAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CodeAnalysisRequest>("Database error"));

        // Act
        var result = await _controller.SubmitAnalysis(request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region GetAnalysisStatus

    [Fact(Timeout = 5000)]
    public async Task GetAnalysisStatus_WithExistingId_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));

        // Act
        var result = await _controller.GetAnalysisStatus(entity.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ok.StatusCode.Should().Be(200);
        ok.Value.Should().BeOfType<AnalysisStatusDto>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetAnalysisStatus_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CodeAnalysisRequest>("Not found"));

        // Act
        var result = await _controller.GetAnalysisStatus(id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
        var notFound = (NotFoundObjectResult)result;
        notFound.StatusCode.Should().Be(404);
    }

    [Fact(Timeout = 5000)]
    public async Task GetAnalysisStatus_MapsDtoCorrectly()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));

        // Act
        var result = await _controller.GetAnalysisStatus(entity.Id, CancellationToken.None);

        // Assert
        var ok = (OkObjectResult)result;
        var dto = (AnalysisStatusDto)ok.Value!;
        dto.Request.Id.Should().Be(entity.Id);
        dto.Request.Title.Should().Be("Test Analysis");
        dto.Request.Status.Should().Be(AnalysisStatus.Pending);
        dto.Iterations.Should().BeEmpty();
    }

    #endregion

    #region IterateAnalysis

    [Fact(Timeout = 5000)]
    public async Task IterateAnalysis_WithValidRequest_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        var iterateRequest = new IterateAnalysisRequest(entity.Id, "AI response text");

        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));
        _repositoryMock.UpdateIterationAsync(
                entity.Id, 1, Arg.Any<string>(), "AI response text", Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _controller.IterateAnalysis(entity.Id, iterateRequest, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task IterateAnalysis_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        var iterateRequest = new IterateAnalysisRequest(id, "Response");

        _repositoryMock.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CodeAnalysisRequest>("Not found"));

        // Act
        var result = await _controller.IterateAnalysis(id, iterateRequest, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task IterateAnalysis_WhenUpdateFails_Returns400()
    {
        // Arrange
        var entity = CreateTestEntity();
        var iterateRequest = new IterateAnalysisRequest(entity.Id, "Response");

        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));
        _repositoryMock.UpdateIterationAsync(
                entity.Id, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Max iterations exceeded"));

        // Act
        var result = await _controller.IterateAnalysis(entity.Id, iterateRequest, CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region FinalizeAnalysis

    [Fact(Timeout = 5000)]
    public async Task FinalizeAnalysis_WithValidRequest_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        var finalizeRequest = new FinalizeAnalysisRequest();

        _repositoryMock.CompleteAsync(entity.Id, null, null, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));

        // Act
        var result = await _controller.FinalizeAnalysis(entity.Id, finalizeRequest, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ok.Value.Should().BeOfType<FinalizeAnalysisResponse>();
    }

    [Fact(Timeout = 5000)]
    public async Task FinalizeAnalysis_WhenCompletionFails_Returns400()
    {
        // Arrange
        var id = Guid.NewGuid();
        var finalizeRequest = new FinalizeAnalysisRequest();

        _repositoryMock.CompleteAsync(id, null, null, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Cannot complete"));

        // Act
        var result = await _controller.FinalizeAnalysis(id, finalizeRequest, CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region CancelAnalysis

    [Fact(Timeout = 5000)]
    public async Task CancelAnalysis_WithExistingId_Returns200()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.CancelAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _controller.CancelAnalysis(id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task CancelAnalysis_WhenNotFound_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.CancelAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Request not found"));

        // Act
        var result = await _controller.CancelAnalysis(id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task CancelAnalysis_WhenAlreadyCancelled_Returns400()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.CancelAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Request is already completed"));

        // Act
        var result = await _controller.CancelAnalysis(id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region GetNextPending

    [Fact(Timeout = 5000)]
    public async Task GetNextPending_WithPendingRequest_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetPendingAsync(1, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeAnalysisRequest>>(new List<CodeAnalysisRequest> { entity }));

        // Act
        var result = await _controller.GetNextPending(CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ok.Value.Should().BeOfType<CodeAnalysisRequestDto>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetNextPending_WithNoPendingRequests_Returns204()
    {
        // Arrange
        _repositoryMock.GetPendingAsync(1, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeAnalysisRequest>>(new List<CodeAnalysisRequest>()));

        // Act
        var result = await _controller.GetNextPending(CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetNextPending_WhenRepositoryFails_Returns500()
    {
        // Arrange
        _repositoryMock.GetPendingAsync(1, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<CodeAnalysisRequest>>("Database error"));

        // Act
        var result = await _controller.GetNextPending(CancellationToken.None);

        // Assert
        result.Should().BeOfType<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region GetByStatus

    [Fact(Timeout = 5000)]
    public async Task GetByStatus_WithValidStatus_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetByStatusAsync(AnalysisStatus.Pending, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeAnalysisRequest>>(new List<CodeAnalysisRequest> { entity }));

        // Act
        var result = await _controller.GetByStatus((int)AnalysisStatus.Pending, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetByStatus_WithInvalidStatus_Returns400()
    {
        // Act
        var result = await _controller.GetByStatus(999, CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetByStatus_WithNoResults_ReturnsEmptyList()
    {
        // Arrange
        _repositoryMock.GetByStatusAsync(AnalysisStatus.Completed, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeAnalysisRequest>>(new List<CodeAnalysisRequest>()));

        // Act
        var result = await _controller.GetByStatus((int)AnalysisStatus.Completed, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
    }

    #endregion

    #region GetByRepository

    [Fact(Timeout = 5000)]
    public async Task GetByRepository_WithValidUrl_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetByRepositoryAsync("https://github.com/org/repo", Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeAnalysisRequest>>(new List<CodeAnalysisRequest> { entity }));

        // Act
        var result = await _controller.GetByRepository("https://github.com/org/repo", CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetByRepository_WithEmptyUrl_Returns400()
    {
        // Act
        var result = await _controller.GetByRepository("", CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetByRepository_WithNullUrl_Returns400()
    {
        // Act
        var result = await _controller.GetByRepository(null!, CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetByRepository_WithWhitespaceUrl_Returns400()
    {
        // Act
        var result = await _controller.GetByRepository("   ", CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region Helpers

    private static CodeAnalysisRequest CreateTestEntity()
    {
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test Analysis",
            "Test description",
            "https://github.com/org/repo",
            10,
            AnalysisType.Refactor,
            "src/test.cs");
        return createResult.Value;
    }

    #endregion
}
