using System.Reflection;
using System.Text;
using System.Text.Json;
using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for the CodeAnalysisEndpoints handlers.
///     Uses a mocked ICodeAnalysisRepository since the handlers are the SUT.
/// </summary>
public sealed class CodeAnalysisEndpointsIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ICodeAnalysisRepository _repositoryMock = Substitute.For<ICodeAnalysisRepository>();
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddDaedalusEndpoints().BuildServiceProvider();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _services.DisposeAsync();

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
            .Returns(callInfo => Result<CodeAnalysisRequest>.Success(callInfo.Arg<CodeAnalysisRequest>()));

        // Act
        var result = (await Submit(request)).Result;

        // Assert
        // Red: return Problem(context, StatusCodes.Status410Gone, "x", "x") in place of the TypedResults.Created return in SubmitAnalysis.
        //      The status 201 is the type's own, so the same group covers it.
        var created = result.Should().BeOfType<Created<SubmitAnalysisResponse>>().Subject;
        created.StatusCode.Should().Be(201);
        // Red: new SubmitAnalysisResponse(Guid.Empty) in place of new SubmitAnalysisResponse(analysisRequest.Id) in SubmitAnalysis.
        created.Value!.RequestId.Should().NotBeEmpty();
        // Red: $"/api/codeanalysis/{analysisRequest.Id}" in place of $"/api/CodeAnalysis/{analysisRequest.Id}" in the ApiLocation.For call of SubmitAnalysis.
        created.Location.Should().Be($"http://localhost/api/CodeAnalysis/{created.Value.RequestId}");
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
                return Result<CodeAnalysisRequest>.Success(capturedRequest);
            });

        // Act
        await Submit(request);

        // Assert
        // Red: string.Join(',', escaped.Take(0)) in place of string.Join(',', escaped) in SubmitAnalysis; no requirement reaches Create.
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Requirements.Should().NotBeNullOrEmpty();
        capturedRequest.Requirements.Should().Contain("requirement 1");
        capturedRequest.Requirements.Should().Contain("requirement 2");
    }

    [Fact(Timeout = 5000)]
    public async Task SubmitAnalysis_WithEmptyRequirements_PassesNullRequirements()
    {
        // Arrange
        // Remark: an empty list answers 500 on the real host today, since a null value reaches a jsonb column as an
        // empty string (#428); this test pins only what the handler passes on, which the port keeps.
        var request = new SubmitAnalysisRequest(
            "https://github.com/org/repo", "src/main.cs",
            AnalysisType.Refactor, "Title", "Description",
            new List<string>());

        CodeAnalysisRequest? capturedRequest = null;
        _repositoryMock.CreateAsync(Arg.Any<CodeAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<CodeAnalysisRequest>();
                return Result<CodeAnalysisRequest>.Success(capturedRequest);
            });

        // Act
        await Submit(request);

        // Assert
        // Red: Count: > 0 or 0 in place of Count: > 0 in the requirements pattern of SubmitAnalysis; "[]" is passed on.
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
            .Returns(Result<CodeAnalysisRequest>.Failure("Database error"));

        // Act
        var result = (await Submit(request)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the saveResult.IsFailure condition in SubmitAnalysis; the result is a Created.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status503ServiceUnavailable in place of Status500InternalServerError in the save failure Problem call of SubmitAnalysis.
        problem.StatusCode.Should().Be(500);
        // Red: "Internal error" in place of "Internal Error" in SubmitAnalysis.
        problem.ProblemDetails.Title.Should().Be("Internal Error");
        // Red: "Internal Error", "x" in place of "Internal Error", saveResult.Error in SubmitAnalysis.
        problem.ProblemDetails.Detail.Should().Be("Database error");
    }

    [Fact(Timeout = 5000)]
    public async Task SubmitAnalysis_WhenTheRequestIsRefusedByTheDomain_Returns400()
    {
        // Arrange
        // The domain refuses an empty title; the DTO validator would answer first on the real host.
        var request = new SubmitAnalysisRequest(
            "https://github.com/org/repo", "src/main.cs",
            AnalysisType.Refactor, "", "Description",
            new List<string>());

        // Act
        var result = (await Submit(request)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the createResult.IsFailure condition in SubmitAnalysis; the call goes on to the repository.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status422UnprocessableEntity in place of Status400BadRequest in the "Validation Error" Problem call of SubmitAnalysis.
        problem.StatusCode.Should().Be(400);
        // Red: "Validation error" in place of "Validation Error" in SubmitAnalysis.
        problem.ProblemDetails.Title.Should().Be("Validation Error");
        // Red: "Validation Error", "x" in place of "Validation Error", createResult.Error in SubmitAnalysis.
        problem.ProblemDetails.Detail.Should().Be("Title cannot be empty");
    }

    #endregion

    #region GetAnalysisStatus

    [Fact(Timeout = 5000)]
    public async Task GetAnalysisStatus_WithExistingId_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result<CodeAnalysisRequest>.Success(entity));

        // Act
        var result = (await CodeAnalysisEndpoints.GetAnalysisStatus(
            entity.Id, CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: a guard on requestId != Guid.Empty that returns Problem(context, StatusCodes.Status404NotFound, "x", "x"), ahead of var entity = result.Value in GetAnalysisStatus.
        var ok = result.Should().BeOfType<Ok<AnalysisStatusDto>>().Subject;
        ok.StatusCode.Should().Be(200);
    }

    [Fact(Timeout = 5000)]
    public async Task GetAnalysisStatus_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<CodeAnalysisRequest>.Failure("Not found"));

        // Act
        var result = (await CodeAnalysisEndpoints.GetAnalysisStatus(
            id, CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the result.IsFailure condition in GetAnalysisStatus; the failed result's Value throws.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status400BadRequest in place of Status404NotFound in GetAnalysisStatus.
        problem.StatusCode.Should().Be(404);
        // Red: "Not found" in place of "Not Found" in GetAnalysisStatus.
        problem.ProblemDetails.Title.Should().Be("Not Found");
        // Red: "Not Found", "x" in place of "Not Found", result.Error in GetAnalysisStatus.
        problem.ProblemDetails.Detail.Should().Be("Not found");
    }

    [Fact(Timeout = 5000)]
    public async Task GetAnalysisStatus_MapsDtoCorrectly()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result<CodeAnalysisRequest>.Success(entity));

        // Act
        var result = (await CodeAnalysisEndpoints.GetAnalysisStatus(
            entity.Id, CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: a guard on requestId != Guid.Empty that returns Problem(context, StatusCodes.Status404NotFound, "x", "x"), ahead of var entity = result.Value in GetAnalysisStatus.
        var dto = result.Should().BeOfType<Ok<AnalysisStatusDto>>().Subject.Value!;
        // Red: Guid.NewGuid() in place of entity.Id in MapToDto.
        dto.Request.Id.Should().Be(entity.Id);
        // Red: entity.Description in place of entity.Title in MapToDto.
        dto.Request.Title.Should().Be("Test Analysis");
        // Red: AnalysisStatus.Completed in place of entity.Status in MapToDto.
        dto.Request.Status.Should().Be(AnalysisStatus.Pending);
        // Red: .Append(new AnalysisIterationDto(1, "x", "x", false, DateTime.UtcNow, null)) after Select(MapIterationToDto) in GetAnalysisStatus.
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
            .Returns(Result<CodeAnalysisRequest>.Success(entity));
        _repositoryMock.UpdateIterationAsync(
                entity.Id, 1, Arg.Any<string>(), "AI response text", Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = (await CodeAnalysisEndpoints.IterateAnalysis(
            entity.Id, await BindBody(iterateRequest), CreateContext(), _repositoryMock, NullLoggerFactory.Instance,
            CancellationToken.None)).Result;

        // Assert
        // Red: return Problem(context, StatusCodes.Status400BadRequest, "x", "x") in place of the ApiJson.Of return in IterateAnalysis.
        var ok = result.Should().BeOfType<ApiJsonResult<IterateAnalysisResponse>>().Subject;
        // Red: Status201Created in place of Status200OK in the ApiJson.Of return of IterateAnalysis.
        ok.StatusCode.Should().Be(200);
        // Red: nextIteration + 1 in place of nextIteration in the IterateAnalysisResponse of IterateAnalysis.
        ok.Value.Should().Be(new IterateAnalysisResponse(entity.Id, 1));
    }

    [Fact(Timeout = 5000)]
    public async Task IterateAnalysis_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        var iterateRequest = new IterateAnalysisRequest(id, "Response");

        _repositoryMock.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<CodeAnalysisRequest>.Failure("Not found"));

        // Act
        var result = (await CodeAnalysisEndpoints.IterateAnalysis(
            id, await BindBody(iterateRequest), CreateContext(), _repositoryMock, NullLoggerFactory.Instance,
            CancellationToken.None)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the getResult.IsFailure condition in IterateAnalysis.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status400BadRequest in place of Status404NotFound in the lookup failure Problem call of IterateAnalysis.
        problem.StatusCode.Should().Be(404);
        // Red: "Not found" in place of "Not Found" in IterateAnalysis.
        problem.ProblemDetails.Title.Should().Be("Not Found");
    }

    [Fact(Timeout = 5000)]
    public async Task IterateAnalysis_WhenUpdateFails_Returns400()
    {
        // Arrange
        var entity = CreateTestEntity();
        var iterateRequest = new IterateAnalysisRequest(entity.Id, "Response");

        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result<CodeAnalysisRequest>.Success(entity));
        _repositoryMock.UpdateIterationAsync(
                entity.Id, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Max iterations exceeded"));

        // Act
        var result = (await CodeAnalysisEndpoints.IterateAnalysis(
            entity.Id, await BindBody(iterateRequest), CreateContext(), _repositoryMock, NullLoggerFactory.Instance,
            CancellationToken.None)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the iterateResult.IsFailure condition in IterateAnalysis.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status409Conflict in place of Status400BadRequest in the "Iteration Error" Problem call of IterateAnalysis.
        problem.StatusCode.Should().Be(400);
        // Red: "Iteration error" in place of "Iteration Error" in IterateAnalysis.
        problem.ProblemDetails.Title.Should().Be("Iteration Error");
        // Red: "Iteration Error", "x" in place of "Iteration Error", iterateResult.Error in IterateAnalysis.
        problem.ProblemDetails.Detail.Should().Be("Max iterations exceeded");
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
            .Returns(Result<CodeAnalysisRequest>.Success(entity));

        // Act
        var result = (await CodeAnalysisEndpoints.FinalizeAnalysis(
            entity.Id, await BindBody(finalizeRequest), CreateContext(), _repositoryMock, NullLoggerFactory.Instance,
            CancellationToken.None)).Result;

        // Assert
        // Red: a guard on prUrl is null that returns Problem(context, StatusCodes.Status400BadRequest, "x", "x"), ahead of the TypedResults.Ok return in FinalizeAnalysis.
        var ok = result.Should().BeOfType<Ok<FinalizeAnalysisResponse>>().Subject;
        // Red: new FinalizeAnalysisResponse(prUrl + "x") in place of new FinalizeAnalysisResponse(prUrl) in FinalizeAnalysis.
        ok.Value!.PullRequestUrl.Should().Be(entity.PullRequestUrl);
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
        var result = (await CodeAnalysisEndpoints.FinalizeAnalysis(
            id, await BindBody(finalizeRequest), CreateContext(), _repositoryMock, NullLoggerFactory.Instance,
            CancellationToken.None)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the completeResult.IsFailure condition in FinalizeAnalysis; the result is an Ok.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status404NotFound in place of Status400BadRequest in the "Finalization Error" Problem call of FinalizeAnalysis.
        problem.StatusCode.Should().Be(400);
        // Red: "Finalization error" in place of "Finalization Error" in FinalizeAnalysis.
        problem.ProblemDetails.Title.Should().Be("Finalization Error");
        // Red: "Finalization Error", "x" in place of "Finalization Error", completeResult.Error in FinalizeAnalysis.
        problem.ProblemDetails.Detail.Should().Be("Cannot complete");
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
        var result = (await CodeAnalysisEndpoints.CancelAnalysis(
            id, CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: return Problem(context, StatusCodes.Status400BadRequest, "x", "x") in place of the ApiJson.Of return in CancelAnalysis.
        var ok = result.Should().BeOfType<ApiJsonResult<CancelAnalysisResponse>>().Subject;
        // Red: Status201Created in place of Status200OK in the ApiJson.Of return of CancelAnalysis.
        ok.StatusCode.Should().Be(200);
        // Red: "Completed" in place of "Cancelled" in the CancelAnalysisResponse of CancelAnalysis.
        ok.Value.Should().Be(new CancelAnalysisResponse(id, "Cancelled"));
    }

    [Fact(Timeout = 5000)]
    public async Task CancelAnalysis_WhenNotFound_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.CancelAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Request not found"));

        // Act
        var result = (await CodeAnalysisEndpoints.CancelAnalysis(
            id, CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the cancelResult.IsFailure condition in CancelAnalysis.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status400BadRequest in place of Status404NotFound in the not-found Problem call of CancelAnalysis.
        problem.StatusCode.Should().Be(404);
        // Red: "Not found" in place of "Not Found" in CancelAnalysis.
        problem.ProblemDetails.Title.Should().Be("Not Found");
        // Red: "Not Found", "x" in place of "Not Found", cancelResult.Error in CancelAnalysis.
        problem.ProblemDetails.Detail.Should().Be("Request not found");
    }

    [Fact(Timeout = 5000)]
    public async Task CancelAnalysis_WhenAlreadyCancelled_Returns400()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.CancelAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Request is already completed"));

        // Act
        var result = (await CodeAnalysisEndpoints.CancelAnalysis(
            id, CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the cancelResult.IsFailure condition in CancelAnalysis.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Contains("completed") in place of Contains("not found") in CancelAnalysis; this error is answered as a 404.
        problem.StatusCode.Should().Be(400);
        // Red: "Cancellation error" in place of "Cancellation Error" in CancelAnalysis.
        problem.ProblemDetails.Title.Should().Be("Cancellation Error");
        // Red: "Cancellation Error", "x" in place of "Cancellation Error", cancelResult.Error in CancelAnalysis.
        problem.ProblemDetails.Detail.Should().Be("Request is already completed");
    }

    #endregion

    #region GetNextPending

    [Fact(Timeout = 5000)]
    public async Task GetNextPending_WithPendingRequest_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetPendingAsync(1, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<CodeAnalysisRequest>>.Success(new List<CodeAnalysisRequest> { entity }));

        // Act
        var result = (await CodeAnalysisEndpoints.GetNextPending(
            CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: return TypedResults.NoContent() in place of the TypedResults.Ok return in GetNextPending.
        var ok = result.Should().BeOfType<Ok<CodeAnalysisRequestDto>>().Subject;
        // Red: MapToDto(pending[0]) with { Id = Guid.NewGuid() } in place of MapToDto(pending[0]) in GetNextPending.
        ok.Value!.Id.Should().Be(entity.Id);
    }

    [Fact(Timeout = 5000)]
    public async Task GetNextPending_WithNoPendingRequests_Returns204()
    {
        // Arrange
        _repositoryMock.GetPendingAsync(1, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<CodeAnalysisRequest>>.Success(new List<CodeAnalysisRequest>()));

        // Act
        var result = (await CodeAnalysisEndpoints.GetNextPending(
            CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: pending.Count == 1 in place of pending.Count == 0 in GetNextPending; the empty list throws on pending[0].
        result.Should().BeOfType<NoContent>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetNextPending_WhenRepositoryFails_Returns500()
    {
        // Arrange
        _repositoryMock.GetPendingAsync(1, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<CodeAnalysisRequest>>.Failure("Database error"));

        // Act
        var result = (await CodeAnalysisEndpoints.GetNextPending(
            CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the result.IsFailure condition in GetNextPending.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status503ServiceUnavailable in place of Status500InternalServerError in GetNextPending.
        problem.StatusCode.Should().Be(500);
        // Red: "Internal error" in place of "Internal Error" in GetNextPending.
        problem.ProblemDetails.Title.Should().Be("Internal Error");
        // Red: "Internal Error", "x" in place of "Internal Error", result.Error in GetNextPending.
        problem.ProblemDetails.Detail.Should().Be("Database error");
    }

    #endregion

    #region GetByStatus

    [Fact(Timeout = 5000)]
    public async Task GetByStatus_WithValidStatus_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetByStatusAsync(AnalysisStatus.Pending, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<CodeAnalysisRequest>>.Success(new List<CodeAnalysisRequest> { entity }));

        // Act
        var result = (await CodeAnalysisEndpoints.GetByStatus(
            (int)AnalysisStatus.Pending, CreateContext(), _repositoryMock, NullLoggerFactory.Instance,
            CancellationToken.None)).Result;

        // Assert
        // Red: a guard on status >= 0 that returns Problem(context, StatusCodes.Status500InternalServerError, "x", "x"), ahead of the TypedResults.Ok return in GetByStatus.
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<CodeAnalysisRequestDto>>>().Subject;
        // Red: result.Value.Select(MapToDto).Take(0).ToList() in place of result.Value.Select(MapToDto).ToList() in GetByStatus.
        ok.Value.Should().ContainSingle().Which.Id.Should().Be(entity.Id);
    }

    [Fact(Timeout = 5000)]
    public async Task GetByStatus_WithInvalidStatus_Returns400()
    {
        // Act
        var result = (await CodeAnalysisEndpoints.GetByStatus(
            999, CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: && status == 0 appended to the Enum.IsDefined condition of GetByStatus; the undefined status reaches the repository.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status404NotFound in place of Status400BadRequest in the "Invalid Status" Problem call of GetByStatus.
        problem.StatusCode.Should().Be(400);
        // Red: "Invalid status" in place of "Invalid Status" in GetByStatus.
        problem.ProblemDetails.Title.Should().Be("Invalid Status");
        // Red: $"Invalid status: {status}" in place of $"Invalid status value: {status}" in GetByStatus.
        problem.ProblemDetails.Detail.Should().Be("Invalid status value: 999");
    }

    [Fact(Timeout = 5000)]
    public async Task GetByStatus_WithNoResults_ReturnsEmptyList()
    {
        // Arrange
        _repositoryMock.GetByStatusAsync(AnalysisStatus.Completed, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<CodeAnalysisRequest>>.Success(new List<CodeAnalysisRequest>()));

        // Act
        var result = (await CodeAnalysisEndpoints.GetByStatus(
            (int)AnalysisStatus.Completed, CreateContext(), _repositoryMock, NullLoggerFactory.Instance,
            CancellationToken.None)).Result;

        // Assert
        // Red: a guard on status >= 0 that returns Problem(context, StatusCodes.Status500InternalServerError, "x", "x"), ahead of the TypedResults.Ok return in GetByStatus.
        result.Should().BeOfType<Ok<IReadOnlyList<CodeAnalysisRequestDto>>>().Which.Value.Should().BeEmpty();
    }

    [Fact(Timeout = 5000)]
    public async Task GetByStatus_WhenRepositoryFails_Returns500()
    {
        // Arrange
        // New case: the controller answered this 500 and no test pinned it.
        _repositoryMock.GetByStatusAsync(AnalysisStatus.Pending, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<CodeAnalysisRequest>>.Failure("Database error"));

        // Act
        var result = (await CodeAnalysisEndpoints.GetByStatus(
            (int)AnalysisStatus.Pending, CreateContext(), _repositoryMock, NullLoggerFactory.Instance,
            CancellationToken.None)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the result.IsFailure condition in GetByStatus.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status503ServiceUnavailable in place of Status500InternalServerError, "Internal Error" in GetByStatus.
        problem.StatusCode.Should().Be(500);
        // Red: "Internal error" in place of "Internal Error" in GetByStatus.
        problem.ProblemDetails.Title.Should().Be("Internal Error");
        // Red: "Internal Error", "x" in place of "Internal Error", result.Error in GetByStatus.
        problem.ProblemDetails.Detail.Should().Be("Database error");
    }

    #endregion

    #region GetByRepository

    [Fact(Timeout = 5000)]
    public async Task GetByRepository_WithValidUrl_Returns200()
    {
        // Arrange
        var entity = CreateTestEntity();
        _repositoryMock.GetByRepositoryAsync("https://github.com/org/repo", Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<CodeAnalysisRequest>>.Success(new List<CodeAnalysisRequest> { entity }));

        // Act
        var context = CreateContext();
        var result = (await CodeAnalysisEndpoints.GetByRepository(
            await BindRepositoryUrl(context, "?repositoryUrl=https://github.com/org/repo"), context, _repositoryMock,
            NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: a guard on repositoryUrl.Value.Length > 0 that returns Problem(context, StatusCodes.Status500InternalServerError, "x", "x"), ahead of the TypedResults.Ok return in GetByRepository.
        var ok = result.Should().BeOfType<Ok<IReadOnlyList<CodeAnalysisRequestDto>>>().Subject;
        // Red: result.Value.Select(MapToDto).Take(0).ToList() in place of result.Value.Select(MapToDto).ToList() in GetByRepository.
        ok.Value.Should().ContainSingle().Which.Id.Should().Be(entity.Id);
    }

    [Fact(Timeout = 5000)]
    public async Task GetByRepository_WhenRepositoryFails_Returns500()
    {
        // Arrange
        // New case: the controller answered this 500 and no test pinned it.
        _repositoryMock.GetByRepositoryAsync("https://github.com/org/repo", Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<CodeAnalysisRequest>>.Failure("Database error"));

        // Act
        var context = CreateContext();
        var result = (await CodeAnalysisEndpoints.GetByRepository(
            await BindRepositoryUrl(context, "?repositoryUrl=https://github.com/org/repo"), context, _repositoryMock,
            NullLoggerFactory.Instance, CancellationToken.None)).Result;

        // Assert
        // Red: && ct.IsCancellationRequested appended to the result.IsFailure condition in GetByRepository.
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: Status503ServiceUnavailable in place of Status500InternalServerError in GetByRepository.
        problem.StatusCode.Should().Be(500);
        // Red: "Internal error" in place of "Internal Error" in GetByRepository.
        problem.ProblemDetails.Title.Should().Be("Internal Error");
        // Red: "Internal Error", "x" in place of "Internal Error", result.Error in GetByRepository.
        problem.ProblemDetails.Detail.Should().Be("Database error");
    }

    /// <summary>
    ///     The controller refused an empty, a missing and a whitespace URL itself, with a 400 that the implicit
    ///     required check of the binder had already answered (R52). The check moved to <c>[Required]</c> on the
    ///     parameter, so each case now fails while binding, with the binder's problem.
    /// </summary>
    [Theory(Timeout = 5000)]
    [InlineData("?repositoryUrl=")]
    [InlineData("")]
    [InlineData("?repositoryUrl=%20%20%20")]
    public async Task GetByRepository_WithAnEmptyNullOrWhitespaceUrl_IsRefusedWhileBindingWith400(string queryString)
    {
        // Act
        var bound = await BindRepositoryUrl(CreateContext(), queryString);

        // Assert
        // Red: ApiQuery<string> repositoryUrl in place of [Required] ApiQuery<string> repositoryUrl in GetByRepository; the parameter binds with no problem.
        var problem = bound.Problem.Should().NotBeNull().And.Subject.As<ValidationProblemDetails>();
        // Red: $"The {name} is required." in place of $"The {name} field is required." in ApiBinding.Required.
        problem.Errors.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new KeyValuePair<string, string[]>("repositoryUrl", ["The repositoryUrl field is required."]));
        // Red: StatusCodes.Status422UnprocessableEntity in place of Status400BadRequest in ApiProblem.BindingProblem.
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
    }

    #endregion

    #region Logging

    /// <summary>
    ///     Every handler logs its information event under the endpoints' category before it calls the repository
    ///     (R42): event ids 100 to 107, as the controller's <c>LoggerMessage</c> methods had them.
    /// </summary>
    [Theory(Timeout = 5000)]
    [InlineData("SubmitAnalysis", 100)]
    [InlineData("GetAnalysisStatus", 101)]
    [InlineData("IterateAnalysis", 102)]
    [InlineData("FinalizeAnalysis", 103)]
    [InlineData("GetNextPending", 104)]
    [InlineData("GetByStatus", 105)]
    [InlineData("GetByRepository", 106)]
    [InlineData("CancelAnalysis", 107)]
    public async Task Each_handler_logs_its_event_under_the_endpoints_category(string handler, int eventId)
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        _repositoryMock.CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var id = Guid.NewGuid();
        var context = CreateContext();
        var ct = CancellationToken.None;
        var okRequest = new SubmitAnalysisRequest("https://github.com/org/repo", "a.cs", AnalysisType.Refactor, "t", "d", ["r"]);

        // Act
        switch (handler)
        {
            case "SubmitAnalysis":
                await CodeAnalysisEndpoints.SubmitAnalysis(await BindBody(okRequest), context, _repositoryMock, factory, ct);
                break;
            case "GetAnalysisStatus":
                await CodeAnalysisEndpoints.GetAnalysisStatus(id, context, _repositoryMock, factory, ct);
                break;
            case "IterateAnalysis":
                await CodeAnalysisEndpoints.IterateAnalysis(
                    id, await BindBody(new IterateAnalysisRequest(id, "r")), context, _repositoryMock, factory, ct);
                break;
            case "FinalizeAnalysis":
                await CodeAnalysisEndpoints.FinalizeAnalysis(
                    id, await BindBody(new FinalizeAnalysisRequest()), context, _repositoryMock, factory, ct);
                break;
            case "GetNextPending":
                await CodeAnalysisEndpoints.GetNextPending(context, _repositoryMock, factory, ct);
                break;
            case "GetByStatus":
                await CodeAnalysisEndpoints.GetByStatus(1, context, _repositoryMock, factory, ct);
                break;
            case "GetByRepository":
                await CodeAnalysisEndpoints.GetByRepository(
                    await BindRepositoryUrl(context, "?repositoryUrl=u"), context, _repositoryMock, factory, ct);
                break;
            default:
                await CodeAnalysisEndpoints.CancelAnalysis(id, context, _repositoryMock, factory, ct);
                break;
        }

        // Assert
        // Red: loggerFactory.CreateLogger("Other") in place of loggerFactory.CreateLogger(typeof(CodeAnalysisEndpoints)) in GetAnalysisStatus.
        factory.Received(1).CreateLogger("Daedalus.Api.Endpoints.CodeAnalysisEndpoints");
        // Red: EventId = 108 in place of EventId = 101 on RetrievingAnalysisStatus in Log.
        logger.ReceivedCalls()
            .Where(call => string.Equals(call.GetMethodInfo().Name, nameof(ILogger.Log), StringComparison.Ordinal))
            .Select(call => call.GetArguments()[1] is EventId logged ? logged.Id : throw new InvalidOperationException("No EventId."))
            .Should().Equal(eventId);
    }

    #endregion

    #region Helpers

    private async Task<Results<Created<SubmitAnalysisResponse>, ApiProblemResult>> Submit(SubmitAnalysisRequest request) =>
        await CodeAnalysisEndpoints.SubmitAnalysis(
            await BindBody(request), CreateContext(), _repositoryMock, NullLoggerFactory.Instance, CancellationToken.None);

    private DefaultHttpContext CreateContext(string? body = null)
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask, new EndpointMetadataCollection(ProblemContentType.Json), "test"));
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
        }

        return context;
    }

    /// <summary>Binds <paramref name="dto"/> as the request delegate binds the body, through its JSON text.</summary>
    private async Task<ApiBody<T>> BindBody<T>(T dto)
        where T : class
    {
        var context = CreateContext(JsonSerializer.Serialize(dto, WebJson));
        var parameter = typeof(CodeAnalysisEndpoints).GetMethods().SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(ApiBody<T>));
        return await BindVia<ApiBody<T>>(context, parameter);
    }

    private static async Task<ApiQuery<string>> BindRepositoryUrl(HttpContext context, string queryString)
    {
        context.Request.QueryString = new QueryString(queryString);
        var parameter = typeof(CodeAnalysisEndpoints).GetMethod(nameof(CodeAnalysisEndpoints.GetByRepository))!.GetParameters()[0];
        return (await BindVia<ApiQuery<string>>(context, parameter))!;
    }

    private static async Task<TSelf> BindVia<TSelf>(HttpContext context, ParameterInfo parameter)
        where TSelf : class, IBindableFromHttpContext<TSelf> => (await TSelf.BindAsync(context, parameter))!;

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
