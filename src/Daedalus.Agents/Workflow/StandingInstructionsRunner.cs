using System.Collections.Frozen;
using System.Text.RegularExpressions;
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
///     <b>The text is agent-proposed and human-approved, so it is treated as untrusted.</b> Since task B5, the
///     file is no longer only something a human writes by hand. A <c>retrospect</c> turn proposes a complete
///     replacement, and a human resuming the gate with <c>applyStandingInstructions</c> writes it verbatim. A
///     human approved that text, but a model wrote it, from <c>learnings</c> another model reported. A reviewer
///     reading a diff can miss one forged tag. So <see cref="BuildBlock"/> neutralises every tag an injected
///     body could use to forge the framing around it, not just an accidental closing tag.
///     </para>
///     <para>
///     <b>What is neutralised mirrors Thalos' own inlined-skill sanitising.</b> For a pinned node, Thalos puts
///     the skill body in the same <c>Task</c> string, running it through <c>SkillBlock.SanitizeBody</c> and then
///     <c>WorkflowVariableBlock.NeutralizeTag</c>. That escapes the <c>&lt;</c> of every opening or closing
///     <c>skill</c>, <c>skills</c>, <c>memories</c> and <c>workflow-variables</c> tag. It matches any casing and
///     allows whitespace around the slash, and it normalises line endings to <c>\n</c> first. This block lands in
///     that same <c>Task</c> string, so it neutralises the same family plus its own
///     <c>standing-instructions</c> tag, the same way. Without that, a body could close this block early, or
///     forge a <c>&lt;workflow-variables&gt;</c> or <c>&lt;skill&gt;</c> block the model would read as the
///     engine's own. The <c>&lt;</c> becomes <c>&amp;lt;</c> and the rest of the tag is kept verbatim, so the
///     text still reads correctly and cannot open or close anything. The rule is restated here rather than
///     called, because Thalos keeps <c>NeutralizeTag</c> internal.
///     </para>
///     <para>
///     <b>Placed outside <see cref="ReviewLensRunner"/>, not inside it.</b> A review node's pinned skill is
///     <c>manufacture-review</c>, never one of the two eligible skills, so this type is inert for every lens pass
///     regardless of where it sits in the chain — but sitting outside means the eligibility check, and the string
///     concatenation it guards, run once per node dispatch rather than once per lens pass.
///     </para>
/// </remarks>
internal sealed partial class StandingInstructionsRunner(ISubagentRunner inner) : ISubagentRunner
{
    /// <summary>The pinned skills whose turn is handed the standing instructions alongside its own task.</summary>
    private static readonly FrozenSet<string> EligibleSkills =
        new[] { ReviewHandoff.ImplementSkillName, ReviewHandoff.RetrospectSkillName }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The block's opening tag. <c>internal</c> so a test can hold <c>skills/manufacture-retrospect/SKILL.md</c>,
    ///     which shows the model this exact tag, to the same text.
    /// </summary>
    internal const string OpenTag =
        "<standing-instructions note=\"this project's build, run and test instructions as of when this run started; " +
        "human-approved, and may include text an agent proposed\">";

    private const string CloseTag = "</standing-instructions>";

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

    /// <summary>
    ///     Wraps <paramref name="text"/> in the delimited tag, after normalising its line endings and neutralising
    ///     every framing tag it contains. See this type's remarks for which tags, and why.
    /// </summary>
    private static string BuildBlock(string text)
    {
        var neutralised = FramingTag().Replace(
            text.ReplaceLineEndings("\n"), static m => string.Concat("&lt;", m.ValueSpan[1..]));
        return string.Concat(OpenTag, "\n", neutralised, "\n", CloseTag);
    }

    // Escapes the '<' of every opening or closing spelling of <standing-instructions>, <workflow-variables>,
    // <skill>, <skills> and <memories>, in any casing and with whitespace around the slash, and keeps the rest
    // verbatim. The same shape as Thalos' SkillBlock and WorkflowVariableBlock tag guards, merged into one
    // pattern. The word boundary keeps the escape to real tags: "<skillset" is an ordinary word.
    [GeneratedRegex(
        @"<\s*/?\s*(?:standing-instructions|workflow-variables|memories|skills?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)] // MA0009: timeout (the pattern is linear)
    private static partial Regex FramingTag();
}
