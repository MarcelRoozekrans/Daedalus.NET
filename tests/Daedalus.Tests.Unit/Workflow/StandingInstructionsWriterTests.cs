using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     <see cref="StandingInstructionsWriter"/> is the one place anything ever writes
///     <c>Thalos:Workflow:StandingInstructionsPath</c> — task B5's trust boundary between a model's proposal and a
///     human's decision. Every test here points at a <see cref="TempDirectory"/>, never a real project directory:
///     see that type's own remarks, and the task brief's "test isolation" note. Since task B11 that directory is the
///     run's worktree, which, since phase 2.6 task B5, a substituted <see cref="IRunWorkspaceHandoff"/> hands off for
///     the run's id.
/// </summary>
public sealed class StandingInstructionsWriterTests
{
    /// <summary>
    ///     Task B11: the proposal lands in the run's worktree. Red: resolve the configured path against the process's
    ///     own directory instead of the worktree, as phase 2.4 resolved it against the content root, and the first
    ///     assertion fails, because no file there holds the pinned text.
    /// </summary>
    [Fact]
    public async Task Writes_the_proposal_when_the_file_still_matches_the_pinned_text()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(dir.Path("AGENT.md"), "Run dotnet test.");
        var run = RunWith(pinned: "Run dotnet test.", proposal: "Run dotnet test.\nIntegration needs Docker.");

        (await Writer(dir, run).ApplyAsync(run, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await File.ReadAllTextAsync(dir.Path("AGENT.md"))).Should().Be("Run dotnet test.\nIntegration needs Docker.");
    }

    [Fact]
    public async Task Refuses_when_the_file_changed_since_the_run_started_and_leaves_it_alone()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(dir.Path("AGENT.md"), "Someone edited this.");
        var run = RunWith(pinned: "Run dotnet test.", proposal: "Run dotnet test.\nX.");

        var result = await Writer(dir, run).ApplyAsync(run, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        (await File.ReadAllTextAsync(dir.Path("AGENT.md"))).Should().Be("Someone edited this.");
    }

    /// <summary>
    ///     Task B11, then phase 2.6 task B5: the writer asks the handoff for the worktree the run publishes from, and a
    ///     handoff that fails, here because the stored patch cannot be read, is a write failure before anything is
    ///     resolved, carrying the handoff's message and its detail. Red, per assertion: classify every handoff failure as
    ///     <see cref="ResumeRefusal.PublishRefused"/>, and the first fails; drop the detail, keeping only the message, and
    ///     the second fails.
    /// </summary>
    [Fact]
    public async Task A_handoff_failure_is_a_write_failure_carrying_its_message_and_detail()
    {
        var run = RunWith(pinned: "", proposal: "New instructions.");
        var writer = WriterRefusedWith(run, AgentError.StoreError("Could not read the patch.", "the disk is gone"));

        var result = await writer.ApplyAsync(run, CancellationToken.None);

        result.Error.Kind.Should().Be(ResumeRefusal.WriteFailed);
        result.Error.Detail.Should().Be("Could not read the patch. the disk is gone");
    }

    /// <summary>
    ///     Ruling R57: in sandbox mode the handoff runs the publish-side protected-path check (S5), and its refusal,
    ///     which Thalos answers with <see cref="AgentErrorCode.Validation"/>, is a policy refusal, not a write failure,
    ///     carrying the message that names the refused path, and any detail. Red, per assertion: classify it as
    ///     <see cref="ResumeRefusal.WriteFailed"/>, and the first fails; drop the detail, and the second fails.
    /// </summary>
    [Fact]
    public async Task A_patch_the_publish_side_check_refuses_is_a_publish_refusal_naming_the_path()
    {
        var run = RunWith(pinned: "", proposal: "New instructions.");
        var writer = WriterRefusedWith(run, new AgentError(
            AgentErrorCode.Validation, "the change touches protected path '.github/workflows/ci.yml'; publish refused", "1 file"));

        var result = await writer.ApplyAsync(run, CancellationToken.None);

        result.Error.Kind.Should().Be(ResumeRefusal.PublishRefused);
        result.Error.Detail.Should().Be("the change touches protected path '.github/workflows/ci.yml'; publish refused 1 file");
    }

    private static StandingInstructionsWriter WriterRefusedWith(WorkflowRun run, AgentError error)
    {
        var handoff = Substitute.For<IRunWorkspaceHandoff>();
        handoff.CheckoutForPublishAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
            Result<RunWorkspace, AgentError>.Failure(error)));
        return new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, handoff);
    }

    /// <summary>
    ///     Phase 2.6, task B5: in sandbox mode the run's own worktree is in its container, and the provider's
    ///     <see cref="IRunWorkspaceProvider.FindAsync"/> answers a <c>sandbox://</c> root. The writer writes into the
    ///     host worktree the handoff checks out instead, the one publish commits. The substitute is both, as the sandbox
    ///     provider is. Red: keep using <see cref="IRunWorkspaceProvider.FindAsync"/>; <see cref="WorkspacePath.Resolve"/>
    ///     refuses the <c>sandbox://</c> root, so the first assertion fails, and nothing reaches the handoff worktree, so
    ///     the second fails.
    /// </summary>
    [Fact]
    public async Task The_writer_writes_into_the_handoff_worktree()
    {
        using var publish = new TempDirectory();
        await File.WriteAllTextAsync(publish.Path("AGENT.md"), "Run dotnet test.");
        var run = RunWith(pinned: "Run dotnet test.", proposal: "Run dotnet test.\nRestore needs the proxy.");
        var sandbox = Substitute.For<IRunWorkspaceHandoff, IRunWorkspaceProvider>();
        ((IRunWorkspaceProvider)sandbox).FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<RunWorkspace?>(
            new RunWorkspace(run.Id, "sandbox", "unused", "main", $"manufacture/{run.Id}", $"sandbox://{run.Id:N}", null)));
        sandbox.CheckoutForPublishAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
            Result<RunWorkspace, AgentError>.Success(
                new RunWorkspace(run.Id, "sandbox", "unused", "main", $"manufacture/{run.Id}", publish.Root, null))));
        var writer = new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, sandbox);

        var result = await writer.ApplyAsync(run, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Detail : null);
        (await File.ReadAllTextAsync(publish.Path("AGENT.md"))).Should().Be("Run dotnet test.\nRestore needs the proxy.");
    }

    /// <summary>
    ///     Task B11: a directory link inside the worktree that leads out of it cannot carry the write out. The file
    ///     outside holds exactly the pinned text, so a writer that followed the link would pass the staleness check and
    ///     overwrite it. Red: combine the worktree root and the configured path with <see cref="Path.Combine(string,string)"/>
    ///     instead of <see cref="WorkspacePath.Resolve"/>, and the first assertion fails, with the outside file holding
    ///     the proposal; report success on that refusal instead, and the second fails; report it as
    ///     <see cref="ResumeRefusal.InstructionsChangedSinceStart"/>, and the third fails.
    /// </summary>
    [Fact]
    public async Task A_link_that_leads_out_of_the_worktree_is_refused_and_the_file_outside_is_untouched()
    {
        using var worktree = new TempDirectory();
        using var outside = new TempDirectory();
        await File.WriteAllTextAsync(outside.Path("AGENT.md"), "Run dotnet test.");
        using var link = DirectoryLink.Create(worktree.Path("docs"), outside.Root);
        var run = RunWith(pinned: "Run dotnet test.", proposal: "Overwritten through a link.");

        var result = await Writer(worktree, run, "docs/AGENT.md").ApplyAsync(run, CancellationToken.None);

        (await File.ReadAllTextAsync(outside.Path("AGENT.md"))).Should().Be("Run dotnet test.");
        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ResumeRefusal.WriteFailed);
    }

    /// <summary>
    ///     Task B11: the writer takes the canonical relative path, the one the run starter pins from and the
    ///     <c>workspace__*</c> tools protect, so <c>./AGENT.md</c> is the worktree's <c>AGENT.md</c>. Red, per
    ///     assertion: resolve the configured value as it is, and <see cref="WorkspacePath.Resolve"/> refuses its
    ///     <c>.</c> segment, so the first fails; drop the move of the temp file onto the target, and the second fails.
    /// </summary>
    [Fact]
    public async Task A_non_canonical_path_writes_the_same_file_the_canonical_one_names()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(dir.Path("AGENT.md"), "Old instructions.");
        var run = RunWith(pinned: "Old instructions.", proposal: "New instructions.");

        var result = await Writer(dir, run, "./AGENT.md").ApplyAsync(run, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Detail : null);
        (await File.ReadAllTextAsync(dir.Path("AGENT.md"))).Should().Be("New instructions.");
    }

    /// <summary>
    ///     Task B11: the writer refuses the same values registration refuses, so a writer built outside the composition
    ///     root cannot be pointed at a host file either. Red: take the configured value without the shared check, and
    ///     nothing throws.
    /// </summary>
    [Theory]
    [InlineData("../AGENT.md")]
    [InlineData("C:/x/AGENT.md")]
    [InlineData(".git/AGENT.md")]
    [InlineData("ROOTED")]
    public void A_path_that_is_not_confined_to_the_worktree_is_refused_when_the_writer_is_built(string configured)
    {
        var path = string.Equals(configured, "ROOTED", StringComparison.Ordinal)
            ? Path.Combine(Path.GetTempPath(), "AGENT.md")
            : configured;

        var act = () => new StandingInstructionsWriter(
            new WorkflowConfig { StandingInstructionsPath = path }, Substitute.For<IRunWorkspaceHandoff>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Thalos:Workflow:StandingInstructionsPath*");
    }

    /// <summary>
    ///     Names the specific refusal kind for the "changed since start" case, which
    ///     <see cref="Refuses_when_the_file_changed_since_the_run_started_and_leaves_it_alone"/> above leaves
    ///     unchecked. Falsifiable: swapping the returned <see cref="ResumeRefusal"/> for
    ///     <see cref="ResumeRefusal.WriteFailed"/> in <c>StandingInstructionsWriter.ApplyAsync</c>'s staleness
    ///     branch turns this red while the test above stays green — it is the assertion this one adds.
    /// </summary>
    [Fact]
    public async Task Reports_the_instructions_changed_refusal_kind()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(dir.Path("AGENT.md"), "Someone edited this.");
        var run = RunWith(pinned: "Run dotnet test.", proposal: "Run dotnet test.\nX.");

        var result = await Writer(dir, run).ApplyAsync(run, CancellationToken.None);

        result.Error.Kind.Should().Be(ResumeRefusal.InstructionsChangedSinceStart);
    }

    /// <summary>
    ///     Falsifiable: replacing the guard's key lookup with a key nothing ever sets makes it unconditionally
    ///     true, so this stays green — but the same mutation turns every other test in this file red (verified:
    ///     the proposal-bearing tests can no longer find their proposal and all report <c>NoProposal</c> instead
    ///     of their own expected kind). That coupling is why this test's own falsifying edit is narrower: deleting
    ///     just the <c>string.IsNullOrEmpty(proposal)</c> disjunct leaves this test green too (the key is still
    ///     absent here) — see <see cref="Refuses_when_the_run_carries_an_empty_proposal_string"/>, which is what
    ///     that specific edit turns red.
    /// </summary>
    [Fact]
    public async Task Refuses_when_the_run_carries_no_proposal_and_writes_nothing()
    {
        using var dir = new TempDirectory();
        var run = RunWith(pinned: "", proposal: null);

        var result = await Writer(dir, run).ApplyAsync(run, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ResumeRefusal.NoProposal);
        File.Exists(dir.Path("AGENT.md")).Should().BeFalse("no proposal means nothing should ever be written, not even an empty file");
    }

    /// <summary>
    ///     The other half of the proposal-lookup guard: an empty (as opposed to absent) proposal string also
    ///     refuses. Falsifiable: deleting the <c>|| string.IsNullOrEmpty(proposal)</c> disjunct in
    ///     <c>StandingInstructionsWriter.ApplyAsync</c> turns this red — an empty proposal would then fall through
    ///     to the staleness check and, once that passed too, write an empty file.
    /// </summary>
    [Fact]
    public async Task Refuses_when_the_run_carries_an_empty_proposal_string()
    {
        using var dir = new TempDirectory();
        var run = RunWith(pinned: "", proposal: "");

        var result = await Writer(dir, run).ApplyAsync(run, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ResumeRefusal.NoProposal);
    }

    /// <summary>
    ///     A missing standing-instructions file is treated as pinned/current text <c>""</c>, matching
    ///     <see cref="Daedalus.Agents.Workflow.ManufactureRunStarter.StartAsync"/>'s own "missing file starts a run
    ///     with an empty document" rule — so a run pinned against an empty document can still apply, writing the
    ///     file for the first time. Falsifiable: changing <c>File.Exists(_path) ? ... : ""</c> to always read the
    ///     file (throwing <see cref="FileNotFoundException"/> when absent) turns this red.
    /// </summary>
    [Fact]
    public async Task Applies_against_a_standing_instructions_file_that_does_not_exist_yet()
    {
        using var dir = new TempDirectory();
        var run = RunWith(pinned: "", proposal: "First-ever standing instructions.");

        var result = await Writer(dir, run).ApplyAsync(run, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await File.ReadAllTextAsync(dir.Path("AGENT.md"))).Should().Be("First-ever standing instructions.");
    }

    /// <summary>
    ///     Falsifiable: removing the <c>catch (IOException)</c> block in
    ///     <c>StandingInstructionsWriter.ApplyAsync</c> turns this red — the unhandled
    ///     <see cref="DirectoryNotFoundException"/> (an <see cref="IOException"/> subtype)
    ///     the temp file's <see cref="FileStream"/> throws when the temp file's
    ///     directory does not exist would then propagate out of <c>ApplyAsync</c> instead of coming back as a
    ///     <see cref="ResumeRefusal.WriteFailed"/> result. The target's parent directory is never created — this
    ///     is what makes the write step itself fail, after the earlier read/staleness check (against a path that
    ///     also does not exist, so pinned <c>""</c> matches current <c>""</c>) has already succeeded.
    /// </summary>
    [Fact]
    public async Task Reports_write_failed_when_the_target_directory_does_not_exist()
    {
        using var dir = new TempDirectory();
        var run = RunWith(pinned: "", proposal: "New instructions.");
        var writer = Writer(dir, run, "missing-subdirectory/AGENT.md");

        var result = await writer.ApplyAsync(run, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ResumeRefusal.WriteFailed);
    }

    /// <summary>
    ///     Falsifiable: returning <c>LineDiff.Compute(...)</c> unconditionally, without the "no proposal" guard,
    ///     turns this red — it would return <c>"-Run dotnet test."</c> (an all-deletions diff) instead of
    ///     <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Diff_returns_null_when_the_run_carries_no_proposal()
    {
        var run = RunWith(pinned: "Run dotnet test.", proposal: null);

        StandingInstructionsWriter.Diff(run).Should().BeNull();
    }

    /// <summary>
    ///     Falsifiable: swapping <c>PinnedText(run)</c> and <c>proposal</c> in the <c>LineDiff.Compute</c> call
    ///     turns this red — the diff would then show the pinned line as added and the new line as removed.
    /// </summary>
    [Fact]
    public void Diff_shows_the_appended_line_with_a_plus_prefix()
    {
        var run = RunWith(pinned: "Run dotnet test.", proposal: "Run dotnet test.\nIntegration needs Docker.");

        StandingInstructionsWriter.Diff(run).Should().Contain("+Integration needs Docker.");
    }

    /// <summary>
    ///     Fix round 1: <c>Diff</c> now shares <c>ApplyAsync</c>'s exact "is there really a proposal" condition via
    ///     the private <c>ProposalOrNull</c> helper, so an empty-string proposal — which <c>ApplyAsync</c> already
    ///     refuses as <see cref="ResumeRefusal.NoProposal"/> — must read as "no proposal" here too, not as an
    ///     all-deletions diff. Falsifiable: reverting <c>Diff</c> to its own, narrower guard (absent or not a
    ///     string, but not empty) turns this red — it would return <c>"-Run dotnet test."</c> instead of
    ///     <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Diff_returns_null_when_the_proposal_is_an_empty_string()
    {
        var run = RunWith(pinned: "Run dotnet test.", proposal: "");

        StandingInstructionsWriter.Diff(run).Should().BeNull();
    }

    /// <summary>
    ///     Fix round 1: when <c>File.Move</c> fails after the temp file was already written — here, because the
    ///     destination is itself an existing directory, which also proves the new
    ///     <see cref="UnauthorizedAccessException"/> mapping added this round, since that is what
    ///     <see cref="File.Move(string,string,bool)"/> throws for this exact case on Windows — the orphaned temp
    ///     file must not survive in the standing-instructions directory. Falsifiable: removing the <c>finally</c>
    ///     block's cleanup turns this red — a stray <c>*.tmp</c> file would remain.
    /// </summary>
    [Fact]
    public async Task Cleans_up_the_temp_file_when_the_move_fails()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Path("AGENT.md"));
        var run = RunWith(pinned: "", proposal: "New instructions.");

        var result = await Writer(dir, run).ApplyAsync(run, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ResumeRefusal.WriteFailed);
        Directory.GetFiles(dir.Root).Should().NotContain(f => f.EndsWith(".tmp", StringComparison.Ordinal),
            "a failed move must not leave an orphaned temp file behind in the standing-instructions directory");
    }

    /// <summary>A writer whose handoff knows only <paramref name="run"/>'s worktree, <paramref name="worktree"/>.</summary>
    private static StandingInstructionsWriter Writer(TempDirectory worktree, WorkflowRun run, string path = "AGENT.md")
    {
        var handoff = Substitute.For<IRunWorkspaceHandoff>();
        handoff.CheckoutForPublishAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
            Result<RunWorkspace, AgentError>.Success(
                new RunWorkspace(run.Id, "sandbox", "unused", "main", $"manufacture/{run.Id}", worktree.Root, null))));
        return new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = path }, handoff);
    }

    private static WorkflowRun RunWith(string pinned, string? proposal)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (proposal is not null)
        {
            variables[ReviewHandoff.ProposedStandingInstructionsKey] = proposal;
        }

        return new WorkflowRun
        {
            Id = Guid.NewGuid(),
            Process = "manufacture",
            ProcessVersion = 5,
            CurrentNode = "gate",
            CurrentSeq = 1,
            Status = WorkflowStatus.Awaiting,
            AwaitingSignal = "human_approval",
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["gate"] = 1 },
            Manifest = new RunManifest
            {
                Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal),
                Documents = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ManufactureRunStarter.StandingInstructionsDocument] = pinned,
                },
            },
            Variables = variables,
        };
    }
}
