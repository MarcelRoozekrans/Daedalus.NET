using System.Collections.Frozen;
using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Ports Ralph's <c>AGENT.md</c> maintenance loop onto the workflow engine: appends the run's pinned standing
///     instructions to the task text of exactly the two nodes that need to see them — <c>implement</c>, which is
///     meant to follow them, and <c>retrospect</c>, which proposes a change to them — and passes every other
///     node's request through unchanged.
/// </summary>
/// <remarks>
///     <b>Why a decorator on <see cref="ISubagentRunner"/>, and not a variable.</b> The text is host-pinned at
///     start, kept at full length, under <see cref="Thalos.Workflow.RunManifest.Documents"/> — never a run
///     variable, because <see cref="Thalos.Workflow.WorkflowVariableBlock"/> cuts a string value at 512
///     characters when it renders the variables block, and a project's build/run/test instructions routinely
///     exceed that. A manifest document is the one channel <c>ManufactureRunStarter</c> already pins full text
///     onto, so this type reads it from there rather than inventing a second one.
///     <para>
///     <b>Eligibility keys off the pinned skill, not the node name.</b> <see cref="WorkflowCaller.Run"/>
///     carries <see cref="Thalos.Workflow.WorkflowRun.Manifest"/>, whose <see cref="Thalos.Workflow.NodePin.SkillName"/>
///     for the run's current node is the exact skill the dispatcher pinned this turn to — the same value a
///     process author writes under <c>skill:</c> in the process file, checked against
///     <see cref="ReviewHandoff.ImplementSkillName"/> and <see cref="ReviewHandoff.RetrospectSkillName"/> rather
///     than against <see cref="Thalos.Workflow.WorkflowRun.CurrentNode"/>. A node is free to rename itself in the
///     graph; the skill it runs is the contract this type actually cares about.
///     </para>
///     <para>
///     <b>Only the closing tag is neutralised, deliberately narrower than <c>WorkflowVariableBlock</c>'s own
///     escaping.</b> The standing instructions are host-authored — maintained by a human editing a file on disk,
///     not written by an agent turn — so they are not the adversarial channel <c>WorkflowVariableBlock</c>
///     guards against. The one thing worth guarding against here is an accidental <c>&lt;/standing-instructions&gt;</c>
///     line inside the file itself closing the block early and running the rest of the file as if it were the
///     turn's own instruction; replacing it with <c>&lt;/standing-instructions-escaped&gt;</c> is enough to stop
///     that without treating a human-maintained file as hostile input.
///     <see cref="BuildBlock"/>'s <c>Replace</c> call is exact-case (<see cref="StringComparison.Ordinal"/>), so
///     a differently-cased tag — <c>&lt;/Standing-Instructions&gt;</c>, say — would not be neutralised. That is
///     accepted rather than fixed: the file is maintained by a human who is not trying to defeat this check, the
///     tag's own spelling (<see cref="CloseTag"/>) is lower-case throughout this type, and a case-insensitive
///     match would cost a culture-aware comparison for a threat model that does not exist here.
///     </para>
///     <para>
///     <b>Placed outside <see cref="ReviewLensRunner"/>, not inside it.</b> A review node's pinned skill is
///     <c>manufacture-review</c>, never one of the two eligible skills, so this type is inert for every lens pass
///     regardless of where it sits in the chain — but sitting outside means the eligibility check, and the string
///     concatenation it guards, run once per node dispatch rather than once per lens pass.
///     </para>
/// </remarks>
internal sealed class StandingInstructionsRunner(ISubagentRunner inner) : ISubagentRunner
{
    /// <summary>The pinned skills whose turn is handed the standing instructions alongside its own task.</summary>
    private static readonly FrozenSet<string> EligibleSkills =
        new[] { ReviewHandoff.ImplementSkillName, ReviewHandoff.RetrospectSkillName }.ToFrozenSet(StringComparer.Ordinal);

    private const string OpenTag =
        "<standing-instructions note=\"this project's build, run and test instructions as of when this run started; maintained by humans\">";

    private const string CloseTag = "</standing-instructions>";

    private const string EscapedCloseTag = "</standing-instructions-escaped>";

    private readonly ISubagentRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var text = ResolveStandingInstructions(request);
        return string.IsNullOrEmpty(text)
            ? _inner.RunAsync(request, ct)
            : _inner.RunAsync(request with { Task = request.Task + "\n" + BuildBlock(text) }, ct);
    }

    /// <summary>
    ///     The run's pinned standing-instructions document, or <see langword="null"/> when this request is not a
    ///     workflow-run turn, the run carries no manifest, the manifest names no such document, the document is
    ///     empty, or the current node's pinned skill is not one of <see cref="EligibleSkills"/>.
    /// </summary>
    private static string? ResolveStandingInstructions(SubagentRunRequest request)
    {
        if (request.Caller is not WorkflowCaller caller)
        {
            return null;
        }

        var manifest = caller.Run.Manifest;
        if (manifest is null
            || !manifest.Documents.TryGetValue(ManufactureRunStarter.StandingInstructionsDocument, out var text)
            || string.IsNullOrEmpty(text))
        {
            return null;
        }

        return manifest.Nodes.TryGetValue(caller.Run.CurrentNode, out var pin) && EligibleSkills.Contains(pin.SkillName)
            ? text
            : null;
    }

    /// <summary>Wraps <paramref name="text"/> in the delimited tag, neutralising a closing tag the text itself contains.</summary>
    private static string BuildBlock(string text)
    {
        var escaped = text.Replace(CloseTag, EscapedCloseTag, StringComparison.Ordinal);
        return string.Concat(OpenTag, "\n", escaped, "\n", CloseTag);
    }
}
