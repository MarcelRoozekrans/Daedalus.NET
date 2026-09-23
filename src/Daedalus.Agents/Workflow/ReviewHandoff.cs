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
///     <b>The gap this type carried until task B5 is closed, and the mechanism moved.</b> Against Thalos 0.8.0
///     nothing populated <c>WorkflowRun.Variables</c> at all, so the contract above described plumbing that did
///     not run. Thalos 0.9.0 carries a node's reported variables into the bag, and <c>implement</c>'s skill now
///     reports all three of <see cref="ImplementWrites"/> through its outcome tool's <c>variables</c> argument.
///     With that, <b>where the projection is applied became load-bearing</b>: 0.9.0's
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

    /// <summary>The reviewer's verdict.</summary>
    public const string VerdictKey = "verdict";

    /// <summary>The reviewer's findings, required to reject.</summary>
    public const string FindingsKey = "findings";

    /// <summary>What the reviewer examined and found sound, required to approve.</summary>
    public const string CheckedKey = "checked";

    /// <summary>What the <c>implement</c> node declares it writes.</summary>
    public static readonly FrozenSet<string> ImplementWrites =
        new[] { SummaryKey, FilesTouchedKey, RationaleKey }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The only keys a <c>review</c> dispatch is given. Note that <see cref="FilesTouchedKey"/> is the sole
    ///     member of <see cref="ImplementWrites"/> that appears here.
    /// </summary>
    public static readonly FrozenSet<string> ReviewReads =
        new[] { WorkIntentKey, FilesTouchedKey }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>What the <c>review</c> node declares it writes.</summary>
    public static readonly FrozenSet<string> ReviewWrites =
        new[] { VerdictKey, FindingsKey, CheckedKey }.ToFrozenSet(StringComparer.Ordinal);

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
    public static IReadOnlyDictionary<string, object?> ProjectForReviewNode(IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var projected = new Dictionary<string, object?>(ReviewReads.Count, StringComparer.Ordinal);
        foreach (var key in ReviewReads)
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
