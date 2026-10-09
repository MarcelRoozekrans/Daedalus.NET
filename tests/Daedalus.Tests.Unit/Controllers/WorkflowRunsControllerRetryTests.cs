using System.Security.Claims;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Controllers;

/// <summary>
///     Covers <see cref="WorkflowRunsController.Retry"/>'s mapping of the gateway's outcome to an HTTP status. The
///     Integration fixture cannot resolve this controller, so nothing else pins 404 versus 409 versus 204.
/// </summary>
public sealed class WorkflowRunsControllerRetryTests
{
    private readonly IWorkflowStore _store = Substitute.For<IWorkflowStore>();
    private readonly WorkflowRunsController _controller;

    public WorkflowRunsControllerRetryTests()
    {
        var gateway = new WorkflowRunGateway(_store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddApiExplorer();

        // HttpSecurityContextFactory.TryCreate needs an authenticated identity carrying a subject id.
        var identity = new ClaimsIdentity(
            [new Claim("sub", "u-admin"), new Claim(ClaimTypes.Role, "admin")], authenticationType: "test");
        _controller = new WorkflowRunsController(gateway)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = new ClaimsPrincipal(identity) },
            },
        };
    }

    [Fact]
    public async Task A_missing_run_is_a_404()
    {
        _store.FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>((WorkflowRun?)null));

        var result = await _controller.Retry(Guid.NewGuid(), CancellationToken.None);

        // Red if: the `is null` check returns Problem(..409) instead of NotFound, or the 404 and 409 branches are
        // swapped. The store is never reached for a missing run.
        result.Should().BeOfType<NotFoundResult>();
        await _store.DidNotReceive().RetryFailedNodeAsync(Arg.Any<Guid>(), Arg.Any<WorkflowRetryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_store_refusal_is_a_409_carrying_the_stores_message()
    {
        const string refusal = "run is not Failed at a host-action node";
        var run = FailedRun();
        _store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        _store.RetryFailedNodeAsync(run.Id, Arg.Any<WorkflowRetryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result>(Result.Failure(refusal)));

        var result = await _controller.Retry(run.Id, CancellationToken.None);

        // Red if: the status code is not 409 (e.g. 404 or 422), or the detail drops result.Error.
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        objectResult.Value.Should().BeOfType<ProblemDetails>().Subject.Detail.Should().Be(refusal);
    }

    [Fact]
    public async Task A_successful_retry_is_a_204()
    {
        var run = FailedRun();
        _store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        _store.RetryFailedNodeAsync(run.Id, Arg.Any<WorkflowRetryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result>(Result.Success()));

        var result = await _controller.Retry(run.Id, CancellationToken.None);

        // Red if: NoContent becomes Ok (200) or any body-bearing result, or the success test is inverted.
        result.Should().BeOfType<NoContentResult>();
    }

    private static WorkflowRun FailedRun() => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 5,
        CurrentNode = "publish",
        CurrentSeq = 7,
        Status = WorkflowStatus.Failed,
        LastError = "git push refused",
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["publish"] = 1 },
    };
}
