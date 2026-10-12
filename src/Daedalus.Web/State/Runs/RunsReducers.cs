using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Runs;

/// <summary>The runs feature's reducers. Pure: no I/O, no clock.</summary>
public static class RunsReducers
{
    [Reducer]
    public static RunsState On(RunsState s, RunListRequested a) => s with
    {
        Query = a.Query,
        PreviousCursors = a.Query.After is null ? [] : s.PreviousCursors,
        ListLoading = true,
        ListError = null,
        ListRequested = true,
    };

    /// <summary>
    ///     A page for another query is stale. So is a page that lands after the list page closed, with no page asked
    ///     for since.
    /// </summary>
    [Reducer]
    public static RunsState On(RunsState s, RunListLoaded a) =>
        a.Query != s.Query || !s.ListRequested ? s : s with { List = a.List, ListLoading = false, ListError = null };

    /// <summary>A failure is stale on the same terms as <see cref="RunListLoaded"/>.</summary>
    [Reducer]
    public static RunsState On(RunsState s, RunListFailed a) =>
        a.Query != s.Query || !s.ListRequested ? s : s with { ListLoading = false, ListError = a.Error };

    [Reducer]
    public static RunsState On(RunsState s, RunListClosed _) =>
        s with { List = null, ListLoading = false, ListError = null, ListRequested = false };

    [Reducer]
    public static RunsState On(RunsState s, RunClosed _) => s with
    {
        SelectedId = null,
        Selected = null,
        SelectedLoading = false,
        SelectedNotFound = false,
        SelectedError = null,
        Gate = GateDraft.Empty,
    };

    [Reducer]
    public static RunsState On(RunsState s, RunListPagedForward a) =>
        s with { PreviousCursors = [.. s.PreviousCursors, s.Query.After], Query = s.Query with { After = a.Next } };

    [Reducer]
    public static RunsState On(RunsState s, RunListPagedBack _) => s.PreviousCursors.Count == 0
        ? s
        : s with
        {
            Query = s.Query with { After = s.PreviousCursors[^1] },
            PreviousCursors = [.. s.PreviousCursors.Take(s.PreviousCursors.Count - 1)],
        };

    [Reducer]
    public static RunsState On(RunsState s, RunSelected a) => a.Id == s.SelectedId
        ? s
        : s with { SelectedId = a.Id, Selected = null, SelectedNotFound = false, SelectedError = null, Gate = GateDraft.Empty };

    [Reducer]
    public static RunsState On(RunsState s, RunLoadStarted a) =>
        a.Id != s.SelectedId ? s : s with { SelectedLoading = s.Selected is null };

    [Reducer]
    public static RunsState On(RunsState s, RunLoaded a) =>
        a.Run.Id != s.SelectedId
            ? s
            : s with { Selected = a.Run, SelectedLoading = false, SelectedNotFound = false, SelectedError = null };

    [Reducer]
    public static RunsState On(RunsState s, RunNotFound a) =>
        a.Id != s.SelectedId ? s : s with { Selected = null, SelectedLoading = false, SelectedNotFound = true };

    [Reducer]
    public static RunsState On(RunsState s, RunLoadFailed a) =>
        a.Id != s.SelectedId ? s : s with { SelectedLoading = false, SelectedError = a.Error };

    [Reducer]
    public static RunsState On(RunsState s, GateFindingToggled a)
    {
        var dropped = new HashSet<string>(s.Gate.Dropped, StringComparer.Ordinal);
        if (a.Dropped)
        {
            dropped.Add(a.FindingId);
        }
        else
        {
            dropped.Remove(a.FindingId);
        }

        return s with { Gate = s.Gate with { Dropped = dropped } };
    }

    [Reducer]
    public static RunsState On(RunsState s, GateApplyToggled a) =>
        s with { Gate = s.Gate with { ApplyStandingInstructions = a.Apply } };

    [Reducer]
    public static RunsState On(RunsState s, GateSubmitStarted _) => s with { Gate = s.Gate with { Submitting = true, Error = null } };

    [Reducer]
    public static RunsState On(RunsState s, GateSubmitSucceeded _) => s with { Gate = GateDraft.Empty };

    [Reducer]
    public static RunsState On(RunsState s, GateSubmitFailed a) =>
        s with { Gate = s.Gate with { Submitting = false, Error = a.Error } };

    [Reducer]
    public static RunsState On(RunsState s, AwaitingApprovalLoaded a) =>
        s with { AwaitingApproval = a.Count, AwaitingApprovalMore = a.More };

    [Reducer]
    public static RunsState On(RunsState s, HubStatusChanged a) => s with { Hub = a.Status };
}
