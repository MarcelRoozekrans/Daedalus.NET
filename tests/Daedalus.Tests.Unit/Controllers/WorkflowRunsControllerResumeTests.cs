using System.Security.Claims;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Controllers;

/// <summary>
///     Ruling R57: <see cref="WorkflowRunsController.Resume"/>'s status for a resume that applies standing instructions
///     and whose handoff fails. In sandbox mode the handoff runs the publish-side protected-path check (S5) inside the
///     request, and a refused patch is a policy refusal, 422, while a genuine failure stays 500. Either way the engine's
///     resume is never called, so the run stays exactly as it was.
/// </summary>
public sealed class WorkflowRunsControllerResumeTests
{
    private const string Signal = "human_approval";

    private readonly IWorkflowStore _store = Substitute.For<IWorkflowStore>();
    private readonly IRunWorkspaceHandoff _handoff = Substitute.For<IRunWorkspaceHandoff>();
    private readonly IWorkflowRunRecordStore _records = Substitute.For<IWorkflowRunRecordStore>();
    private readonly WorkflowRunsController _controller;

    public WorkflowRunsControllerResumeTests()
    {
        _records.ListAsync(Guid.Empty, default, default).ReturnsForAnyArgs(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]));
        var writer = new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, _handoff, NullLogger<StandingInstructionsWriter>.Instance);
        var gateway = new WorkflowRunGateway(_store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(_records), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance, writer);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddApiExplorer();

        var identity = new ClaimsIdentity(
            [new Claim("sub", "u-dev"), new Claim(ClaimTypes.Role, "developer")], authenticationType: "test");
        _controller = new WorkflowRunsController(gateway)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = new ClaimsPrincipal(identity) },
            },
        };
    }

    /// <summary>
    ///     A patch the publish-side check refused is 422 problem details naming the refused path. Red: map
    ///     <see cref="ResumeRefusal.PublishRefused"/> to 500 in the controller, and the status fails; classify the refusal
    ///     as <see cref="ResumeRefusal.WriteFailed"/> in the writer, and it fails too. Red for the run: resume the engine
    ///     before the write; the store then receives the resume.
    /// </summary>
    [Fact]
    public async Task A_patch_the_publish_side_check_refused_is_a_422_naming_the_path()
    {
        var run = AwaitingRun();
        RefuseWith(run, AgentError.Validation("the change touches protected path '.github/workflows/ci.yml'; publish refused"));

        var result = await _controller.Resume(run.Id, new ResumeWorkflowRunRequest(Signal, null, ApplyStandingInstructions: true), CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        objectResult.Value.Should().BeOfType<ProblemDetails>().Subject.Detail.Should().Contain("'.github/workflows/ci.yml'");
        await _store.DidNotReceiveWithAnyArgs().ResumeAsync(Guid.Empty, default!, default);
    }

    /// <summary>
    ///     A handoff that fails for a reason that is not a refusal, here a git failure, is still a server fault: 500,
    ///     whose problem detail is the message alone, never the git stderr with its host paths (fix round 2). Red: map
    ///     every handoff failure to <see cref="ResumeRefusal.PublishRefused"/> in the writer; the status is then 422.
    ///     Red: include the detail in the writer's refusal; the problem detail assertion fails.
    /// </summary>
    [Fact]
    public async Task A_handoff_that_fails_is_still_a_500()
    {
        var run = AwaitingRun();
        RefuseWith(run, AgentError.GitOperationFailed("git apply failed.", "error: corrupt patch"));

        var result = await _controller.Resume(run.Id, new ResumeWorkflowRunRequest(Signal, null, ApplyStandingInstructions: true), CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        objectResult.Value.Should().BeOfType<ProblemDetails>().Subject.Detail.Should().Be("git apply failed.");
        await _store.DidNotReceiveWithAnyArgs().ResumeAsync(Guid.Empty, default!, default);
    }

    /// <summary>
    ///     A drop id the run does not have is a 422, and the run is not resumed. Red: map
    ///     <see cref="ResumeRefusal.UnknownFinding"/> to 409 in the controller, and the status fails.
    /// </summary>
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

        var result = await _controller.Resume(run.Id, new ResumeWorkflowRunRequest(Signal, null, DropFindings: ["nope-1"]), CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        objectResult.Value.Should().BeOfType<ProblemDetails>().Subject.Detail.Should().Contain("nope-1");
        await _store.DidNotReceiveWithAnyArgs().ResumeAsync(Guid.Empty, default!, default);
    }

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
