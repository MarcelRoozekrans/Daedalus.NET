using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Prd;

/// <summary>
///     The PRD reducers. Pure: no I/O, no clock. Only the newest request's answer lands; see <see cref="PrdState"/>.
/// </summary>
public static class PrdReducers
{
    /// <summary>The wizard asks for the requirements for the picked project.</summary>
    [Reducer]
    public static PrdState On(PrdState s, PrdProjectSelected a) =>
        s with { Step = PrdStep.RequirementsInput, ProjectId = a.ProjectId };

    /// <summary>
    ///     Marks the newest request and clears the last failure, as the page always did on Generate and Convert. A start
    ///     seen out of order never moves the sequence back.
    /// </summary>
    [Reducer]
    public static PrdState On(PrdState s, PrdRequestStarted a) =>
        s with { Sequence = Math.Max(s.Sequence, a.Sequence), Error = null };

    [Reducer]
    public static PrdState On(PrdState s, PrdGenerated a) =>
        a.Sequence == s.Sequence ? s with { Prd = a.Prd, Step = PrdStep.PrdReview } : s;

    [Reducer]
    public static PrdState On(PrdState s, PrdConverted a) =>
        a.Sequence == s.Sequence ? s with { CreatedTasks = a.Tasks, Step = PrdStep.TasksCreated } : s;

    /// <summary>The failure shows on the step that asked; the wizard does not move.</summary>
    [Reducer]
    public static PrdState On(PrdState s, PrdRequestFailed a) =>
        a.Sequence == s.Sequence ? s with { Error = a.Error } : s;

    [Reducer]
    public static PrdState On(PrdState s, PrdErrorDismissed a) => s with { Error = null };

    /// <summary>Back to the first step with nothing held; the sequence is kept. See <see cref="PrdState"/>.</summary>
    [Reducer]
    public static PrdState On(PrdState s, PrdReset a) => PrdState.Empty with { Sequence = s.Sequence };

    /// <summary>Clears the page's fields and keeps the sequence; see <see cref="PrdState"/>.</summary>
    [Reducer]
    public static PrdState On(PrdState s, PrdClosed a) => PrdState.Empty with { Sequence = s.Sequence };
}
