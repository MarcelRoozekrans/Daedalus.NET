using System.Text.RegularExpressions;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The one neutralisation for every framing tag a turn's task text is built with: the host's own blocks,
///     <c>work-intent</c>, <c>review-findings</c> and <c>standing-instructions</c>, and the engine's,
///     <c>workflow-variables</c>, <c>skill</c>, <c>skills</c> and <c>memories</c>.
/// </summary>
/// <remarks>
///     <para>
///     <b>What it does.</b> It escapes the <c>&lt;</c> of every opening or closing spelling of those tags, in any casing
///     and with whitespace around the slash, and keeps the rest verbatim, so the text still reads correctly and cannot
///     open or close anything. The word boundary keeps the escape to real tags, so <c>&lt;skillset</c> is an ordinary
///     word. It has the same shape as Thalos' <c>SkillBlock</c> and <c>WorkflowVariableBlock</c> tag guards, restated
///     because Thalos keeps <c>NeutralizeTag</c> internal.
///     </para>
///     <para>
///     <b>Who uses it.</b> <see cref="WorkIntentRunner"/>, <see cref="ReviewFindingsRunner"/> and
///     <see cref="StandingInstructionsRunner"/> run their block's text through it. <see cref="ReviewHandoffWorkflowStore"/>
///     runs every string variable of the dispatcher's copy of the bag through it as well. Thalos' variable rendering only
///     escapes its own <c>workflow-variables</c> tag, so before that an agent-written value, or a board task's own text in
///     <c>work_intent</c>, could forge a host block ahead of the real one. Phase 2.10 task B11, fix round 1.
///     </para>
/// </remarks>
internal static partial class FramingTags
{
    /// <summary><paramref name="text"/> with the <c>&lt;</c> of every framing tag escaped. See this type's remarks.</summary>
    internal static string Neutralise(string text) =>
        Pattern().Replace(text, static m => string.Concat("&lt;", m.ValueSpan[1..]));

    [GeneratedRegex(
        @"<\s*/?\s*(?:work-intent|review-findings|standing-instructions|workflow-variables|memories|skills?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)] // MA0009: timeout (the pattern is linear)
    private static partial Regex Pattern();
}
