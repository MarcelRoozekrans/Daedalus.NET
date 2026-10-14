using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.10 task B7: <see cref="ManufactureRunStarter"/> starts the <c>review-replay</c> process on an existing
///     <c>manufacture/&lt;run-id&gt;</c> branch, and refuses a base branch anywhere else or in any other shape. A refusal is
///     a <c>Result</c> failure of kind <c>Invalid</c> before a workspace or a run exists, never an exception. The workspace
///     provider and the process-definition store are substitutes; <see cref="WorkflowRunStarter"/> is the real one.
/// </summary>
public sealed class ReviewReplayStartTests
{
    private const string Branch = "manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90";

    private static readonly RunPrincipal Starter = new("u-dev", ["developer"]);

    private readonly IRunBaseFileReader _baseFiles = Substitute.For<IRunBaseFileReader>();
    private readonly IRunManifestResolver _manifests = Substitute.For<IRunManifestResolver>();
    private readonly IRunWorkspaceProvider _workspaces = Substitute.For<IRunWorkspaceProvider>();
    private readonly IProcessDefinitionStore _definitions = Substitute.For<IProcessDefinitionStore>();
    private readonly IWorkflowStore _originals = Substitute.For<IWorkflowStore>();
    private readonly WorkflowConfig _config = new();

    public ReviewReplayStartTests()
    {
        _config.Repositories.Add(new RepositoryConfig { Name = "sandbox", Remote = "https://example.invalid/sandbox.git", DefaultBranch = "trunk" });
        _workspaces.CreateAsync(Arg.Any<RunWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RunWorkspaceRequest>();
                return new ValueTask<Result<RunWorkspace, AgentError>>(Result<RunWorkspace, AgentError>.Success(new RunWorkspace(
                    request.RunId, request.Repository, request.Remote, request.DefaultBranch, request.Branch, "sandbox://run", null)));
            });
        _workspaces.RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Success()));
        _baseFiles.ReadBaseFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<string?, AgentError>>(Result<string?, AgentError>.Success(null)));
        _definitions.GetActiveVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<int?>((int?)null));
        Original(process: "manufacture", filesTouched: Touched);
    }

    /// <summary>
    ///     Red: take the message from the request's process name, and the message holds the name. The name is caller input of any
    ///     length, so the refusal does not echo it.
    /// </summary>
    [Fact]
    public async Task The_unknown_process_refusal_does_not_echo_the_name()
    {
        var name = new string('x', 500);

        var result = await CreateStarter().StartAsync(Request(name, null), CancellationToken.None);

        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
        result.Error.Message.Should().Be("unknown process; it must be manufacture or review-replay.");
    }

    /// <summary>
    ///     Task B7 fix 1: the replay is given what the original run touched, read back from the run the branch names, and
    ///     reaches the store as an opening variable beside the intent. Red, per assertion: drop the seeding, and the first
    ///     fails; look the run up by another guid than the branch's, and the second fails.
    /// </summary>
    [Fact]
    public async Task A_replay_opens_with_the_files_the_original_run_touched()
    {
        var variables = await ReplayVariablesAsync();

        variables.Should().ContainKey(ReviewHandoff.FilesTouchedKey).WhoseValue.Should().BeEquivalentTo(Touched);
        await _originals.Received(1).FindAsync(Guid.Parse(Branch["manufacture/".Length..]), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Task B7 fix 1: a replay of a run that does not exist, is not a manufacture run, or recorded no touched files is
    ///     refused as Invalid before a workspace exists. Red, per row: skip the existence check, and the missing row
    ///     throws or goes on to a workspace; skip the process check, and the review-replay row goes on; skip the content
    ///     check, and the missing, null, empty and blank rows go on. Each assertion fails on the kind or the workspace.
    /// </summary>
    [Theory]
    [InlineData("missing", null)]
    [InlineData("review-replay", "src/A.cs")]
    [InlineData("manufacture", "absent")]
    [InlineData("manufacture", "null")]
    [InlineData("manufacture", "empty")]
    [InlineData("manufacture", "blank")]
    public async Task A_replay_of_a_run_that_cannot_be_replayed_is_refused_before_any_workspace(string process, string? files)
    {
        if (string.Equals(process, "missing", StringComparison.Ordinal))
        {
            _originals.FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>((WorkflowRun?)null));
        }
        else
        {
            object? value = files switch
            {
                "absent" => Absent,
                "null" => null,
                "empty" => Array.Empty<string>(),
                "blank" => "  ",
                _ => new[] { files },
            };
            Original(process, value);
        }

        var result = await CreateStarter().StartAsync(Request("review-replay", Branch), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
        result.Error.Message.Should().Contain("baseBranch");
        await _workspaces.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    private static readonly string[] Touched = ["src/A.cs"];

    private static readonly object Absent = new();

    private void Original(string process, object? filesTouched)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!ReferenceEquals(filesTouched, Absent))
        {
            variables[ReviewHandoff.FilesTouchedKey] = filesTouched;
        }

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            Process = process,
            ProcessVersion = 9,
            CurrentNode = "done",
            CurrentSeq = 1,
            Status = WorkflowStatus.Succeeded,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal),
            Variables = variables,
        };
        _originals.FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
    }

    /// <summary>Lets the real run starter reach the store, and returns the opening variables it handed it.</summary>
    private async Task<IReadOnlyDictionary<string, object?>> ReplayVariablesAsync()
    {
        _definitions.GetActiveVersionAsync("review-replay", Arg.Any<CancellationToken>()).Returns(new ValueTask<int?>(1));
        _definitions.GetAsync("review-replay", 1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<ProcessDefinition>>(Result<ProcessDefinition>.Success(new ProcessDefinition
            {
                Name = "review-replay",
                Version = 1,
                StartNode = "done",
                Nodes = new Dictionary<string, ProcessNode>(StringComparer.Ordinal) { ["done"] = new() },
            })));
        _manifests.ResolveAsync(Arg.Any<ProcessDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<Result<RunManifest>>(Result<RunManifest>.Success(new RunManifest
            {
                Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal),
                Documents = call.Arg<IReadOnlyDictionary<string, string>>(),
            })));
        var store = Substitute.For<IWorkflowStore>();
        WorkflowStartRequest? captured = null;
        store.StartAsync(Arg.Do<WorkflowStartRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<Guid>>(Result<Guid>.Success(Guid.NewGuid())));

        var starter = new ManufactureRunStarter(
            new WorkflowRunStarter(_definitions, _manifests, store), _workspaces, _baseFiles, _config, _originals);
        await starter.StartAsync(Request("review-replay", Branch), CancellationToken.None);

        captured.Should().NotBeNull("the start must reach the store for the opening variables to be read");
        return captured!.InitialVariables!;
    }

    /// <summary>
    ///     Red, per assertion: accept a base branch whatever the process, and the start goes on to a workspace, so the
    ///     kind and the no-workspace assertions fail; drop the process name from the message, and the message fails.
    ///     Covers both an unnamed process, which is <c>manufacture</c>, and one named.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("manufacture")]
    public async Task A_base_branch_is_refused_for_manufacture(string? process)
    {
        var result = await CreateStarter().StartAsync(Request(process, Branch), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
        result.Error.Message.Should().Contain("review-replay");
        await _workspaces.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    /// <summary>
    ///     A replay reviews the code on a branch, so one without a branch has nothing to review. Red: treat a missing
    ///     branch as the default branch, and the start goes on to a workspace, so both assertions fail.
    /// </summary>
    [Fact]
    public async Task A_review_replay_without_a_base_branch_is_refused()
    {
        var result = await CreateStarter().StartAsync(Request("review-replay", null), CancellationToken.None);

        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
        result.Error.Message.Should().Contain("baseBranch");
        await _workspaces.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    /// <summary>
    ///     Only the two shipped processes can be started, so a caller cannot name an arbitrary stored definition. Red:
    ///     pass the request's process to the run starter unchecked, and the workspace is created.
    /// </summary>
    [Fact]
    public async Task An_unknown_process_is_refused_before_any_workspace()
    {
        var result = await CreateStarter().StartAsync(Request("adjudicate", null), CancellationToken.None);

        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
        result.Error.Message.Should().Contain("unknown process");
        await _workspaces.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    /// <summary>
    ///     The base branch is exactly <c>manufacture/&lt;guid&gt;</c>, in the lowercase form a run id prints in: another
    ///     prefix, a path escape, a full ref, a leading dash, whitespace, a blank, a suffix or a differently written
    ///     guid is refused. Red, by mutation:
    ///     test only that the branch <c>StartsWith("manufacture/")</c>, and the ten rows that carry the prefix and a bad
    ///     remainder are accepted; parse the remainder with <c>Guid.TryParse</c> and no round trip, and the uppercase,
    ///     no-dash, braces and whitespace-suffix rows are accepted; look for the prefix with <c>Contains</c> and cut the
    ///     remainder at the first slash, and the other-prefix, full-ref, origin, leading-dash and leading-space rows are
    ///     accepted. Every accepted row creates a workspace, which the assertion fails on.
    /// </summary>
    [Theory]
    [InlineData("main")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("manufacture/")]
    [InlineData("manufacture/main")]
    [InlineData("manufacture/../main")]
    [InlineData("manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90/extra")]
    [InlineData("manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90..main")]
    [InlineData("manufacture/0B1F6C2E-7D4A-4E53-9A1C-2F8D5E6B7A90")]
    [InlineData("manufacture/0b1f6c2e7d4a4e539a1c2f8d5e6b7a90")]
    [InlineData("manufacture/{0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90}")]
    [InlineData("refs/heads/manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90")]
    [InlineData("origin/manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90")]
    [InlineData("feature/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90")]
    [InlineData("-manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90")]
    [InlineData("--upload-pack=x")]
    [InlineData(" manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90")]
    [InlineData("manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90 ")]
    [InlineData("manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90\n")]
    [InlineData("manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a9")]
    public async Task A_base_branch_that_is_not_a_manufacture_run_branch_is_refused(string baseBranch)
    {
        var result = await CreateStarter().StartAsync(Request("review-replay", baseBranch), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ManufactureStartFailureKind.Invalid);
        result.Error.Message.Should().Contain("manufacture/<run-id>");
        await _workspaces.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    /// <summary>
    ///     A replay cuts its worktree from the given branch on the allow-listed remote, on a branch of its own named for
    ///     the replay run, never <c>manufacture/...</c>, which would collide with the reviewed branch's name. Red, per
    ///     assertion: keep the repository's default branch as the start, and the first fails; name the replay's branch
    ///     <c>manufacture/&lt;run&gt;</c>, and the second fails; take the remote from anywhere but the allow-list, and
    ///     the third fails.
    /// </summary>
    [Fact]
    public async Task A_replay_checks_out_the_given_branch_from_the_allow_listed_remote()
    {
        await CreateStarter().StartAsync(Request("review-replay", Branch), CancellationToken.None);

        var created = CreatedWorkspace();
        created.DefaultBranch.Should().Be(Branch);
        created.Branch.Should().Be($"review-replay/{created.RunId}");
        created.Remote.Should().Be("https://example.invalid/sandbox.git");
    }

    /// <summary>
    ///     The replay asks the store for its own process, not manufacture. Red: hard-code the manufacture name in the run
    ///     starter's options, and the store is asked for <c>manufacture</c>.
    /// </summary>
    [Fact]
    public async Task A_replay_starts_the_review_replay_process()
    {
        await CreateStarter().StartAsync(Request("review-replay", Branch), CancellationToken.None);

        await _definitions.Received(1).GetActiveVersionAsync("review-replay", Arg.Any<CancellationToken>());
        await _definitions.DidNotReceive().GetActiveVersionAsync("manufacture", Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A manufacture run is unchanged: it cuts from the repository's default branch on <c>manufacture/&lt;run&gt;</c>.
    ///     Red, per assertion: cut every start from a constant branch rather than the repository's default, and the
    ///     first fails; name a manufacture run's branch anything but <c>manufacture/&lt;run&gt;</c>, and the second fails.
    /// </summary>
    [Fact]
    public async Task A_manufacture_run_still_cuts_from_the_default_branch()
    {
        await CreateStarter().StartAsync(Request(null, null), CancellationToken.None);

        var created = CreatedWorkspace();
        created.DefaultBranch.Should().Be("trunk");
        created.Branch.Should().Be($"manufacture/{created.RunId}");
    }

    private RunWorkspaceRequest CreatedWorkspace() =>
        (RunWorkspaceRequest)_workspaces.ReceivedCalls()
            .Single(c => string.Equals(c.GetMethodInfo().Name, nameof(IRunWorkspaceProvider.CreateAsync), StringComparison.Ordinal))
            .GetArguments()[0]!;

    private static ManufactureStartRequest Request(string? process, string? baseBranch) =>
        new("Review this branch.", "sandbox", Starter, process, baseBranch);

    private ManufactureRunStarter CreateStarter() => new(
        new WorkflowRunStarter(_definitions, _manifests, Substitute.For<IWorkflowStore>()),
        _workspaces,
        _baseFiles,
        _config,
        _originals);
}
