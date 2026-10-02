using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.5 task B3 and phase 2.6 task B4: the paths of <see cref="ManufactureRunStarter.StartAsync"/> that the
///     endpoint tests in <c>StartRunEndpointTests</c> cannot reach with a real provider: the workspace provider
///     failing, the undo itself failing, the base-file reader failing or throwing, and a fault thrown before or during
///     the start. The provider, the reader and the process-definition store are substitutes;
///     <see cref="WorkflowRunStarter"/> is the real one. The standing instructions are never read from disk here, and
///     the workspace root is not a directory a file could be read from.
/// </summary>
public sealed class ManufactureRunStarterTests
{
    private static readonly RunPrincipal Starter = new("u-dev", ["developer"]);

    /// <summary>What a sandboxed run's workspace root looks like: not a directory.</summary>
    private const string SandboxRoot = "sandbox://run";

    private readonly IRunBaseFileReader _baseFiles = Substitute.For<IRunBaseFileReader>();
    private readonly IRunManifestResolver _manifests = Substitute.For<IRunManifestResolver>();
    private readonly IRunWorkspaceProvider _workspaces = Substitute.For<IRunWorkspaceProvider>();
    private readonly IProcessDefinitionStore _definitions = Substitute.For<IProcessDefinitionStore>();
    private readonly WorkflowConfig _config = new();

    public ManufactureRunStarterTests()
    {
        _config.Repositories.Add(new RepositoryConfig { Name = "sandbox", Remote = "https://example.invalid/sandbox.git" });
        _workspaces.CreateAsync(Arg.Any<RunWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RunWorkspaceRequest>();
                return new ValueTask<Result<RunWorkspace, AgentError>>(Result<RunWorkspace, AgentError>.Success(new RunWorkspace(
                    request.RunId, request.Repository, request.Remote, request.DefaultBranch, request.Branch, SandboxRoot, null)));
            });
        _workspaces.RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Success()));

        // No active version: the real WorkflowRunStarter reports a failure before it reaches the store, so no run row.
        _definitions.GetActiveVersionAsync("manufacture", Arg.Any<CancellationToken>()).Returns(new ValueTask<int?>((int?)null));

        // Every file is absent at the base commit unless a test says otherwise.
        _baseFiles.ReadBaseFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<string?, AgentError>>(Result<string?, AgentError>.Success(null)));
    }

    /// <summary>
    ///     Red, per assertion: match names with <c>OrdinalIgnoreCase</c>, and a workspace is created for <c>SANDBOX</c>,
    ///     so both fail; report it as <c>Failed</c>, and the first fails.
    /// </summary>
    [Fact]
    public async Task A_repository_name_is_matched_ordinally()
    {
        var result = await CreateStarter().StartAsync(Request("SANDBOX"), CancellationToken.None);

        result.Error.Should().Be(new ManufactureStartFailure(ManufactureStartFailureKind.Invalid, "repository 'SANDBOX' is not allow-listed"));
        await _workspaces.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    /// <summary>
    ///     Red, per assertion: leave the provider's message out of the failure, and the first fails; call the starter
    ///     before the workspace is created, and the store is asked for a version.
    /// </summary>
    [Fact]
    public async Task A_workspace_that_cannot_be_prepared_fails_the_start_before_any_run()
    {
        _workspaces.CreateAsync(Arg.Any<RunWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
                Result<RunWorkspace, AgentError>.Failure(AgentError.Validation("git clone failed."))));

        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.Error.Message.Should().Be("could not prepare the run's workspace: git clone failed.");
        await _definitions.DidNotReceiveWithAnyArgs().GetActiveVersionAsync(default!, default);
    }

    /// <summary>
    ///     The worktree is created under the run id the start then uses, and the undo removes that same one, and a start
    ///     the run starter refuses is the host's failure, not the caller's. Red, per assertion: map the run starter's
    ///     failure to <c>Invalid</c>, and the first fails; pass <c>Guid.NewGuid()</c> to <c>RemoveAsync</c>, and the
    ///     received id differs.
    /// </summary>
    [Fact]
    public async Task A_failed_start_removes_the_worktree_it_created_by_its_run_id()
    {
        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Failed);
        var created = (RunWorkspaceRequest)_workspaces.ReceivedCalls()
            .Single(c => string.Equals(c.GetMethodInfo().Name, nameof(IRunWorkspaceProvider.CreateAsync), StringComparison.Ordinal)).GetArguments()[0]!;
        await _workspaces.Received(1).RemoveAsync(created.RunId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The undo failing must not hide either failure. Red: return the start's failure without looking at the
    ///     removal, and the second assertion fails.
    /// </summary>
    [Fact]
    public async Task A_failed_start_whose_worktree_cannot_be_removed_reports_both_failures()
    {
        _workspaces.RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Failure(AgentError.Validation("worktree is locked."))));

        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.Error.Message.Should().Contain("process 'manufacture' has no active version");
        result.Error.Message.Should().Contain("worktree is locked.");
    }

    /// <summary>
    ///     A start that throws may or may not have written its run row, so the worktree is left for the sweeper, which
    ///     decides on the run's state. Red: remove the worktree in a <c>catch</c> around the start, and it is removed.
    /// </summary>
    [Fact]
    public async Task A_start_that_throws_leaves_the_worktree_for_the_sweeper()
    {
        _definitions.GetActiveVersionAsync("manufacture", Arg.Any<CancellationToken>())
            .Returns<ValueTask<int?>>(_ => throw new InvalidOperationException("connection reset"));

        var act = async () => await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _workspaces.DidNotReceive().RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A fault reading the standing instructions comes before the start, so no run row can exist and the workspace
    ///     is removed at once. The reader throws when its token is cancelled. Red: drop the <c>catch</c> around the
    ///     read, and nothing removes the workspace.
    /// </summary>
    [Fact]
    public async Task A_fault_reading_the_standing_instructions_removes_the_workspace()
    {
        _baseFiles.ReadBaseFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<Result<string?, AgentError>>>(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return default;
            });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await CreateStarter().StartAsync(Request("sandbox"), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _workspaces.Received(1).RemoveAsync(Arg.Any<Guid>(), CancellationToken.None);
    }

    /// <summary>
    ///     Fix round 1. A removal that throws must not replace the read fault. Red, per assertion: remove without a
    ///     <c>try</c> of its own, and the removal's exception escapes instead; drop the record in its <c>catch</c>, and
    ///     the key is absent.
    /// </summary>
    [Fact]
    public async Task A_removal_that_throws_during_a_read_fault_does_not_replace_the_read_fault()
    {
        _workspaces.RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<UnitResult<AgentError>>>(_ => throw new InvalidOperationException("remover crashed."));
        ReaderThrows();

        var act = async () => await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<IOException>();
        thrown.Which.Data[ManufactureRunStarter.WorkspaceRemovalFailureKey].Should().Be("remover crashed.");
    }

    /// <summary>
    ///     Fix round 1. A removal that reports a failure is recorded on the read fault, which still propagates as
    ///     itself. Red: discard the removal's result, and the key is absent.
    /// </summary>
    [Fact]
    public async Task A_removal_that_fails_during_a_read_fault_is_recorded_on_the_read_fault()
    {
        _workspaces.RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Failure(AgentError.Validation("worktree is locked."))));
        ReaderThrows();

        var act = async () => await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<IOException>();
        thrown.Which.Data[ManufactureRunStarter.WorkspaceRemovalFailureKey].Should().Be("worktree is locked.");
    }

    /// <summary>
    ///     Task B4: the standing instructions come from the reader, and the workspace root is <c>sandbox://run</c>, which
    ///     no file read could open. The text reaches the manifest resolver as the pinned document, with the intent beside
    ///     it. Red, per assertion: read <c>File.ReadAllText</c> of the root joined with the path, which throws on this
    ///     root, so both fail; pin the empty text whatever the reader returned, and the first fails; leave the intent
    ///     out of the documents, and the second fails.
    /// </summary>
    [Fact]
    public async Task The_standing_instructions_are_read_from_the_base_commit_through_the_reader()
    {
        _baseFiles.ReadBaseFileAsync(Arg.Any<Guid>(), "AGENT.md", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<string?, AgentError>>(Result<string?, AgentError>.Success("text")));

        var documents = await PinnedDocumentsAsync();

        documents[ManufactureRunStarter.StandingInstructionsDocument].Should().Be("text");
        documents[ManufactureRunStarter.WorkIntentDocument].Should().Be("Tighten a guard.");
    }

    /// <summary>
    ///     Task B4: a file absent at the base commit is a null from the reader, and the run pins the empty text. Red:
    ///     pin the reader's value as it is, and the run starter is handed no text for the document, so the assertion
    ///     fails or the dictionary refuses it.
    /// </summary>
    [Fact]
    public async Task A_missing_file_pins_empty()
    {
        var documents = await PinnedDocumentsAsync();

        documents[ManufactureRunStarter.StandingInstructionsDocument].Should().BeEmpty();
    }

    /// <summary>
    ///     Task B4: the starter asks the reader for the canonical relative path the writer writes, so <c>./AGENT.md</c>
    ///     is asked for as <c>AGENT.md</c>, under the run id the workspace was created with. Red, per assertion: pass
    ///     the configured value as it is, and the first fails; pass <c>Guid.NewGuid()</c>, and the second fails.
    /// </summary>
    [Fact]
    public async Task A_non_canonical_path_is_read_as_its_canonical_form_under_the_runs_id()
    {
        _config.StandingInstructionsPath = "./AGENT.md";

        await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        var created = (RunWorkspaceRequest)_workspaces.ReceivedCalls()
            .Single(c => string.Equals(c.GetMethodInfo().Name, nameof(IRunWorkspaceProvider.CreateAsync), StringComparison.Ordinal)).GetArguments()[0]!;
        await _baseFiles.Received(1).ReadBaseFileAsync(created.RunId, "AGENT.md", Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Task B4: a reader that reports a failure fails the start before any run, and the workspace is removed. A
    ///     refusal of the path is the caller's, so <c>Invalid</c>. Red, per assertion: map the reader's failure to
    ///     <c>Failed</c>, and the first fails; drop the reader's message from the failure, and the second fails; skip
    ///     the removal, and the third fails; go on to the run starter anyway, and the fourth fails.
    /// </summary>
    [Fact]
    public async Task A_reader_failure_fails_the_start_and_removes_the_workspace()
    {
        _baseFiles.ReadBaseFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<string?, AgentError>>(
                Result<string?, AgentError>.Failure(AgentError.Validation("the path is not permitted."))));

        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
        result.Error.Message.Should().Be(
            "the standing instructions 'AGENT.md' cannot be read from the run's base commit: the path is not permitted.");
        await _workspaces.Received(1).RemoveAsync(Arg.Any<Guid>(), CancellationToken.None);
        await _definitions.DidNotReceiveWithAnyArgs().GetActiveVersionAsync(default!, default);
    }

    /// <summary>
    ///     Task B4: an engine outage on create is a provider error, which is <c>Unavailable</c> so the endpoint answers 503
    ///     and not 500. Red: map the provider error to <c>Failed</c> or to <c>Invalid</c>, and the kind fails.
    /// </summary>
    [Fact]
    public async Task A_provider_error_on_create_is_Unavailable()
    {
        _workspaces.CreateAsync(Arg.Any<RunWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
                Result<RunWorkspace, AgentError>.Failure(AgentError.ProviderError("the container engine is not reachable."))));

        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Unavailable);
        result.Error.Message.Should().Contain("the container engine is not reachable.");
    }

    /// <summary>Task B4: a validation error on create is the request's fault. Red: map it to <c>Failed</c>, and the kind fails.</summary>
    [Fact]
    public async Task A_validation_error_on_create_is_Invalid()
    {
        _workspaces.CreateAsync(Arg.Any<RunWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
                Result<RunWorkspace, AgentError>.Failure(AgentError.Validation("the branch name is not valid."))));

        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
    }

    /// <summary>
    ///     Any other provider failure is the host's, so <c>Failed</c>. Red: map every error that is not a validation
    ///     error to <c>Unavailable</c>, and the kind fails.
    /// </summary>
    [Fact]
    public async Task Any_other_error_on_create_is_Failed()
    {
        _workspaces.CreateAsync(Arg.Any<RunWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
                Result<RunWorkspace, AgentError>.Failure(new AgentError(AgentErrorCode.GitOperationFailed, "git clone failed."))));

        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Failed);
    }

    /// <summary>
    ///     Red, per assertion: stop checking the length, and the run goes on to a workspace, so the second kind and the
    ///     workspace assertion fail; report a blank intent as <c>Failed</c>, and the first fails.
    /// </summary>
    [Fact]
    public async Task A_blank_or_over_long_intent_is_Invalid_before_any_workspace()
    {
        var blank = await CreateStarter().StartAsync(new ManufactureStartRequest("  ", "sandbox", Starter), CancellationToken.None);
        var tooLong = await CreateStarter().StartAsync(new ManufactureStartRequest(new string('x', 4001), "sandbox", Starter), CancellationToken.None);

        blank.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
        tooLong.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
        await _workspaces.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    /// <summary>
    ///     Lets the real <see cref="WorkflowRunStarter"/> reach the resolver, which captures the documents it is handed
    ///     and fails, so no store is needed, and returns them.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> PinnedDocumentsAsync()
    {
        _definitions.GetActiveVersionAsync("manufacture", Arg.Any<CancellationToken>()).Returns(new ValueTask<int?>(1));
        _definitions.GetAsync("manufacture", 1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<ProcessDefinition>>(Result<ProcessDefinition>.Success(new ProcessDefinition
            {
                Name = "manufacture",
                Version = 1,
                StartNode = "done",
                Nodes = new Dictionary<string, ProcessNode>(StringComparer.Ordinal) { ["done"] = new() },
            })));
        IReadOnlyDictionary<string, string>? captured = null;
        _manifests.ResolveAsync(Arg.Any<ProcessDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<IReadOnlyDictionary<string, string>>();
                return new ValueTask<Result<RunManifest>>(Result<RunManifest>.Failure("stop here."));
            });

        await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        captured.Should().NotBeNull("the start must reach the manifest resolver for the documents to be pinned");
        return captured!;
    }

    private void ReaderThrows() =>
        _baseFiles.ReadBaseFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<Result<string?, AgentError>>>(_ => throw new IOException("the object store is unreadable."));

    private static ManufactureStartRequest Request(string repository) => new("Tighten a guard.", repository, Starter);

    private ManufactureRunStarter CreateStarter() => new(
        new WorkflowRunStarter(_definitions, _manifests, Substitute.For<IWorkflowStore>()),
        _workspaces,
        _baseFiles,
        _config);
}
