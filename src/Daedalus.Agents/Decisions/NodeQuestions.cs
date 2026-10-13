using Minos;

namespace Daedalus.Agents.Decisions;

/// <summary>How demanding a manufacture node's work is, lowest first. Picks the model within an agent's allowed list.</summary>
public enum Demand
{
    /// <summary>Mechanical work with a clear, local change.</summary>
    [Level("Routine work: a small, well-specified change in one or two places, following patterns already in the code.")]
    Routine,

    /// <summary>Ordinary feature work.</summary>
    [Level("Moderate work: a feature or fix that spans several files or needs some design judgement.")]
    Moderate,

    /// <summary>Work that needs the strongest model.</summary>
    [Level("Hard work: a cross-cutting or subtle change, such as concurrency, security, a public API or a migration, where a mistake is costly.")]
    Hard,
}

/// <summary>
///     The questions asked once before a manufacture node runs. The state is a JSON object of the node's role, node, visit and work_intent; the answers
///     pick a model and the tool families to keep. <c>workspace</c> and <c>daedalus</c> are never asked: they are never removable.
/// </summary>
[Questions]
public partial record NodeQuestions
{
    /// <summary>How demanding the node's work is.</summary>
    [Score("How demanding is the work this node has to do, judged from the task described in the state?")]
    public partial Score<Demand> Demand { get; }

    /// <summary>Whether Roslyn code navigation helps.</summary>
    [Noul("Would this node need to navigate or analyse C# code semantically, for example to find callers, references or implementations?")]
    public partial Noul NeedsRoslyn { get; }

    /// <summary>Whether a sandbox build and test run helps.</summary>
    [Noul("Would this node need to build the code or run its tests in a sandbox?")]
    public partial Noul NeedsSandbox { get; }

    /// <summary>Whether long-term memory helps.</summary>
    [Noul("Would this node benefit from recalling or storing long-term memory, such as earlier decisions or lessons from past runs?")]
    public partial Noul NeedsMemory { get; }

    /// <summary>Whether skills help.</summary>
    [Noul("Would this node benefit from looking up a written procedure or skill, such as a team workflow or checklist?")]
    public partial Noul NeedsSkills { get; }

    /// <summary>Whether up-to-date library documentation helps.</summary>
    [Noul("Would this node need current documentation for an external library, framework or API?")]
    public partial Noul NeedsContext7 { get; }

    /// <summary>Whether issue tracker access helps.</summary>
    [Noul("Would this node need to read or write issues or pull requests in the issue tracker?")]
    public partial Noul NeedsIssues { get; }
}
