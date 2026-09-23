using System.ComponentModel;
using Daedalus.Agents.Workflow;
using Thalos;

namespace Daedalus.Agents.Tools;

/// <summary>
///     The tool a manufacturing reviewer reports one lens pass through: a verdict carrying the evidence that
///     verdict is required to have. Exposed as <c>daedalus__report_review_outcome</c>.
/// </summary>
/// <remarks>
///     <b>Why this exists alongside the engine's own outcome tool.</b> Thalos' <c>OutcomeToolSchema</c> declares
///     a tool with exactly one argument — a string constrained to the node's declared outcomes. That is the
///     right shape for a branch decision and has no room for evidence, so <c>approved</c> from a reviewer that
///     read nothing is structurally identical to <c>approved</c> from one that read everything. Phase 2.2's
///     successful run is the motivating case. This tool is the second half: the engine's tool decides where the
///     run goes, this one decides whether the verdict was earned, and
///     <see cref="Daedalus.Agents.Workflow.ReviewLensRunner"/> requires the two to agree.
///     <para>
///     <b>This tool refuses a report it considers hollow</b> — an approval with nothing in <c>checked</c>, a
///     rejection whose findings carry no file, no positive line, or no failure scenario. The refusal is returned
///     as the tool call's result, so the model sees exactly what is missing and can report again. It is not a
///     throw: a rejected report is an expected outcome of this tool, not an exceptional one, and the turn
///     continues.
///     </para>
///     <para>
///     <b>Stateless on purpose.</b> It records nothing and reads nothing back. The evidence the runner acts on
///     is read off <c>AgentTurnResult.ToolCalls</c> — the arguments of the call the model actually made, already
///     captured by Thalos for audit — so there is no second store that could disagree with the turn record, and
///     no per-run state to leak between concurrent runs on the same host.
///     </para>
/// </remarks>
[ThalosToolType]
public sealed class DaedalusReviewTools
{
    /// <summary>The unqualified tool name; agents see it as <c>daedalus__report_review_outcome</c>.</summary>
    public const string ReportReviewOutcomeToolName = "report_review_outcome";

    /// <summary>The qualified name, as it appears in <c>AgentTurnResult.ToolCalls</c>.</summary>
    public const string QualifiedReportReviewOutcomeToolName =
        DaedalusAgentsServiceCollectionExtensions.KnowledgeToolSourceName + "__" + ReportReviewOutcomeToolName;

    /// <summary>Reports one lens pass's verdict with its evidence, refusing a report that carries none.</summary>
    /// <remarks>
    ///     An instance method with no instance state, which CA1822 would rather see static. It stays an instance
    ///     method because Thalos' <c>AddLocalTools</c> discovers tools by reflecting over a
    ///     <see cref="ThalosToolTypeAttribute"/> class it constructs, and whether it also picks up static members
    ///     is not documented. A tool that silently fails to register would leave every review pass reporting no
    ///     evidence at all — too large a failure to trade for one suppressed style warning.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Thalos discovers local tools by reflecting over an instance of the tool type; see remarks.")]
    [ThalosTool(ReportReviewOutcomeToolName)]
    [Description(
        "Report one review lens pass's verdict together with the evidence for it. An 'approved' verdict must " +
        "list what was checked; a 'rejected' verdict must list findings, each naming a file, a positive line " +
        "number and a concrete failure scenario. A report without its evidence is refused and not counted.")]
    public string ReportReviewOutcome(
        [Description("The review lens this pass applied: correctness, falsifiability or mechanism.")] string lens,
        [Description("The verdict for this lens only: 'approved' or 'rejected'.")] string verdict,
        [Description("JSON array of findings, required when rejecting. Each entry: {\"file\": \"src/X.cs\", \"line\": 42, \"scenario\": \"what concretely goes wrong\"}.")] string? findings = null,
        [Description("JSON array of strings, required when approving. What you examined and found sound, specific enough for a human to look at the same thing.")] string? @checked = null)
    {
        var validated = ReviewEvidence.Validate(lens, verdict, findings, @checked);
        if (validated.IsFailure)
            return $"Report refused: {validated.Error}";

        var evidence = validated.Value;
        return evidence.IsApproval
            ? $"Recorded: lens '{evidence.Lens}' approved, {evidence.Checked.Count} item(s) checked."
            : $"Recorded: lens '{evidence.Lens}' rejected, {evidence.Findings.Count} finding(s). Remaining lenses will not run.";
    }
}
