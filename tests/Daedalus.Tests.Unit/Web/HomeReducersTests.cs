using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.State.Home;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The Home reducers: what a read shows while in flight, the guard that keeps a stale answer out, and the close.</summary>
public sealed class HomeReducersTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<WorkflowRunListItem> One = [RunViews.Item(Guid.NewGuid())];

    private static readonly IReadOnlyList<WorkflowRunListItem> Two = [RunViews.Item(Guid.NewGuid()), RunViews.Item(Guid.NewGuid())];

    /// <summary>Home open, with the running card loaded with <see cref="One"/> by read 1.</summary>
    private static HomeState Loaded()
    {
        var state = HomeReducers.On(HomeState.Initial(null!), new HomeOpened());
        state = HomeReducers.On(state, new HomeCardLoadStarted(HomeCardKind.Running, 1, Background: false));
        return HomeReducers.On(state, new HomeCardLoaded(HomeCardKind.Running, 1, One, More: true, ReadAt));
    }

    /// <summary>
    ///     An answer lands on its own card with its rows, its More and the time it was read, and leaves the others alone.
    ///     Red: HomeCardLoaded returns the state unchanged; the running card has no rows.
    ///     Red: HomeCardLoaded drops More; the count reads as exact.
    ///     Red: HomeCardLoaded leaves ReadAt; the wait is measured from the start of time.
    ///     Red: HomeState.With writes the running card into Awaiting; the running card has no rows.
    /// </summary>
    [Fact]
    public void An_answer_lands_on_its_own_card()
    {
        var state = Loaded();

        state.Running.Rows.Should().BeSameAs(One);
        state.Running.More.Should().BeTrue();
        state.ReadAt.Should().Be(ReadAt);
        state.Awaiting.Rows.Should().BeNull("only the running card was read");
    }

    /// <summary>
    ///     A read that is not a background one shows the card's skeleton: its rows, error and More go until it lands.
    ///     Red: HomeCardLoadStarted keeps the rows for every read; Retry shows the old rows, not the skeleton.
    ///     Red: HomeCardLoadStarted keeps More; a fresh read's heading carries the old "+".
    /// </summary>
    [Fact]
    public void A_foreground_read_shows_the_skeleton()
    {
        var state = HomeReducers.On(Loaded(), new HomeCardLoadStarted(HomeCardKind.Running, 2, Background: false));

        state.Running.Rows.Should().BeNull();
        state.Running.More.Should().BeFalse();
    }

    /// <summary>
    ///     A background read, a run change's or a reconnect's, keeps the rows on show, so the card never turns into its
    ///     skeleton; its answer then replaces them.
    ///     Red: HomeCardLoadStarted clears the rows for a background read too; the rows are null in flight.
    ///     Red: HomeCardLoadStarted leaves the card's Sequence; the background answer is dropped as stale.
    /// </summary>
    [Fact]
    public void A_background_read_keeps_the_rows_until_its_answer_lands()
    {
        var state = HomeReducers.On(Loaded(), new HomeCardLoadStarted(HomeCardKind.Running, 2, Background: true));
        state.Running.Rows.Should().BeSameAs(One, "a background read never shows the skeleton");

        state = HomeReducers.On(state, new HomeCardLoaded(HomeCardKind.Running, 2, Two, More: false, ReadAt));

        state.Running.Rows.Should().BeSameAs(Two);
    }

    /// <summary>
    ///     A failure is the card's error and clears its rows, so the heading carries no count beside the alert, as the
    ///     card always showed a failure.
    ///     Red: HomeCardLoadFailed returns the state unchanged; there is no error.
    ///     Red: HomeCardLoadFailed keeps the rows; the card still counts them beside its alert.
    /// </summary>
    [Fact]
    public void A_failure_is_the_cards_error_and_clears_its_rows()
    {
        var state = HomeReducers.On(Loaded(), new HomeCardLoadStarted(HomeCardKind.Running, 2, Background: true));

        state = HomeReducers.On(state, new HomeCardLoadFailed(HomeCardKind.Running, 2, "The run store is down."));

        state.Running.Error.Should().Be("The run store is down.");
        state.Running.Rows.Should().BeNull();
        state.Running.Loaded.Should().BeFalse("a card in error is not re-fetched by a change");
    }

    /// <summary>
    ///     An answer to a read older than the card's newest is dropped, success or failure alike.
    ///     Red: drop the sequence check from HomeCardLoaded; the older rows win.
    ///     Red: drop the sequence check from HomeCardLoadFailed; the older failure replaces the newer rows.
    /// </summary>
    [Fact]
    public void An_older_answer_is_dropped()
    {
        var state = HomeReducers.On(Loaded(), new HomeCardLoadStarted(HomeCardKind.Running, 2, Background: true));
        state = HomeReducers.On(state, new HomeCardLoadStarted(HomeCardKind.Running, 3, Background: true));
        state = HomeReducers.On(state, new HomeCardLoaded(HomeCardKind.Running, 3, Two, More: false, ReadAt));

        state = HomeReducers.On(state, new HomeCardLoaded(HomeCardKind.Running, 2, One, More: true, ReadAt));
        state.Running.Rows.Should().BeSameAs(Two);

        state = HomeReducers.On(state, new HomeCardLoadFailed(HomeCardKind.Running, 2, "late"));
        state.Running.Error.Should().BeNull();
        state.Running.Rows.Should().BeSameAs(Two);
    }

    /// <summary>
    ///     Closing clears the cards and Open, but keeps the sequence, so a read made before the close never matches one
    ///     made after Home opens again.
    ///     Red: HomeClosed returns the state unchanged; the rows stay.
    ///     Red: HomeClosed resets the sequence to 0; the next read after reopening reuses number 1.
    /// </summary>
    [Fact]
    public void Closing_clears_the_cards_and_keeps_the_sequence()
    {
        var state = HomeReducers.On(Loaded(), new HomeClosed());

        state.Open.Should().BeFalse();
        state.Running.Rows.Should().BeNull();
        state.Sequence.Should().Be(1);
    }

    /// <summary>
    ///     An answer that lands after the close is not kept, whether its read began before the close or after it; every
    ///     start still takes a number, so Home reopened later never hands that number out again.
    ///     Red: HomeCardLoadStarted never moves the state's Sequence; it is not 3 after the third start.
    ///     Red: drop the Open check from Awaited; the answer to the read started after the close fills the closed card.
    /// </summary>
    [Fact]
    public void A_read_after_the_close_is_not_kept()
    {
        var state = HomeReducers.On(Loaded(), new HomeCardLoadStarted(HomeCardKind.Running, 2, Background: true));
        state = HomeReducers.On(state, new HomeClosed());
        state = HomeReducers.On(state, new HomeCardLoaded(HomeCardKind.Running, 2, Two, More: false, ReadAt));
        state.Running.Rows.Should().BeNull("Home closed before the answer landed");

        state = HomeReducers.On(state, new HomeCardLoadStarted(HomeCardKind.Running, 3, Background: true));
        state.Sequence.Should().Be(3, "a start takes its number even while closed");
        state.Running.Sequence.Should().Be(3);
        state = HomeReducers.On(state, new HomeCardLoaded(HomeCardKind.Running, 3, Two, More: false, ReadAt));
        state.Running.Rows.Should().BeNull("a read started while closed is not kept");
    }

    /// <summary>
    ///     Opening marks Home open, so a background read is kept.
    ///     Red: HomeOpened returns the state unchanged; Open stays false.
    /// </summary>
    [Fact]
    public void Opening_marks_home_open() =>
        HomeReducers.On(HomeState.Initial(null!), new HomeOpened()).Open.Should().BeTrue();
}
