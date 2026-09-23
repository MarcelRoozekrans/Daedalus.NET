using Daedalus.Api.Controllers;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Controllers;

/// <summary>
///     Covers <see cref="CostAnalyticsController.EstimateCost"/>'s translation of
///     <see cref="ICostAnalyticsService.EstimateCostAsync"/>'s <c>Result&lt;CostEstimateDto&gt;</c> into an HTTP
///     response. Nothing else in the suite touches this controller at all, so without this test, deleting the
///     failure-to-404 mapping entirely (returning <c>Ok(result.Value)</c> unconditionally, i.e. a 200 with a null
///     body on failure) would leave every test in the repository green.
/// </summary>
public class CostAnalyticsControllerTests
{
    private readonly ICostAnalyticsService _service = Substitute.For<ICostAnalyticsService>();
    private readonly ILogger<CostAnalyticsController> _logger = Substitute.For<ILogger<CostAnalyticsController>>();
    private readonly CostAnalyticsController _controller;

    public CostAnalyticsControllerTests()
    {
        _controller = new CostAnalyticsController(_service, _logger);
    }

    [Fact]
    public async Task EstimateCost_WhenServiceFails_Returns404WithModelIdInDetail()
    {
        // Falsifiability: deleting the `if (result.IsFailure)` branch in CostAnalyticsController.EstimateCost (or
        // widening it so the failure still falls through to `Ok(result.Value)`) turns this red — the action would
        // return 200 with a null body instead of 404.
        const string unpricedModelId = "claude-unpriced-model";
        _service.EstimateCostAsync(unpricedModelId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<CostEstimateDto>.Failure($"No pricing configured for model '{unpricedModelId}'"));

        var result = await _controller.EstimateCost(unpricedModelId);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        var problem = notFound.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status404NotFound);
        problem.Detail.Should().Contain(unpricedModelId);
    }

    [Fact]
    public async Task EstimateCost_WhenServiceSucceeds_ReturnsTheEstimate()
    {
        // Guards against the mirror-image bug: a mapping so aggressive it also turns the success path into a
        // failure response (e.g. an inverted `if (result.IsSuccess)` check), which the 404 test above cannot catch
        // on its own.
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

        var result = await _controller.EstimateCost(pricedModelId);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(estimate);
    }
}
