using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.State.Runs;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The runs feature's reducers: pure functions of state and action.</summary>
public sealed class RunsReducersTests
{
    private static readonly RunsState Empty = RunsState.Initial(null!);

    /// <summary>
    ///     A run loaded for another id than the selected one is ignored, so a slow response cannot overwrite the page.
    ///     Red: drop the id check in the RunLoaded reducer.
    /// </summary>
    [Fact]
    public void A_stale_run_is_ignored()
    {
        var selected = RunsReducers.On(Empty, new RunSelected(Guid.NewGuid()));

        var after = RunsReducers.On(selected, new RunLoaded(RunViews.Sample(Guid.NewGuid())));

        after.Selected.Should().BeNull();
    }

    /// <summary>
    ///     Selecting another run clears the gate draft; reselecting the same run keeps it.
    ///     Red: never clear the draft. Red: always clear it.
    /// </summary>
    [Fact]
    public void Selecting_another_run_clears_the_gate_draft()
    {
        var id = Guid.NewGuid();
        var drafted = RunsReducers.On(RunsReducers.On(Empty, new RunSelected(id)), new GateFindingToggled("mechanism-1", Dropped: true));

        RunsReducers.On(drafted, new RunSelected(id)).Gate.Dropped.Should().Contain("mechanism-1");
        RunsReducers.On(drafted, new RunSelected(Guid.NewGuid())).Gate.Dropped.Should().BeEmpty();
    }

    /// <summary>
    ///     Toggling a finding adds and removes its id; a submit failure keeps the draft and records the reason.
    ///     Red: replace the set instead of adding. Red: clear Dropped on GateSubmitFailed.
    ///     Red: drop the reason on GateSubmitFailed. Red: leave Submitting set on GateSubmitFailed.
    /// </summary>
    [Fact]
    public void The_gate_draft_follows_the_toggles()
    {
        var state = RunsReducers.On(Empty, new GateFindingToggled("a", Dropped: true));
        state = RunsReducers.On(state, new GateFindingToggled("b", Dropped: true));
        state = RunsReducers.On(state, new GateFindingToggled("a", Dropped: false));
        state = RunsReducers.On(state, new GateSubmitStarted());
        state = RunsReducers.On(state, new GateSubmitFailed("conflict"));

        state.Gate.Dropped.Should().BeEquivalentTo(["b"]);
        state.Gate.Error.Should().Be("conflict");
        state.Gate.Submitting.Should().BeFalse();
    }

    /// <summary>
    ///     A list response that arrives after the query changed is ignored.
    ///     Red: drop the query comparison in the RunListLoaded reducer.
    /// </summary>
    [Fact]
    public void A_stale_list_is_ignored()
    {
        var awaiting = RunsReducers.On(Empty, new RunListRequested(RunListQuery.First with { Status = "Awaiting" }));

        var after = RunsReducers.On(awaiting, new RunListLoaded(RunListQuery.First, new WorkflowRunList([], null)));

        after.List.Should().BeNull();
    }

    /// <summary>
    ///     A list response still in flight when the list page closes does not bring the list back.
    ///     Red: drop the ListRequested guard in the RunListLoaded reducer. Red: keep ListRequested on RunListClosed.
    /// </summary>
    [Fact]
    public void A_list_that_lands_after_the_page_closed_is_ignored()
    {
        var loading = RunsReducers.On(Empty, new RunListRequested(RunListQuery.First));
        var closed = RunsReducers.On(loading, new RunListClosed());

        var after = RunsReducers.On(closed, new RunListLoaded(RunListQuery.First, new WorkflowRunList([], null)));

        after.List.Should().BeNull();
    }

    /// <summary>
    ///     A list failure still in flight when the list page closes leaves no error in the closed state.
    ///     Red: drop the ListRequested guard in the RunListFailed reducer.
    /// </summary>
    [Fact]
    public void A_list_failure_that_lands_after_the_page_closed_is_ignored()
    {
        var loading = RunsReducers.On(Empty, new RunListRequested(RunListQuery.First));
        var closed = RunsReducers.On(loading, new RunListClosed());

        var after = RunsReducers.On(closed, new RunListFailed(RunListQuery.First, "API error: 502 Bad Gateway"));

        after.ListError.Should().BeNull();
    }

    /// <summary>
    ///     A first-page request, which is what a filter change sends, empties the cursor stack; a later page keeps it.
    ///     Red: don't push the cursor on RunListPagedForward.
    ///     Red: keep PreviousCursors on a first-page request. Red: clear PreviousCursors on a request with a cursor.
    /// </summary>
    [Fact]
    public void A_first_page_request_clears_the_cursor_stack()
    {
        var paged = RunsReducers.On(Empty, new RunListPagedForward("c1"));
        paged.PreviousCursors.Should().HaveCount(1);

        RunsReducers.On(paged, new RunListRequested(new RunListQuery("Failed", null, null))).PreviousCursors.Should().BeEmpty();
        RunsReducers.On(paged, new RunListRequested(new RunListQuery(null, null, "c1"))).PreviousCursors.Should().HaveCount(1);
    }
}
