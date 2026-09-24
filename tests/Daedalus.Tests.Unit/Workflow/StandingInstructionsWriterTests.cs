using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Hosting;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     <see cref="StandingInstructionsWriter"/> is the one place anything ever writes
///     <c>Thalos:Workflow:StandingInstructionsPath</c> — task B5's trust boundary between a model's proposal and a
///     human's decision. Every test here points at a <see cref="TempDirectory"/>, never a real project directory:
///     see that type's own remarks, and the task brief's "test isolation" note.
/// </summary>
public sealed class StandingInstructionsWriterTests
{
    [Fact]
    public async Task Writes_the_proposal_when_the_file_still_matches_the_pinned_text()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.Path("AGENT.md"), "Run dotnet test.");
        var run = RunWith(pinned: "Run dotnet test.", proposal: "Run dotnet test.\nIntegration needs Docker.");

        (await Writer(dir).ApplyAsync(run, CancellationToken.None)).IsSuccess.Should().BeTrue();
        File.ReadAllText(dir.Path("AGENT.md")).Should().Be("Run dotnet test.\nIntegration needs Docker.");
    }

    [Fact]
    public async Task Refuses_when_the_file_changed_since_the_run_started_and_leaves_it_alone()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.Path("AGENT.md"), "Someone edited this.");
        var run = RunWith(pinned: "Run dotnet test.", proposal: "Run dotnet test.\nX.");

        var result = await Writer(dir).ApplyAsync(run, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        File.ReadAllText(dir.Path("AGENT.md")).Should().Be("Someone edited this.");
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
        File.WriteAllText(dir.Path("AGENT.md"), "Someone edited this.");
        var run = RunWith(pinned: "Run dotnet test.", proposal: "Run dotnet test.\nX.");

        var result = await Writer(dir).ApplyAsync(run, CancellationToken.None);

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

        var result = await Writer(dir).ApplyAsync(run, CancellationToken.None);

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

        var result = await Writer(dir).ApplyAsync(run, CancellationToken.None);

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

        var result = await Writer(dir).ApplyAsync(run, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        File.ReadAllText(dir.Path("AGENT.md")).Should().Be("First-ever standing instructions.");
    }

    /// <summary>
    ///     Falsifiable: removing the <c>catch (IOException)</c> block in
    ///     <c>StandingInstructionsWriter.ApplyAsync</c> turns this red — the unhandled
    ///     <see cref="DirectoryNotFoundException"/> (an <see cref="IOException"/> subtype)
    ///     <see cref="File.WriteAllTextAsync(string,string,CancellationToken)"/> throws when the temp file's
    ///     directory does not exist would then propagate out of <c>ApplyAsync</c> instead of coming back as a
    ///     <see cref="ResumeRefusal.WriteFailed"/> result. The target's parent directory is never created — this
    ///     is what makes the write step itself fail, after the earlier read/staleness check (against a path that
    ///     also does not exist, so pinned <c>""</c> matches current <c>""</c>) has already succeeded.
    /// </summary>
    [Fact]
    public async Task Reports_write_failed_when_the_target_directory_does_not_exist()
    {
        using var dir = new TempDirectory();
        var writer = new StandingInstructionsWriter(
            new WorkflowConfig { StandingInstructionsPath = dir.Path("missing-subdirectory/AGENT.md") },
            Substitute.For<IHostEnvironment>());
        var run = RunWith(pinned: "", proposal: "New instructions.");

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

        var result = await Writer(dir).ApplyAsync(run, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ResumeRefusal.WriteFailed);
        Directory.GetFiles(dir.Root).Should().NotContain(f => f.EndsWith(".tmp", StringComparison.Ordinal),
            "a failed move must not leave an orphaned temp file behind in the standing-instructions directory");
    }

    private static StandingInstructionsWriter Writer(TempDirectory dir) =>
        new(new WorkflowConfig { StandingInstructionsPath = dir.Path("AGENT.md") }, Substitute.For<IHostEnvironment>());

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
