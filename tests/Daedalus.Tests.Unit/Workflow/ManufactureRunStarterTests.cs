using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.5 task B3: the paths of <see cref="ManufactureRunStarter.StartAsync"/> that the endpoint tests in
///     <c>StartRunEndpointTests</c> cannot reach with a real provider: the workspace provider failing, the undo itself
///     failing, and a fault thrown before or during the start. The provider and the process-definition store are
///     substitutes; <see cref="WorkflowRunStarter"/> is the real one.
/// </summary>
public sealed class ManufactureRunStarterTests : IDisposable
{
    private static readonly RunPrincipal Starter = new("u-dev", ["developer"]);

    private readonly TempDirectory _worktree = new();
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
                    request.RunId, request.Repository, request.Remote, request.DefaultBranch, request.Branch, _worktree.Root, null)));
            });
        _workspaces.RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Success()));

        // No active version: the real WorkflowRunStarter reports a failure before it reaches the store, so no run row.
        _definitions.GetActiveVersionAsync("manufacture", Arg.Any<CancellationToken>()).Returns(new ValueTask<int?>((int?)null));
    }

    public void Dispose() => _worktree.Dispose();

    /// <summary>Red: match names with <c>OrdinalIgnoreCase</c>, and a worktree is created for <c>SANDBOX</c>.</summary>
    [Fact]
    public async Task A_repository_name_is_matched_ordinally()
    {
        var result = await CreateStarter().StartAsync(Request("SANDBOX"), CancellationToken.None);

        result.Error.Should().Be("repository 'SANDBOX' is not allow-listed");
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

        result.Error.Should().Be("could not prepare the run's workspace: git clone failed.");
        await _definitions.DidNotReceiveWithAnyArgs().GetActiveVersionAsync(default!, default);
    }

    /// <summary>
    ///     The worktree is created under the run id the start then uses, and the undo removes that same one. Red: pass
    ///     <c>Guid.NewGuid()</c> to <c>RemoveAsync</c>, and the received id differs.
    /// </summary>
    [Fact]
    public async Task A_failed_start_removes_the_worktree_it_created_by_its_run_id()
    {
        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
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

        result.Error.Should().Contain("process 'manufacture' has no active version");
        result.Error.Should().Contain("worktree is locked.");
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
    ///     A fault reading the standing instructions comes before the start, so no run row can exist and the worktree
    ///     is removed at once. The read is made to throw by a cancelled token, which the substituted provider ignores.
    ///     Red: drop the <c>catch</c> around the read, and nothing removes the worktree.
    /// </summary>
    [Fact]
    public async Task A_fault_reading_the_standing_instructions_removes_the_worktree()
    {
        await File.WriteAllTextAsync(_worktree.Path("AGENT.md"), "Run dotnet test.");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await CreateStarter().StartAsync(Request("sandbox"), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _workspaces.Received(1).RemoveAsync(Arg.Any<Guid>(), CancellationToken.None);
    }

    /// <summary>
    ///     Fix round 1. A removal that throws must not replace the read fault. The read is made to fail with an
    ///     <see cref="IOException"/> by holding <c>AGENT.md</c> open with <see cref="FileShare.None"/>. Red, per
    ///     assertion: remove without a <c>try</c> of its own, and the removal's exception escapes instead; drop the
    ///     record in its <c>catch</c>, and the key is absent.
    /// </summary>
    [Fact]
    public async Task A_removal_that_throws_during_a_read_fault_does_not_replace_the_read_fault()
    {
        _workspaces.RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<UnitResult<AgentError>>>(_ => throw new InvalidOperationException("remover crashed."));
        await using var locked = LockedAgentMd();

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
        await using var locked = LockedAgentMd();

        var act = async () => await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<IOException>();
        thrown.Which.Data[ManufactureRunStarter.WorkspaceRemovalFailureKey].Should().Be("worktree is locked.");
    }

    /// <summary>
    ///     Task B11: the standing instructions are read through <see cref="WorkspacePath.Resolve"/>, so a directory link
    ///     in the worktree that leads out of it fails the start instead of pinning a host file's text, and the worktree is
    ///     removed. Red, per assertion: read <c>Path.Combine(root, path)</c> instead, and the start goes on to the
    ///     starter, which fails for its own reason, so the first fails; drop the removal on that refusal, and the second
    ///     fails.
    /// </summary>
    [Fact]
    public async Task A_link_that_leads_out_of_the_worktree_fails_the_start_and_removes_the_worktree()
    {
        using var outside = new TempDirectory();
        await File.WriteAllTextAsync(outside.Path("AGENT.md"), "A host file.");
        using var link = DirectoryLink.Create(_worktree.Path("docs"), outside.Root);
        _config.StandingInstructionsPath = "docs/AGENT.md";

        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.Error.Should().Be(
            "the standing instructions 'docs/AGENT.md' cannot be read from the run's worktree: the path is not permitted.");
        await _workspaces.Received(1).RemoveAsync(Arg.Any<Guid>(), CancellationToken.None);
    }

    /// <summary>
    ///     Task B11: the starter reads the same canonical relative path the writer writes, so <c>./AGENT.md</c> is read,
    ///     not refused. The start then fails for the substituted store's reason, which shows the read passed. Red: read
    ///     the configured value as it is, and <see cref="WorkspacePath.Resolve"/> refuses its <c>.</c> segment.
    /// </summary>
    [Fact]
    public async Task A_non_canonical_path_is_read_as_its_canonical_form()
    {
        await File.WriteAllTextAsync(_worktree.Path("AGENT.md"), "Run dotnet test.");
        _config.StandingInstructionsPath = "./AGENT.md";

        var result = await CreateStarter().StartAsync(Request("sandbox"), CancellationToken.None);

        result.Error.Should().Contain("process 'manufacture' has no active version");
    }

    private FileStream LockedAgentMd()
    {
        File.WriteAllText(_worktree.Path("AGENT.md"), "Run dotnet test.");
        return new FileStream(_worktree.Path("AGENT.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static ManufactureStartRequest Request(string repository) => new("Tighten a guard.", repository, Starter);

    private ManufactureRunStarter CreateStarter() => new(
        new WorkflowRunStarter(_definitions, Substitute.For<IRunManifestResolver>(), Substitute.For<IWorkflowStore>()),
        _workspaces,
        _config);
}
