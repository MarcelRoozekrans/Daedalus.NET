using System.Security.Claims;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Shared;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     Ruling R57: <see cref="WorkflowRunsEndpoints.Resume"/>'s status for a resume that applies standing instructions
///     and whose handoff fails. In sandbox mode the handoff runs the publish-side protected-path check (S5) inside the
///     request, and a refused patch is a policy refusal, 422, while a genuine failure stays 500. Either way the engine's
///     resume is never called, so the run stays exactly as it was.
/// </summary>
public sealed class WorkflowRunsEndpointsResumeTests : IAsyncLifetime
{
    private const string Signal = "human_approval";

    private readonly IWorkflowStore _store = Substitute.For<IWorkflowStore>();
    private readonly IRunWorkspaceHandoff _handoff = Substitute.For<IRunWorkspaceHandoff>();
    private readonly IWorkflowRunRecordStore _records = Substitute.For<IWorkflowRunRecordStore>();
    private readonly WorkflowRunsRequest _request;

    public WorkflowRunsEndpointsResumeTests()
    {
        _records.ListAsync(Guid.Empty, default, default).ReturnsForAnyArgs(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]));
        var writer = new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, _handoff, NullLogger<StandingInstructionsWriter>.Instance);
        var gateway = new WorkflowRunGateway(_store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(_records), Substitute.For<IRunWorkspaceProvider>(), new WorkflowConfig(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance, writer);

        var identity = new ClaimsIdentity(
            [new Claim("sub", "u-dev"), new Claim(ClaimTypes.Role, "developer")], authenticationType: "test");
        _request = WorkflowRunsRequest.For(gateway, new ClaimsPrincipal(identity));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _request.DisposeAsync();

    /// <summary>
    ///     A patch the publish-side check refused is 422 problem details naming the refused path. Red for the
    ///     writer: classify the refusal as <see cref="ResumeRefusal.WriteFailed"/> in the writer, and the status fails
    ///     too.
    /// </summary>
    [Fact]
    public async Task A_patch_the_publish_side_check_refused_is_a_422_naming_the_path()
    {
        var run = AwaitingRun();
        RefuseWith(run, AgentError.Validation("the change touches protected path '.github/workflows/ci.yml'; publish refused"));

        var result = await ResumeAsync(run.Id, new ResumeWorkflowRunRequest(Signal, null, ApplyStandingInstructions: true));

        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: StatusCodes.Status500InternalServerError in place of Status422UnprocessableEntity for
        //      ResumeRefusal.PublishRefused in WorkflowRunsEndpoints.Resume.
        problem.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        // Red: ApiProblem.Of(context, statusCode) without result.Error.Detail in WorkflowRunsEndpoints.Resume.
        problem.ProblemDetails.Detail.Should().Contain("'.github/workflows/ci.yml'");
        // Red: in WorkflowRunGateway.ApplyAndResumeAsync, the engine's resume moved ahead of the standing-instructions
        //      write; the store then receives the resume.
        await _store.DidNotReceiveWithAnyArgs().ResumeAsync(Guid.Empty, default!, default);
    }

    /// <summary>
    ///     A handoff that fails for a reason that is not a refusal, here a git failure, is still a server fault: 500,
    ///     whose problem detail is the message alone, never the git stderr with its host paths (fix round 2). Red: map
    ///     every handoff failure to <see cref="ResumeRefusal.PublishRefused"/> in the writer; the status is then 422.
    /// </summary>
    [Fact]
    public async Task A_handoff_that_fails_is_still_a_500()
    {
        var run = AwaitingRun();
        RefuseWith(run, AgentError.GitOperationFailed("git apply failed.", "error: corrupt patch"));

        var result = await ResumeAsync(run.Id, new ResumeWorkflowRunRequest(Signal, null, ApplyStandingInstructions: true));

        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: StatusCodes.Status409Conflict in place of Status500InternalServerError for ResumeRefusal.WriteFailed
        //      in WorkflowRunsEndpoints.Resume.
        problem.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: Describe(error) in place of error.Message in StandingInstructionsWriter's WriteFailed refusal; the
        //      detail then carries the git stderr.
        problem.ProblemDetails.Detail.Should().Be("git apply failed.");
        // Red: the same move in WorkflowRunGateway.ApplyAndResumeAsync as in the 422 case above.
        await _store.DidNotReceiveWithAnyArgs().ResumeAsync(Guid.Empty, default!, default);
    }

    /// <summary>A drop id the run does not have is a 422, and the run is not resumed.</summary>
    [Fact]
    public async Task An_unknown_finding_to_drop_is_a_422()
    {
        var run = AwaitingRun();
        _store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        var evidence = WorkflowRunRecord.Create(
            run.Id, 1, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null,
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked"}] }""",
            DateTime.UtcNow).Value;
        _records.ListAsync(run.Id, WorkflowRunRecord.ReviewEvidenceKind, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([evidence]));

        var result = await ResumeAsync(run.Id, new ResumeWorkflowRunRequest(Signal, null, DropFindings: ["nope-1"]));

        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: StatusCodes.Status409Conflict in place of Status422UnprocessableEntity for ResumeRefusal.UnknownFinding
        //      in WorkflowRunsEndpoints.Resume.
        problem.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        // Red: ApiProblem.Of(context, statusCode) without result.Error.Detail in WorkflowRunsEndpoints.Resume.
        problem.ProblemDetails.Detail.Should().Contain("nope-1");
        // Red: ApplyAndResumeAsync awaited inside the unknown-finding branch of WorkflowRunGateway.ResumeAsync, ahead
        //      of its return; the store then receives the resume before the 422.
        await _store.DidNotReceiveWithAnyArgs().ResumeAsync(Guid.Empty, default!, default);
    }

    private async Task<Results<NoContent, ApiProblemResult>> ResumeAsync(Guid id, ResumeWorkflowRunRequest body) =>
        await WorkflowRunsEndpoints.Resume(
            id, await _request.BodyAsync(nameof(WorkflowRunsEndpoints.Resume), body), _request.Context, CancellationToken.None);

    private void RefuseWith(WorkflowRun run, AgentError error)
    {
        _store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        _handoff.CheckoutForPublishAsync(run.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<RunWorkspace, AgentError>>(Result<RunWorkspace, AgentError>.Failure(error)));
    }

    private static WorkflowRun AwaitingRun() => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 7,
        CurrentNode = "gate",
        CurrentSeq = 1,
        Status = WorkflowStatus.Awaiting,
        AwaitingSignal = Signal,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["gate"] = 1 },
        Variables = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ReviewHandoff.ProposedStandingInstructionsKey] = "New instructions.",
        },
    };
}
