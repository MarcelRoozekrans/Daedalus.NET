using System.Diagnostics;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Testing;
using Thalos.Workflow;
using Thalos.Workspaces;

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
    ///     Red: set <c>ErrorWhen = "IsSuccess"</c>; the status assertion fails.
    ///     Red: add <c>[TraceTag("daedalus.work_intent", "WorkIntent")]</c>; the content assertion fails.
    /// </summary>
    [Fact]
    public async Task A_started_run_is_traced_with_its_run_id_and_not_its_intent()
    {
        var runId = Guid.NewGuid();
        var inner = Substitute.For<IManufactureRunStarter>();
        inner.StartAsync(Arg.Any<ManufactureStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<Guid, ManufactureStartFailure>>(Result<Guid, ManufactureStartFailure>.Success(runId)));
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName);

        await new ManufactureRunStarterInstrumented(inner)
            .StartAsync(new ManufactureStartRequest("zqx probe intent", "zqx-repo", Starter), CancellationToken.None);

        var span = capture.SpansNamed(ManufactureTelemetry.StartSpanName).Should()
            .ContainSingle(s => runId.Equals(s.GetTagItem(ManufactureTelemetry.RunIdTag))).Subject;
        span.Status.Should().Be(ActivityStatusCode.Unset);
        span.TagObjects.Select(t => t.Value?.ToString()).Should()
            .NotContain(v => v != null && v.Contains("zqx", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Red: drop <c>ErrorWhen = "IsFailure"</c>; the status is Unset.
    ///     Red: set <c>ErrorDescription = "Error.Message"</c>; the description is the message, not the kind.
    ///     Red: drop the <c>When = "IsSuccess"</c> guard; the proxy throws reading <c>Value</c> on the failure, so the
    ///     act fails before any assertion.
    ///     Red: add <c>[TraceTagFromResult(ManufactureTelemetry.RunIdTag, "Error.Kind", When = "IsFailure")]</c>; the
    ///     run id assertion fails.
    /// </summary>
    [Fact]
    public async Task A_refused_start_is_an_error_span_described_by_its_kind_only()
    {
        var parentName = $"probe-parent-{Guid.NewGuid():N}";
        using var parents = new ActivitySource(parentName);
        var inner = Substitute.For<IManufactureRunStarter>();
        inner.StartAsync(Arg.Any<ManufactureStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<Guid, ManufactureStartFailure>>(Result<Guid, ManufactureStartFailure>.Failure(
                new ManufactureStartFailure(ManufactureStartFailureKind.Unstartable, "zqx skill text"))));
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName, parentName);
        using var parent = parents.StartActivity("probe");

        await new ManufactureRunStarterInstrumented(inner)
            .StartAsync(new ManufactureStartRequest("intent", "sandbox", Starter), CancellationToken.None);

        var span = capture.SpansNamed(ManufactureTelemetry.StartSpanName).Single(s => s.ParentSpanId == parent!.SpanId);
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().Be(nameof(ManufactureStartFailureKind.Unstartable));
        span.GetTagItem(ManufactureTelemetry.RunIdTag).Should().BeNull();
    }

    /// <summary>
    ///     A repository the allow-list refuses is caller input, an HTTP body or an agent argument, so no tag carries it.
    ///     Red: restore <c>[TraceTag(ManufactureTelemetry.RepositoryTag, "Repository")]</c> on the request parameter; a tag
    ///     holds the <c>zqx</c> repository.
    ///     Red: set <c>ErrorDescription = "Error.Message"</c>; the description holds the repository, and the description
    ///     assertion fails.
    /// </summary>
    [Fact]
    public async Task A_start_for_a_repository_that_is_not_allow_listed_tags_none_of_it()
    {
        var refused = $"zqx-{Guid.NewGuid():N}";
        var parentName = $"probe-parent-{Guid.NewGuid():N}";
        using var parents = new ActivitySource(parentName);
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName, parentName);
        using var parent = parents.StartActivity("probe");

        await new ManufactureRunStarterInstrumented(RealStarter("sandbox"))
            .StartAsync(new ManufactureStartRequest("intent", refused, Starter), CancellationToken.None);

        var span = capture.SpansNamed(ManufactureTelemetry.StartSpanName).Single(s => s.ParentSpanId == parent!.SpanId);
        span.StatusDescription.Should().Be(nameof(ManufactureStartFailureKind.Invalid));
        span.TagObjects.Select(t => t.Value?.ToString()).Should()
            .NotContain(v => v != null && v.Contains("zqx", StringComparison.Ordinal));
        span.GetTagItem(ManufactureTelemetry.RepositoryTag).Should().BeNull();
    }

    /// <summary>
    ///     An allow-listed repository is tagged with its configured name, set by the starter once the allow-list matches.
    ///     Red: drop <c>TagStartSpan</c> from <c>ManufactureRunStarter</c>; the span has no repository tag.
    /// </summary>
    [Fact]
    public async Task A_start_for_an_allow_listed_repository_is_tagged_with_its_configured_name()
    {
        var parentName = $"probe-parent-{Guid.NewGuid():N}";
        using var parents = new ActivitySource(parentName);
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName, parentName);
        using var parent = parents.StartActivity("probe");

        await new ManufactureRunStarterInstrumented(RealStarter("sandbox"))
            .StartAsync(new ManufactureStartRequest("intent", "sandbox", Starter), CancellationToken.None);

        capture.SpansNamed(ManufactureTelemetry.StartSpanName).Single(s => s.ParentSpanId == parent!.SpanId)
            .GetTagItem(ManufactureTelemetry.RepositoryTag).Should().Be("sandbox");
    }

    /// <summary>
    ///     The starter tags only the manufacture start span. Called directly, with an ambient span of another source, it
    ///     leaves that span alone.
    ///     Red: drop the <c>OperationName</c> and <c>Source</c> guard in <c>TagStartSpan</c>; the ambient span gains the
    ///     repository tag.
    /// </summary>
    [Fact]
    public async Task The_starter_leaves_an_ambient_span_that_is_not_the_start_span_untagged()
    {
        var parentName = $"probe-parent-{Guid.NewGuid():N}";
        using var parents = new ActivitySource(parentName);
        using var capture = new TelemetryCapture(parentName);
        using var parent = parents.StartActivity("probe");

        await RealStarter("sandbox").StartAsync(new ManufactureStartRequest("intent", "sandbox", Starter), CancellationToken.None);

        parent.Should().NotBeNull();
        parent!.GetTagItem(ManufactureTelemetry.RepositoryTag).Should().BeNull();
    }

    /// <summary>The real starter over substitutes. No active process version, so an allow-listed start fails cleanly.</summary>
    private static ManufactureRunStarter RealStarter(string allowListed)
    {
        var config = new WorkflowConfig();
        config.Repositories.Add(new RepositoryConfig { Name = allowListed, Remote = "https://example.invalid/sandbox.git" });
        var workspaces = Substitute.For<IRunWorkspaceProvider>();
        workspaces.CreateAsync(Arg.Any<RunWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RunWorkspaceRequest>();
                return new ValueTask<Result<RunWorkspace, AgentError>>(Result<RunWorkspace, AgentError>.Success(new RunWorkspace(
                    request.RunId, request.Repository, request.Remote, request.DefaultBranch, request.Branch, "sandbox://run", null)));
            });
        workspaces.RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Success()));
        var baseFiles = Substitute.For<IRunBaseFileReader>();
        baseFiles.ReadBaseFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<string?, AgentError>>(Result<string?, AgentError>.Success(null)));
        var definitions = Substitute.For<IProcessDefinitionStore>();
        definitions.GetActiveVersionAsync("manufacture", Arg.Any<CancellationToken>()).Returns(new ValueTask<int?>((int?)null));
        return new ManufactureRunStarter(
            new WorkflowRunStarter(definitions, Substitute.For<IRunManifestResolver>(), Substitute.For<IWorkflowStore>()),
            workspaces,
            baseFiles,
            config,
            Substitute.For<IWorkflowStore>());
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
