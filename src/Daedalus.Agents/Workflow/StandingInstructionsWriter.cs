using Microsoft.Extensions.Hosting;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>Why <see cref="WorkflowRunGateway.ResumeAsync(Guid,string,string?,bool,CancellationToken)"/> refused a resume.</summary>
public enum ResumeRefusal
{
    /// <summary>The run's <c>retrospect</c> step reported no <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/> — there is nothing to apply.</summary>
    NoProposal,

    /// <summary>The standing-instructions file on disk no longer holds exactly the text pinned when the run started.</summary>
    InstructionsChangedSinceStart,

    /// <summary>The write itself failed at the filesystem level.</summary>
    WriteFailed,

    /// <summary>The workflow engine's own resume refused — e.g. the run is not awaiting the given signal.</summary>
    EngineRefused,
}

/// <summary>A resume refusal's kind plus a human-readable detail, carried by <see cref="UnitResult{E}"/>.</summary>
/// <param name="Kind">Which of the four refusal shapes this is.</param>
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
///     <b>The staleness check is a compare-then-write, not a lock.</b> Between the read of the file below and the
///     temp-file write further down, nothing stops a concurrent editor from changing the file again — the design
///     accepts that race because the actor who could lose it is a human editing <c>AGENT.md</c> by hand at the
///     same moment an operator resumes a gate, which is rare enough that a lock would cost more than it protects
///     against. What the check does guarantee is the common case: an editor who touched the file <em>before</em>
///     the resume request arrived is never silently overwritten by a proposal pinned against an older version.
/// </remarks>
public sealed class StandingInstructionsWriter(WorkflowConfig config, IHostEnvironment env)
{
    private readonly string _path = ResolvePath(config, env);

    private static string ResolvePath(WorkflowConfig config, IHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(env);

        // Same resolution rule ManufactureRunStarter's own path is built with (Daedalus.Agents' composition
        // root) — reused, not restated, so the two can never resolve the same configured value differently.
        return DaedalusAgentsServiceCollectionExtensions.ResolveStandingInstructionsPath(config.StandingInstructionsPath, env);
    }

    /// <summary>
    ///     Writes <paramref name="run"/>'s proposed standing instructions to disk, iff the file still holds
    ///     exactly the text pinned into the run's manifest when it started. Refuses without touching the file for
    ///     every other case: no proposal was ever reported, or the file has since changed. A successful write goes
    ///     through a temp file in the same directory, then <see cref="File.Move(string,string,bool)"/> with
    ///     <c>overwrite: true</c> — the rename is what keeps a reader of the file from ever observing a partial
    ///     write.
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

        var directory = Path.GetDirectoryName(_path);
        var tempPath = string.IsNullOrEmpty(directory)
            ? $"{_path}.{Guid.NewGuid():N}.tmp"
            : Path.Combine(directory, $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

        var moved = false;
        try
        {
            var pinned = PinnedText(run);
            var current = File.Exists(_path) ? await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false) : "";
            if (!string.Equals(current, pinned, StringComparison.Ordinal))
            {
                return UnitResult<ResumeFailure>.Failure(new ResumeFailure(
                    ResumeRefusal.InstructionsChangedSinceStart,
                    $"'{_path}' no longer holds the text pinned when run '{run.Id}' started; refusing to overwrite an edit made since."));
            }

            await File.WriteAllTextAsync(tempPath, proposal, ct).ConfigureAwait(false);
            File.Move(tempPath, _path, overwrite: true);
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
