using System.Security.Claims;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Daedalus.Tests.Shared;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     Covers <see cref="WorkflowRunsEndpoints.Retry"/>'s mapping of the gateway's outcome to an HTTP status. The
///     Integration fixture cannot reach the handler, so nothing else pins 404 versus 409 versus 204.
/// </summary>
public sealed class WorkflowRunsEndpointsRetryTests : IAsyncLifetime
{
    private readonly IWorkflowStore _store = Substitute.For<IWorkflowStore>();
    private readonly WorkflowRunsRequest _request;

    public WorkflowRunsEndpointsRetryTests()
    {
        var gateway = new WorkflowRunGateway(_store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), Substitute.For<IRunWorkspaceProvider>(), new WorkflowConfig(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);

        // HttpSecurityContextFactory.TryCreate needs an authenticated identity carrying a subject id.
        var identity = new ClaimsIdentity(
            [new Claim("sub", "u-admin"), new Claim(ClaimTypes.Role, "admin")], authenticationType: "test");
        _request = WorkflowRunsRequest.For(gateway, new ClaimsPrincipal(identity));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _request.DisposeAsync();

    [Fact]
    public async Task A_missing_run_is_a_404()
    {
        _store.FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>((WorkflowRun?)null));

        var result = await RetryAsync(Guid.NewGuid());

        // Red: StatusCodes.Status409Conflict in place of Status404NotFound for a missing run in
        //      WorkflowRunsEndpoints.Retry.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: a pair, since WorkflowRunGateway.RetryAsync refuses a missing run before the store as well: an await of
        //      runs.RetryAsync(id, new RunPrincipal("x", []), ct) ahead of the 404 return in WorkflowRunsEndpoints.Retry,
        //      with the not-found return of WorkflowRunGateway.RetryAsync dropped and ExpectedSeq = run?.CurrentSeq ?? 0.
        //      The store then receives the retry while the answer stays 404.
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

        var result = await RetryAsync(run.Id);

        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: StatusCodes.Status422UnprocessableEntity in place of Status409Conflict for a refused retry in
        //      WorkflowRunsEndpoints.Retry.
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        // Red: ApiProblem.Of(context, StatusCodes.Status409Conflict) without result.Error in WorkflowRunsEndpoints.Retry.
        problem.ProblemDetails.Detail.Should().Be(refusal);
    }

    [Fact]
    public async Task A_successful_retry_is_a_204()
    {
        var run = FailedRun();
        _store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        _store.RetryFailedNodeAsync(run.Id, Arg.Any<WorkflowRetryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result>(Result.Success()));

        var result = await RetryAsync(run.Id);

        // Red: result.IsFailure in place of result.IsSuccess in WorkflowRunsEndpoints.Retry's answer.
        result.Result.Should().BeOfType<NoContent>();
    }

    private Task<Results<NoContent, ApiProblemResult>> RetryAsync(Guid id) =>
        WorkflowRunsEndpoints.Retry(id, _request.Context, CancellationToken.None);

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
