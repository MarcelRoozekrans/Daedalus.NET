using Daedalus.Application.DTOs;
using Daedalus.Web.State.Projects;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The projects reducers: the sequence guard that keeps a late answer out, and what a failure keeps.</summary>
public sealed class ProjectsReducersTests
{
    private static readonly ProjectDto First = Project("First");
    private static readonly ProjectDto Second = Project("Second");

    /// <summary>
    ///     A start marks loading and a load asked for, and clears the last error: the state is shared, so a failure from
    ///     an earlier reader must not show under another reader's picker while that reader re-fetches.
    ///     Red: leave Loading unset in the ProjectsLoadStarted reducer.
    ///     Red: leave LoadRequested unset in the ProjectsLoadStarted reducer.
    ///     Red: keep Error in the ProjectsLoadStarted reducer; the earlier failure is still the error.
    /// </summary>
    [Fact]
    public void A_start_marks_loading_and_clears_the_last_error()
    {
        var state = ProjectsReducers.On(ProjectsState.Initial(null!), new ProjectsLoadStarted(1));
        state = ProjectsReducers.On(state, new ProjectsLoadFailed(1, "boom"));
        state.Error.Should().Be("boom");

        state = ProjectsReducers.On(state, new ProjectsLoadStarted(2));

        state.Loading.Should().BeTrue();
        state.LoadRequested.Should().BeTrue();
        state.Error.Should().BeNull("an earlier reader's failure is not this load's");
    }

    /// <summary>
    ///     An answer to a load older than the newest one started is dropped, a list or a failure alike, and the newest
    ///     one's own answer lands.
    ///     Red: drop the sequence check from the ProjectsLoaded reducer; the older list lands.
    ///     Red: drop the sequence check from the ProjectsLoadFailed reducer; the older failure is the error.
    ///     Red: compare with &lt;= instead of &lt; in the ProjectsLoaded reducer; the newest list is dropped too.
    /// </summary>
    [Fact]
    public void An_answer_to_an_older_load_is_dropped()
    {
        var state = ProjectsReducers.On(ProjectsState.Initial(null!), new ProjectsLoadStarted(1));
        state = ProjectsReducers.On(state, new ProjectsLoadStarted(2));

        state = ProjectsReducers.On(state, new ProjectsLoaded(1, [First]));
        state = ProjectsReducers.On(state, new ProjectsLoadFailed(1, "late"));

        state.Projects.Should().BeNull();
        state.Error.Should().BeNull();
        state.Loading.Should().BeTrue("the newest load is still in flight");

        state = ProjectsReducers.On(state, new ProjectsLoaded(2, [Second]));
        state.Projects.Should().Equal(Second);
    }

    /// <summary>
    ///     A start older than one already seen does not move the sequence back, so the newer load's answer still lands
    ///     and the older one's is still dropped.
    ///     Red: set Sequence to the action's in the ProjectsLoadStarted reducer, not the larger of the two; the older
    ///     list lands.
    /// </summary>
    [Fact]
    public void A_start_seen_out_of_order_does_not_move_the_sequence_back()
    {
        var state = ProjectsReducers.On(ProjectsState.Initial(null!), new ProjectsLoadStarted(2));
        state = ProjectsReducers.On(state, new ProjectsLoadStarted(1));

        state = ProjectsReducers.On(state, new ProjectsLoaded(1, [First]));

        state.Projects.Should().BeNull();
        state.Sequence.Should().Be(2);
    }

    /// <summary>
    ///     A failure keeps the projects already loaded, so a failed refresh does not empty the picker.
    ///     Red: set Projects to null in the ProjectsLoadFailed reducer.
    /// </summary>
    [Fact]
    public void A_failure_keeps_the_projects()
    {
        var state = ProjectsReducers.On(ProjectsState.Initial(null!), new ProjectsLoadStarted(1));
        state = ProjectsReducers.On(state, new ProjectsLoaded(1, [First]));
        state = ProjectsReducers.On(state, new ProjectsLoadStarted(2));

        state = ProjectsReducers.On(state, new ProjectsLoadFailed(2, "boom"));

        state.Projects.Should().Equal(First);
        state.Error.Should().Be("boom");
    }

    /// <summary>
    ///     An abandoned load ends loading and stores nothing, and an abandoned older load leaves the newer one loading.
    ///     Red: leave Loading set in the ProjectsLoadAbandoned reducer.
    ///     Red: drop the sequence check from the ProjectsLoadAbandoned reducer; the newer load reads as done.
    ///     Red: clear Projects in the ProjectsLoadAbandoned reducer.
    /// </summary>
    [Fact]
    public void An_abandoned_load_ends_loading_and_stores_nothing()
    {
        var state = ProjectsReducers.On(ProjectsState.Initial(null!), new ProjectsLoadStarted(1));
        state = ProjectsReducers.On(state, new ProjectsLoaded(1, [First]));
        state = ProjectsReducers.On(state, new ProjectsLoadStarted(2));
        state = ProjectsReducers.On(state, new ProjectsLoadStarted(3));

        state = ProjectsReducers.On(state, new ProjectsLoadAbandoned(2));
        state.Loading.Should().BeTrue("load 3 is still in flight");

        state = ProjectsReducers.On(state, new ProjectsLoadAbandoned(3));
        state.Loading.Should().BeFalse();
        state.Error.Should().BeNull();
        state.Projects.Should().Equal(First);
    }

    private static ProjectDto Project(string name) => new(Guid.NewGuid(), name, "", "1.0", "", "main", DateTime.UnixEpoch, null, []);
}
