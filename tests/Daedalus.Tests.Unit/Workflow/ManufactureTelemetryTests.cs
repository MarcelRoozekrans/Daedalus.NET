using System.Diagnostics;
using Daedalus.Agents.Workflow;
using Thalos.Testing;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.9, spec Part B step 2: a manufacture start is the <c>daedalus.manufacture.start</c> span every dispatch of
///     its run links back to, tagged with the repository and, on success, the run id. A compensating cancel is a
///     <c>daedalus.manufacture.cancel</c> span. Neither carries the work intent or the cancel reason.
/// </summary>
public sealed class ManufactureTelemetryTests
{
    private static readonly RunPrincipal Starter = new("u-dev", ["developer"]);

    /// <summary>
    ///     Red: drop the <c>[TraceTagFromResult]</c>; no span carries the run id, so the single-span assertion fails.
    ///     Red: drop the repository <c>[TraceTag]</c>; the repository assertion fails.
    ///     Red: set <c>ErrorWhen = "IsSuccess"</c>; the status assertion fails.
    ///     Red: add <c>[TraceTag("daedalus.work_intent", "WorkIntent")]</c>; the content assertion fails.
    /// </summary>
    [Fact]
    public async Task A_started_run_is_traced_with_its_repository_and_run_id_and_not_its_intent()
    {
        var runId = Guid.NewGuid();
        var inner = Substitute.For<IManufactureRunStarter>();
        inner.StartAsync(Arg.Any<ManufactureStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<Guid, ManufactureStartFailure>>(Result<Guid, ManufactureStartFailure>.Success(runId)));
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName);

        await new ManufactureRunStarterInstrumented(inner)
            .StartAsync(new ManufactureStartRequest("zqx probe intent", "sandbox", Starter), CancellationToken.None);

        var span = capture.SpansNamed(ManufactureTelemetry.StartSpanName).Should()
            .ContainSingle(s => runId.Equals(s.GetTagItem(ManufactureTelemetry.RunIdTag))).Subject;
        span.GetTagItem(ManufactureTelemetry.RepositoryTag).Should().Be("sandbox");
        span.Status.Should().Be(ActivityStatusCode.Unset);
        span.TagObjects.Select(t => t.Value?.ToString()).Should()
            .NotContain(v => v != null && v.Contains("zqx", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Red: drop <c>ErrorWhen = "IsFailure"</c>; the status is Unset.
    ///     Red: set <c>ErrorDescription = "Error.Message"</c>; the description is the message, not the kind.
    ///     Red: drop the <c>When = "IsSuccess"</c> guard; reading <c>Value</c> on the failure throws.
    /// </summary>
    [Fact]
    public async Task A_refused_start_is_an_error_span_described_by_its_kind_only()
    {
        var repository = $"probe-repo-{Guid.NewGuid():N}";
        var inner = Substitute.For<IManufactureRunStarter>();
        inner.StartAsync(Arg.Any<ManufactureStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<Guid, ManufactureStartFailure>>(Result<Guid, ManufactureStartFailure>.Failure(
                new ManufactureStartFailure(ManufactureStartFailureKind.Unstartable, "zqx skill text"))));
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName);

        await new ManufactureRunStarterInstrumented(inner)
            .StartAsync(new ManufactureStartRequest("intent", repository, Starter), CancellationToken.None);

        var span = capture.SpansNamed(ManufactureTelemetry.StartSpanName)
            .Single(s => repository.Equals(s.GetTagItem(ManufactureTelemetry.RepositoryTag)));
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().Be(nameof(ManufactureStartFailureKind.Unstartable));
        span.GetTagItem(ManufactureTelemetry.RunIdTag).Should().BeNull();
    }

    /// <summary>
    ///     Red: drop the <c>[TraceTag]</c> on <c>runId</c>; no span carries the run id.
    ///     Red: drop the <c>[TraceTagFromResult]</c>; the cancelled assertion fails.
    ///     Red: add <c>[TraceTag("daedalus.reason")]</c> to the reason; the content assertion fails.
    /// </summary>
    [Fact]
    public async Task A_compensating_cancel_is_traced_with_its_run_and_outcome_and_not_its_reason()
    {
        var runId = Guid.NewGuid();
        var inner = Substitute.For<IWorkflowRunCanceller>();
        inner.CancelAsync(runId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName);

        await new WorkflowRunCancellerInstrumented(inner).CancelAsync(runId, "zqx reason", CancellationToken.None);

        var span = capture.SpansNamed(ManufactureTelemetry.CancelSpanName).Should()
            .ContainSingle(s => runId.Equals(s.GetTagItem(ManufactureTelemetry.RunIdTag))).Subject;
        span.GetTagItem(ManufactureTelemetry.CancelledTag).Should().Be(true);
        span.TagObjects.Select(t => t.Value?.ToString()).Should()
            .NotContain(v => v != null && v.Contains("zqx", StringComparison.Ordinal));
    }
}
