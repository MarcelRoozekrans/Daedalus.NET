using System.Reflection;
using System.Runtime.CompilerServices;
using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     Covers <see cref="CostAnalyticsEndpoints.EstimateCost"/>'s translation of
///     <see cref="ICostAnalyticsService.EstimateCostAsync"/>'s <c>Result&lt;CostEstimateDto&gt;</c> into an HTTP
///     response, and the four reads' call-through and catch-all. Without the estimate test, deleting the
///     failure-to-404 mapping entirely (returning <c>Ok(result.Value)</c> unconditionally, i.e. a 200 with a null
///     body on failure) would leave every other test in the repository green.
/// </summary>
public sealed class CostAnalyticsEndpointsTests
{
    private const string Category = "Daedalus.Api.Endpoints.CostAnalyticsEndpoints";

    private static readonly Guid ProjectId = Guid.NewGuid();

    private readonly ICostAnalyticsService _service = Substitute.For<ICostAnalyticsService>();

    [Fact]
    public async Task EstimateCost_WhenServiceFails_Returns404WithModelIdInDetail()
    {
        const string unpricedModelId = "claude-unpriced-model";
        _service.EstimateCostAsync(unpricedModelId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<CostEstimateDto>.Failure($"No pricing configured for model '{unpricedModelId}'"));
        await using var services = Services();
        var context = Create(services, ProblemContentType.Json);
        var (modelId, maxIterations, promptTokens) = await BindEstimate(context, $"?modelId={unpricedModelId}");

        var result = await CostAnalyticsEndpoints.EstimateCost(
            modelId, maxIterations, promptTokens, context, _service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: delete the IsFailure branch in EstimateCost, so the failure falls through to the Ok answer
        //      and the handler answers a 200 with a null body in place of the problem.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: answer StatusCodes.Status410Gone in place of 404 in EstimateCost's failure branch.
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: ProblemDetails.Detail set to a constant in place of result.Error.
        problem.ProblemDetails.Detail.Should().Contain(unpricedModelId);
        // Red: the Title "Not Found" dropped from the ProblemDetails, so the body carries the table's title only.
        problem.ProblemDetails.Title.Should().Be("Not Found");
        // Red: ApiProblem.Of in place of ApiProblem.AsGiven in EstimateCost; the problem gains type and traceId.
        problem.ProblemDetails.Type.Should().BeNull();
        problem.ProblemDetails.Extensions.Should().BeEmpty();
    }

    [Fact]
    public async Task EstimateCost_WhenServiceSucceeds_ReturnsTheEstimate()
    {
        const string pricedModelId = "claude-sonnet-4-20250514";
        var estimate = new CostEstimateDto(
            ModelId: pricedModelId,
            ModelDisplayName: "Claude Sonnet 4",
            MaxIterations: 10,
            EstimatedPromptTokens: 4000,
            EstimatedResponseTokens: 4000,
            EstimatedMinCost: 0.189m,
            EstimatedMaxCost: 0.63m);
        _service.EstimateCostAsync(pricedModelId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<CostEstimateDto>.Success(estimate));
        await using var services = Services();
        var context = Create(services);
        var (modelId, maxIterations, promptTokens) = await BindEstimate(context, $"?modelId={pricedModelId}");

        var result = await CostAnalyticsEndpoints.EstimateCost(
            modelId, maxIterations, promptTokens, context, _service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: invert the IsFailure check in EstimateCost; the success path answers the 404 problem.
        result.Result.Should().BeOfType<Ok<CostEstimateDto>>().Which.Value.Should().BeSameAs(estimate);
    }

    [Fact]
    public async Task EstimateCost_WithoutQueryValues_AsksForTenIterationsAndFourThousandPromptTokens()
    {
        _service.EstimateCostAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<CostEstimateDto>.Failure("unpriced"));
        await using var services = Services();
        var context = Create(services);
        var (modelId, maxIterations, promptTokens) = await BindEstimate(context, "?modelId=m");

        await CostAnalyticsEndpoints.EstimateCost(
            modelId, maxIterations, promptTokens, context, _service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: use maxIterations.Value and estimatedPromptTokens.Value in EstimateCost; the service is asked for 0 and 0.
        await _service.Received(1).EstimateCostAsync("m", 10, 4000, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EstimateCost_PassesTheSentValues()
    {
        _service.EstimateCostAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<CostEstimateDto>.Failure("unpriced"));
        await using var services = Services();
        var context = Create(services);
        var (modelId, maxIterations, promptTokens) = await BindEstimate(context, "?modelId=m&maxIterations=3&estimatedPromptTokens=900");

        await CostAnalyticsEndpoints.EstimateCost(
            modelId, maxIterations, promptTokens, context, _service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: pass the defaults 10 and 4000 in place of the sent values in EstimateCost; the call for 3 and 900 is not made.
        await _service.Received(1).EstimateCostAsync("m", 3, 900, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?modelId=")]
    [InlineData("?modelId=%20%20")]
    public async Task EstimateCost_BindsAMissingModelIdAsTheRequiredProblem(string queryString)
    {
        await using var services = Services();
        var context = Create(services, ProblemContentType.Json);

        var (modelId, _, _) = await BindEstimate(context, queryString);

        // Red: remove [Required] from the modelId parameter of EstimateCost; the parameter binds with no problem.
        var problem = modelId.Problem.Should().NotBeNull().And.Subject.As<ValidationProblemDetails>();
        // Red: key the error with "ModelId" in place of the parameter's name in ApiQuery.BindAsync.
        problem.Errors.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new KeyValuePair<string, string[]>("modelId", ["The modelId field is required."]));
        // Red: ApiProblem.BindingProblem sets Status 422 in place of 400.
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        // Red: ApiProblem.BindingProblem skips ApplyDefaults; the type is missing.
        problem.Type.Should().Be(BadRequestType);
    }

    [Fact]
    public async Task GetSummary_ReturnsTheSummary()
    {
        var summary = (CostSummaryDto)RuntimeHelpers.GetUninitializedObject(typeof(CostSummaryDto));
        _service.GetSummaryAsync(Arg.Any<CancellationToken>()).Returns(summary);

        var result = await CostAnalyticsEndpoints.GetSummary(_service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: return an empty summary in place of the service's in GetSummary.
        result.Result.Should().BeOfType<Ok<CostSummaryDto>>().Which.Value.Should().BeSameAs(summary);
    }

    [Fact]
    public async Task GetByProject_ReturnsTheProjectCosts()
    {
        IReadOnlyList<ProjectCostDto> costs = new List<ProjectCostDto>();
        _service.GetCostsByProjectAsync(Arg.Any<CancellationToken>()).Returns(costs);

        var result = await CostAnalyticsEndpoints.GetByProject(_service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: return an empty list in place of the service's in GetByProject.
        result.Result.Should().BeOfType<Ok<IReadOnlyList<ProjectCostDto>>>().Which.Value.Should().BeSameAs(costs);
    }

    [Fact]
    public async Task GetByProjectId_AsksForTheProjectAndReturnsItsTaskCosts()
    {
        IReadOnlyList<TaskCostDto> costs = new List<TaskCostDto>();
        _service.GetCostsByProjectIdAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(costs);

        var result = await CostAnalyticsEndpoints.GetByProjectId(
            ProjectId, _service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: pass Guid.Empty in place of id in GetByProjectId; the stub for the project is not met.
        result.Result.Should().BeOfType<Ok<IReadOnlyList<TaskCostDto>>>().Which.Value.Should().BeSameAs(costs);
    }

    [Fact]
    public async Task GetPricing_ReturnsThePricing()
    {
        IReadOnlyList<ModelPricingDto> pricing = new List<ModelPricingDto>();
        _service.GetPricingAsync(Arg.Any<CancellationToken>()).Returns(pricing);

        var result = await CostAnalyticsEndpoints.GetPricing(_service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: return an empty list in place of the service's in GetPricing.
        result.Result.Should().BeOfType<Ok<IReadOnlyList<ModelPricingDto>>>().Which.Value.Should().BeSameAs(pricing);
    }

    [Fact]
    public async Task GetSummary_AnswersALogged500ErrorBodyWhenTheServiceThrows()
    {
        _service.GetSummaryAsync(Arg.Any<CancellationToken>()).Returns<Task<CostSummaryDto>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();

        var result = await CostAnalyticsEndpoints.GetSummary(_service, factory, CancellationToken.None);

        AssertLoggedInternalServerError(result.Result, factory, logger);
    }

    [Fact]
    public async Task GetByProject_AnswersALogged500ErrorBodyWhenTheServiceThrows()
    {
        _service.GetCostsByProjectAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ProjectCostDto>>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();

        var result = await CostAnalyticsEndpoints.GetByProject(_service, factory, CancellationToken.None);

        AssertLoggedInternalServerError(result.Result, factory, logger);
    }

    [Fact]
    public async Task GetByProjectId_AnswersALogged500ErrorBodyWhenTheServiceThrows()
    {
        _service.GetCostsByProjectIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<TaskCostDto>>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();

        var result = await CostAnalyticsEndpoints.GetByProjectId(ProjectId, _service, factory, CancellationToken.None);

        AssertLoggedInternalServerError(result.Result, factory, logger);
    }

    [Fact]
    public async Task GetPricing_AnswersALogged500ErrorBodyWhenTheServiceThrows()
    {
        _service.GetPricingAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ModelPricingDto>>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();

        var result = await CostAnalyticsEndpoints.GetPricing(_service, factory, CancellationToken.None);

        AssertLoggedInternalServerError(result.Result, factory, logger);
    }

    [Fact]
    public async Task EstimateCost_AnswersALogged500ErrorBodyWhenTheServiceThrows()
    {
        _service.EstimateCostAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<CostEstimateDto>>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();
        await using var services = Services();
        var context = Create(services);
        var (modelId, maxIterations, promptTokens) = await BindEstimate(context, "?modelId=m");

        var result = await CostAnalyticsEndpoints.EstimateCost(
            modelId, maxIterations, promptTokens, context, _service, factory, CancellationToken.None);

        AssertLoggedInternalServerError(result.Result, factory, logger);
    }

    // Red: rethrow in the catch of the handler under test, or answer a 400; the result is not a 500 error body.
    // Red: answer 503 in InternalServerError, the helper every catch uses; all five 500 tests fail.
    // Red: drop the call to Log.ErrorRetrievingCosts in the catch of the handler under test.
    // Red: change EventId = 200 to 201 on ErrorRetrievingCosts.
    // Red: pass the category "Other" to CreateLogger in the catch of the handler under test.
    private static void AssertLoggedInternalServerError(object? result, ILoggerFactory factory, ILogger logger)
    {
        var json = result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: change the text in InternalServerError to "Internal error"; the body is not the error body.
        json.Value.Should().Be(new ErrorBody("Internal server error"));
        LoggedEventId(logger).Id.Should().Be(200);
        factory.Received(1).CreateLogger(Category);
    }

    /// <summary>Binds the three query parameters of <c>EstimateCost</c> as the request delegate does.</summary>
    private static async Task<(ApiQuery<string> ModelId, ApiQuery<int> MaxIterations, ApiQuery<int> PromptTokens)> BindEstimate(
        HttpContext context, string queryString)
    {
        context.Request.QueryString = new QueryString(queryString);
        var parameters = typeof(CostAnalyticsEndpoints).GetMethod(nameof(CostAnalyticsEndpoints.EstimateCost))!.GetParameters();
        return (await BindAsync<ApiQuery<string>>(context, parameters[0]),
            await BindAsync<ApiQuery<int>>(context, parameters[1]),
            await BindAsync<ApiQuery<int>>(context, parameters[2]));
    }

    private static (ILoggerFactory Factory, ILogger Logger) CreateLogging()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        return (factory, logger);
    }

    private static EventId LoggedEventId(ILogger logger) =>
        logger.ReceivedCalls().Single(call => string.Equals(call.GetMethodInfo().Name, nameof(ILogger.Log), StringComparison.Ordinal))
            .GetArguments()[1] is EventId id ? id : throw new InvalidOperationException("No EventId argument.");
}
