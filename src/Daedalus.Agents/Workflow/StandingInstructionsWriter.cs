using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>Why <see cref="WorkflowRunGateway.ResumeAsync(Guid,string,string?,bool,Thalos.Workflow.RunPrincipal,CancellationToken)"/> refused a resume.</summary>
public enum ResumeRefusal
{
    /// <summary>The run's <c>retrospect</c> step reported no <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/> — there is nothing to apply.</summary>
    NoProposal,

    /// <summary>The standing-instructions file on disk no longer holds exactly the text pinned when the run started.</summary>
    InstructionsChangedSinceStart,

    /// <summary>
    ///     The write could not be made: the handoff failed for a reason that is not a refusal (a git, store or provider
    ///     failure), the path is not permitted inside the worktree, or the write itself failed at the filesystem level.
    /// </summary>
    WriteFailed,

    /// <summary>
    ///     Phase 2.6, ruling R57: the handoff refused to hand off a worktree to publish from, with
    ///     <see cref="Thalos.AgentErrorCode.Validation"/>. In sandbox mode that is above all the publish-side
    ///     protected-path check (S5) refusing the run's patch, whose detail names the refused path; it also covers the
    ///     handoff's other refusals of the run's state, such as a run with no workspace or sandbox, or one another call is
    ///     creating or removing. A policy refusal, not a server fault.
    /// </summary>
    PublishRefused,

    /// <summary>The workflow engine's own resume refused — e.g. the run is not awaiting the given signal.</summary>
    EngineRefused,
}

/// <summary>A resume refusal's kind plus a human-readable detail, carried by <see cref="UnitResult{E}"/>.</summary>
/// <param name="Kind">Which of the refusal shapes this is.</param>
/// <param name="Detail">Safe to show a human operator directly — never a stack trace or a raw exception dump.</param>
public readonly record struct ResumeFailure(ResumeRefusal Kind, string Detail);

/// <summary>
///     Task B5: the one place anything writes the standing-instructions file
///     (<see cref="Daedalus.Agents.DaedalusAgentsOptions.Workflow"/>'s <c>StandingInstructionsPath</c>, default
///     <c>AGENT.md</c>) — and only through <see cref="ApplyAsync"/>, which <see cref="WorkflowRunGateway"/> calls
///     exactly once a human resumes the gate with <c>applyStandingInstructions: true</c>. The model that proposes
///     the replacement text (<c>retrospect</c>, via <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/>)
///     never has a path to disk of its own; this type, called from host code on a human's explicit say-so, is the
///     trust boundary the phase 2.4 design draws between "the model proposes" and "a human decides".
/// </summary>
/// <remarks>
///     <para>
///     <b>The file is the run's own, in its worktree.</b> Phase 2.5, task B11: the path is resolved inside the worktree
///     the run publishes from, through the same canonical relative path <see cref="ManufactureRunStarter"/> pinned the
///     run's text from and the same <see cref="WorkspacePath.Resolve"/>. Phase 2.6, task B5: that worktree comes from
///     <see cref="IRunWorkspaceHandoff.CheckoutForPublishAsync"/>. In local mode it is the run's own worktree. In
///     sandbox mode the run's worktree lives in its container, and <see cref="IRunWorkspaceProvider.FindAsync"/>
///     answers a <c>sandbox://</c> root that is no host directory, so the write goes to the trusted publish worktree
///     the run's checked patch was applied to, the one <c>OpenPullRequestAction</c> then commits.
///     That confinement replaces phase 2.4's content-root check: the host's own files are out of reach, and a link
///     inside the worktree that leads out of it is refused. The write is host code applying an approved change, so it
///     goes through this type's own file IO, not through the <c>workspace__*</c> tools, which refuse the file to a run.
///     </para>
///     <para>
///     <b>The staleness check is a compare-then-write, not a lock.</b> Between the read of the file below and the
///     temp-file write further down, nothing stops a concurrent editor from changing the file again — the design
///     accepts that race because the actor who could lose it is a human editing <c>AGENT.md</c> by hand at the
///     same moment an operator resumes a gate, which is rare enough that a lock would cost more than it protects
///     against. What the check does guarantee is the common case: an editor who touched the file <em>before</em>
///     the resume request arrived is never silently overwritten by a proposal pinned against an older version.
///     </para>
///     <para>
///     <b>Resolve and open are two steps.</b> <see cref="WorkspacePath.Resolve"/> does not close the window in which
///     a link could be swapped in after it returns. Nothing a run can do opens that window: the run is parked at the
///     gate, so no turn of its own is in flight; the <c>workspace__*</c> tools write regular files only; and the
///     worktree is checked out with <c>core.symlinks=false</c>, so a link committed to the repository arrives as a
///     text file. The temp file is opened with <see cref="FileMode.CreateNew"/>, so it is never an existing entry,
///     link or not, and the rename replaces the target's directory entry rather than writing through it.
///     </para>
/// </remarks>
public sealed class StandingInstructionsWriter(WorkflowConfig config, IRunWorkspaceHandoff handoff)
{
    private readonly string _path = DaedalusAgentsServiceCollectionExtensions.StandingInstructionsRelativePath(
        (config ?? throw new ArgumentNullException(nameof(config))).StandingInstructionsPath);

    private readonly IRunWorkspaceHandoff _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));

    /// <summary>
    ///     Writes <paramref name="run"/>'s proposed standing instructions to its worktree, iff the file still holds
    ///     exactly the text pinned into the run's manifest when it started. Refuses without touching the file for
    ///     every other case: no proposal was ever reported, no worktree could be handed off for the run, the path is
    ///     not permitted inside it, or the file has since changed. A successful write goes through a temp file in the same directory, then
    ///     <see cref="File.Move(string,string,bool)"/> with <c>overwrite: true</c> — the rename is what keeps a reader
    ///     of the file from ever observing a partial write. The target and the temp file are each resolved through
    ///     <see cref="WorkspacePath.Resolve"/>, so neither can lie outside the worktree.
    /// </summary>
    /// <remarks>
    ///     <b>Fix round 1.</b> The staleness read moved inside the <c>try</c>, so a locked or otherwise
    ///     unreadable file now reports <see cref="ResumeRefusal.WriteFailed"/> instead of throwing past this
    ///     method. <see cref="UnauthorizedAccessException"/> — thrown for a read-only file or a permissions
    ///     denial, neither of which is an <see cref="IOException"/> — is caught alongside it. A cancellation
    ///     (<see cref="OperationCanceledException"/>) is deliberately <em>not</em> caught here and propagates to
    ///     the caller, but the <c>finally</c> block below still runs first and still deletes an orphaned temp
    ///     file — cancelling a write must not leave a stray <c>.tmp</c> file behind any more than a genuine
    ///     failure should.
    /// </remarks>
    public async ValueTask<UnitResult<ResumeFailure>> ApplyAsync(WorkflowRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        var proposal = ProposalOrNull(run);
        if (proposal is null)
        {
            return UnitResult<ResumeFailure>.Failure(new ResumeFailure(
                ResumeRefusal.NoProposal,
                $"Workflow run '{run.Id}' carries no '{ReviewHandoff.ProposedStandingInstructionsKey}' proposal to apply."));
        }

        // The worktree the run publishes from: its own in local mode, the trusted publish worktree in sandbox mode.
        var handedOff = await _handoff.CheckoutForPublishAsync(run.Id, ct).ConfigureAwait(false);
        if (handedOff.IsFailure)
        {
            // Thalos answers Validation for every refusal on this path, the applier's "publish refused" included, and a
            // git, store or provider code for a failure; the detail is kept, since a refusal's names the refused path.
            var refusal = handedOff.Error.Code == AgentErrorCode.Validation ? ResumeRefusal.PublishRefused : ResumeRefusal.WriteFailed;
            return UnitResult<ResumeFailure>.Failure(new ResumeFailure(refusal, Describe(handedOff.Error)));
        }

        var workspace = handedOff.Value;

        var target = WorkspacePath.Resolve(workspace.Root, _path);
        if (target.IsFailure)
        {
            return NotPermitted(run, _path, target.Error);
        }

        // Next to the configured path, so the rename stays within one directory of the worktree.
        var tempRelative = $"{_path}.{Guid.NewGuid():N}.tmp";
        var temp = WorkspacePath.Resolve(workspace.Root, tempRelative);
        if (temp.IsFailure)
        {
            return NotPermitted(run, tempRelative, temp.Error);
        }

        var targetPath = target.Value;
        var tempPath = temp.Value;
        var moved = false;
        try
        {
            var pinned = PinnedText(run);
            var current = File.Exists(targetPath) ? await File.ReadAllTextAsync(targetPath, ct).ConfigureAwait(false) : "";
            if (!string.Equals(current, pinned, StringComparison.Ordinal))
            {
                return UnitResult<ResumeFailure>.Failure(new ResumeFailure(
                    ResumeRefusal.InstructionsChangedSinceStart,
                    $"'{_path}' in the worktree of run '{run.Id}' no longer holds the text pinned when the run started; " +
                    "refusing to overwrite an edit made since."));
            }

            var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream);
                await using (writer.ConfigureAwait(false))
                {
                    await writer.WriteAsync(proposal.AsMemory(), ct).ConfigureAwait(false);
                }
            }

            File.Move(tempPath, targetPath, overwrite: true);
            moved = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnitResult<ResumeFailure>.Failure(new ResumeFailure(ResumeRefusal.WriteFailed, ex.Message));
        }
        finally
        {
            // Cleans up whenever the temp file was created but never became the real file — a caught
            // IOException/UnauthorizedAccessException above, or an OperationCanceledException left uncaught to
            // propagate. Best-effort: a failure here is not worth masking the real outcome behind.
            if (!moved && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup only; the result already returned (or the propagating cancellation)
                    // already names the real outcome.
                }
            }
        }

        return UnitResult<ResumeFailure>.Success();
    }

    /// <summary>
    ///     A unified-style line diff of the run's pinned standing instructions against its proposal, or
    ///     <see langword="null"/> when the run carries no proposal — the exact same condition
    ///     <see cref="ApplyAsync"/> refuses <see cref="ResumeRefusal.NoProposal"/> on, via the shared
    ///     <see cref="ProposalOrNull"/> helper so the two can never drift (fix round 1: before this, <c>Diff</c>
    ///     treated an empty-string proposal as a real one and returned an all-deletions diff, while
    ///     <c>ApplyAsync</c> already refused it as no proposal at all). Read-only and side-effect free, so
    ///     <c>GET /api/workflow-runs/{id}</c> can show an operator what a resume with
    ///     <c>applyStandingInstructions: true</c> would write, before they decide.
    /// </summary>
    public static string? Diff(WorkflowRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var proposal = ProposalOrNull(run);
        return proposal is null ? null : LineDiff.Compute(PinnedText(run), proposal);
    }

    private static string Describe(AgentError error) =>
        string.IsNullOrWhiteSpace(error.Detail) ? error.Message : $"{error.Message} {error.Detail}";

    /// <summary>
    ///     A refusal for a path <see cref="WorkspacePath.Resolve"/> would not confine to the run's worktree. Its message
    ///     is generic by design, so the detail names the configured path, never a host path.
    /// </summary>
    private static UnitResult<ResumeFailure> NotPermitted(WorkflowRun run, string path, AgentError error) =>
        UnitResult<ResumeFailure>.Failure(new ResumeFailure(
            ResumeRefusal.WriteFailed, $"'{path}' is not writable in the worktree of run '{run.Id}': {error.Message}"));

    /// <summary>
    ///     <paramref name="run"/>'s reported <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/>, or
    ///     <see langword="null"/> when it is absent, not a string, or an empty string — the one "is there really a
    ///     proposal" check <see cref="ApplyAsync"/> and <see cref="Diff"/> both need and must agree on.
    /// </summary>
    private static string? ProposalOrNull(WorkflowRun run) =>
        run.Variables.TryGetValue(ReviewHandoff.ProposedStandingInstructionsKey, out var value)
            && value is string proposal
            && !string.IsNullOrEmpty(proposal)
            ? proposal
            : null;

    private static string PinnedText(WorkflowRun run) =>
        run.Manifest is not null && run.Manifest.Documents.TryGetValue(ManufactureRunStarter.StandingInstructionsDocument, out var text)
            ? text
            : "";
}
