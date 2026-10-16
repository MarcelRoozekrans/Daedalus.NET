using Daedalus.Application.DTOs;
using Daedalus.Web.State.Brainstorm;
using BrainstormPhase = Daedalus.Domain.Entities.BrainstormPhase;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The Brainstorm reducers: the session, the sequence guard and the close.</summary>
public sealed class BrainstormReducersTests
{
    private static readonly BrainstormSessionDto Session = new(
        Guid.NewGuid(), Guid.NewGuid(), BrainstormPhase.ContextGathering, [], null, null, true, DateTime.UnixEpoch, null);

    private static BrainstormState Initial => BrainstormState.Initial(null!);

    /// <summary>
    ///     A read start takes its number and keeps the session shown, so the messages stay while the page reloads them; a
    ///     start seen out of order never moves the number back.
    ///     Red: clear Session in the BrainstormReadStarted reducer; the shown session goes.
    ///     Red: leave Sequence unset in the BrainstormReadStarted reducer; it stays 3.
    ///     Red: set Sequence to the action's in the BrainstormReadStarted reducer; an out-of-order start moves it back to 2.
    /// </summary>
    [Fact]
    public void A_read_start_takes_its_number_and_keeps_the_session()
    {
        var shown = Initial with { Session = Session, Sequence = 3 };

        var started = BrainstormReducers.On(shown, new BrainstormReadStarted(4));

        started.Session.Should().Be(Session);
        started.Sequence.Should().Be(4);
        BrainstormReducers.On(started, new BrainstormReadStarted(2)).Sequence.Should().Be(4);
    }

    /// <summary>
    ///     The newest read's session lands; an older read's answer changes nothing.
    ///     Red: drop Session in the BrainstormLoaded reducer; the session stays null.
    ///     Red: drop the sequence check in the BrainstormLoaded reducer; the older session lands.
    /// </summary>
    [Fact]
    public void The_newest_reads_session_lands()
    {
        var started = BrainstormReducers.On(Initial, new BrainstormReadStarted(2));
        var older = Session with { Phase = BrainstormPhase.Clarification };

        var loaded = BrainstormReducers.On(started, new BrainstormLoaded(2, Session));

        loaded.Session.Should().Be(Session);
        BrainstormReducers.On(loaded, new BrainstormLoaded(1, older)).Session.Should().Be(Session);
    }

    /// <summary>
    ///     Closing the page clears the session and keeps the sequence.
    ///     Red: return the state unchanged in the BrainstormClosed reducer; the session is still held.
    ///     Red: return BrainstormState.Empty in the BrainstormClosed reducer; the sequence goes back to 0.
    /// </summary>
    [Fact]
    public void The_close_clears_the_session_and_keeps_the_sequence()
    {
        var held = Initial with { Session = Session, Sequence = 5 };

        BrainstormReducers.On(held, new BrainstormClosed()).Should().Be(BrainstormState.Empty with { Sequence = 5 });
    }
}
