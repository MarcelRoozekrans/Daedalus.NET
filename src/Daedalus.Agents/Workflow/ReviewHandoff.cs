using System.Collections.Frozen;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The variable contract between the manufacturing pipeline's <c>implement</c> and <c>review</c> nodes, and
///     the projection that enforces it:
///     <code>
///     implement  writes  { summary, files_touched, rationale }
///     review     reads   { work_intent, files_touched }
///                writes  { verdict, findings[], checked[] }
///     </code>
/// </summary>
/// <remarks>
///     <b>The reviewer never receives <c>summary</c> or <c>rationale</c>.</b> A reviewer handed the
///     implementer's account of a change reviews the account: the narrative is fluent, self-consistent, and
///     describes code that may not be there. Withholding <c>rationale</c> while passing <c>summary</c> would be
///     the same mistake in compressed form — a summary <em>is</em> rationale, shorter — so both are withheld.
///     They stay in the run record for humans and for <c>adjudicate</c>.
///     <para>
///     <b><see cref="ProjectForReview"/> is an allow-list, by construction.</b> It walks
///     <see cref="ReviewReads"/> and looks each key up, rather than walking the run's variables and removing
///     known-bad ones. The difference is what happens to a key nobody has thought of yet: a deny-list admits
///     every field a later phase adds, silently, and the leak is invisible until someone re-reads the filter. An
///     allow-list excludes it by default, and the cost of that is a loud missing value rather than a quiet extra
///     one.
///     </para>
///     <para>
///     <b>Only <see cref="ReviewReads"/> is a declared set here, and that is deliberate.</b> The
///     <c>implement</c> and <c>review</c> write sides are declared where they are enforced — the implement
///     skill's outcome-tool contract, and <see cref="ReviewEvidence"/>'s validator plus
///     <c>DaedalusReviewTools.ReportReviewOutcome</c>'s own argument names. Restating them here as key sets
///     would give the same contract two homes and no way to tell which one is in force; the ones that were
///     here were read by no production code and pinned only by a test asserting each equalled the literal it
///     was declared with, which is a test of the assignment operator. <see cref="ReviewReads"/> earns its
///     place because <see cref="ProjectForReviewNode"/> walks it.
///     </para>
///     <para>
///     <b>The gap this type carried until task B5 is closed, and the mechanism moved.</b> Against Thalos 0.8.0
///     nothing populated <c>WorkflowRun.Variables</c> at all, so the contract above described plumbing that did
///     not run. Thalos 0.9.0 carries a node's reported variables into the bag, and <c>implement</c>'s skill now
///     reports <c>summary</c>, <c>files_touched</c> and <c>rationale</c> through its outcome tool's
///     <c>variables</c> argument. With that, <b>where the projection is applied became load-bearing</b>: 0.9.0's
///     <c>WorkflowNodeDispatcher.BuildTaskText</c> renders the <em>whole</em> bag into every node's task text,
///     so a projection applied after dispatch withholds nothing. <see cref="ProjectForReviewNode"/> is
///     therefore applied by <see cref="ReviewHandoffWorkflowStore"/>, between the store and the dispatcher, so
///     the withheld keys never reach the component that renders them. See that type for the full reasoning.
///     </para>
/// </remarks>
public static class ReviewHandoff
{
    /// <summary>What was asked of this run, from its opening variables — never from the implementer.</summary>
    public const string WorkIntentKey = "work_intent";

    /// <summary>The files the implement step reports having changed; a pointer, not evidence.</summary>
    public const string FilesTouchedKey = "files_touched";

    /// <summary>The implementer's own account of its change. Withheld from the reviewer.</summary>
    public const string SummaryKey = "summary";

    /// <summary>The implementer's reasoning for its change. Withheld from the reviewer.</summary>
    public const string RationaleKey = "rationale";

    /// <summary>
    ///     The implementer's own build/run/test observations, optionally reported alongside its outcome. The one
    ///     thing <c>retrospect</c> reads — see <see cref="RetrospectReads"/> — and, like <see cref="FilesTouchedKey"/>,
    ///     a narrow, factual pointer rather than the implementer's narrative.
    /// </summary>
    public const string LearningsKey = "learnings";

    /// <summary>
    ///     The complete replacement text <c>retrospect</c> proposes for the standing instructions file, reported
    ///     on its <c>proposed</c> outcome. B5 is what ever writes this to disk, and only after a human resumes the
    ///     gate the retrospect node's branch always leads to — this key is a proposal sitting in the run record,
    ///     never a write.
    /// </summary>
    public const string ProposedStandingInstructionsKey = "proposed_standing_instructions";

    /// <summary>
    ///     The <c>retrospect</c> outcome that carries a proposal. The skill's other outcome, <c>none</c>, carries
    ///     none. <see cref="ReviewHandoffWorkflowStore"/> keeps a reported <see cref="ProposedStandingInstructionsKey"/>
    ///     only on this outcome and clears the key on every other retrospect completion, so the value a human sees
    ///     at the gate is always the one this run's retrospect turn reported, or nothing.
    /// </summary>
    public const string RetrospectProposedOutcome = "proposed";

    /// <summary>The skill name <c>implement</c> is pinned to. Named here so it sits beside <see cref="RetrospectSkillName"/> rather than as a bare literal at the one other call site that needs it, <see cref="StandingInstructionsRunner"/>.</summary>
    public const string ImplementSkillName = "manufacture-implement";

    /// <summary>The skill name a <c>retrospect</c> node is pinned to — what <see cref="ReviewHandoffWorkflowStore.ProjectionForAsync"/> and <see cref="StandingInstructionsRunner"/> both key off.</summary>
    public const string RetrospectSkillName = "manufacture-retrospect";

    /// <summary>
    ///     The only keys a <c>review</c> dispatch is given. <see cref="FilesTouchedKey"/> is the sole thing the
    ///     implementer writes that appears here: a pointer travels, an account does not.
    /// </summary>
    public static readonly FrozenSet<string> ReviewReads =
        new[] { WorkIntentKey, FilesTouchedKey }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The only keys a <c>retrospect</c> dispatch is given: the implementer's <see cref="LearningsKey"/>.
    ///     Never <see cref="SummaryKey"/> or <see cref="RationaleKey"/> — retrospect proposes a change to the
    ///     standing instructions from durable facts, not from the implementer's narrative, for the same reason
    ///     the reviewer does not read either. The pinned standing instructions themselves travel a different way:
    ///     as a manifest document, appended to the task text by <see cref="StandingInstructionsRunner"/>, not as a
    ///     run variable — so they are not in this set.
    /// </summary>
    public static readonly FrozenSet<string> RetrospectReads =
        new[] { LearningsKey }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     Projects a run's variables down to <see cref="ReviewReads"/>. Keys outside that set are not copied;
    ///     keys inside it that the run does not carry are simply absent from the result rather than present and
    ///     empty, so the reviewer can tell "not supplied" from "supplied as nothing".
    /// </summary>
    /// <param name="variables">The run's accumulated variables, or <see langword="null"/>.</param>
    public static IReadOnlyDictionary<string, string> ProjectForReview(IReadOnlyDictionary<string, object?>? variables)
    {
        var projected = new Dictionary<string, string>(ReviewReads.Count, StringComparer.Ordinal);
        if (variables is null || variables.Count == 0)
            return projected;

        foreach (var key in ReviewReads)
        {
            if (variables.TryGetValue(key, out var value) && value is not null)
                projected[key] = Render(value);
        }

        return projected;
    }

    /// <summary>
    ///     Projects a run's variables down to <see cref="ReviewReads"/>, keeping each value as it stands rather
    ///     than rendering it. Used by <see cref="ReviewHandoffWorkflowStore"/> to narrow the bag a
    ///     <c>review</c> dispatch is built from, where the values go on to be rendered by Thalos' own
    ///     <c>WorkflowVariableBlock</c> - which escapes and bounds them - so flattening them to strings here
    ///     would throw away type information for nothing.
    /// </summary>
    /// <param name="variables">The run's accumulated variables.</param>
    public static IReadOnlyDictionary<string, object?> ProjectForReviewNode(IReadOnlyDictionary<string, object?> variables) =>
        Project(variables, ReviewReads);

    /// <summary>
    ///     Projects a run's variables down to an arbitrary declared read set, keeping each value as it stands —
    ///     the same allow-list shape <see cref="ProjectForReviewNode"/> has always had, generalised in task B4 so
    ///     <see cref="ReviewHandoffWorkflowStore"/> can apply it to <see cref="RetrospectReads"/> as well as
    ///     <see cref="ReviewReads"/> without a second copy of the walk.
    /// </summary>
    /// <param name="variables">The run's accumulated variables.</param>
    /// <param name="reads">The declared set of keys the dispatch may see.</param>
    public static IReadOnlyDictionary<string, object?> Project(IReadOnlyDictionary<string, object?> variables, IReadOnlySet<string> reads)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(reads);

        var projected = new Dictionary<string, object?>(reads.Count, StringComparer.Ordinal);
        foreach (var key in reads)
        {
            if (variables.TryGetValue(key, out var value))
                projected[key] = value;
        }

        return projected;
    }

    private static string Render(object value) => value switch
    {
        string s => s,
        IEnumerable<string> many => string.Join(", ", many),
        _ => value.ToString() ?? "",
    };
}
