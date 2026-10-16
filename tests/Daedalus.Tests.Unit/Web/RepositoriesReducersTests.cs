using Daedalus.Application.DTOs;
using Daedalus.Web.Services;
using Daedalus.Web.State.Repositories;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The repositories reducers: what a read clears and keeps, the sequence guard and the close.</summary>
public sealed class RepositoriesReducersTests
{
    private static readonly RepositoryConfigurationDto Atlas = new() { Id = Guid.NewGuid(), Name = "atlas" };
    private static readonly RepositoryConfigurationDto Hermes = new() { Id = Guid.NewGuid(), Name = "hermes" };

    private static RepositoriesState Initial => RepositoriesState.Initial(null!);

    /// <summary>
    ///     A start clears what the last read showed, the list, the refusal and the error alike, as the page always did:
    ///     a read that then ends with the session being renewed shows nothing of its own.
    ///     Red: keep Repositories in the RepositoriesLoadStarted reducer; the old list is still there.
    ///     Red: keep Forbidden in the RepositoriesLoadStarted reducer; the refusal is still there.
    ///     Red: keep Error in the RepositoriesLoadStarted reducer; the old error is still there.
    ///     Red: leave Sequence unset in the RepositoriesLoadStarted reducer; the sequence stays 3.
    /// </summary>
    [Fact]
    public void A_start_clears_what_the_last_read_showed()
    {
        var listed = Initial with { Repositories = [Atlas], Sequence = 3 };
        var refused = Initial with { Forbidden = true, Sequence = 3 };
        var failed = Initial with { Error = "boom", Sequence = 3 };

        RepositoriesReducers.On(listed, new RepositoriesLoadStarted(4)).Repositories.Should().BeNull();
        RepositoriesReducers.On(refused, new RepositoriesLoadStarted(4)).Forbidden.Should().BeFalse();
        RepositoriesReducers.On(failed, new RepositoriesLoadStarted(4)).Error.Should().BeNull();
        RepositoriesReducers.On(failed, new RepositoriesLoadStarted(4)).Sequence.Should().Be(4);
    }

    /// <summary>
    ///     A start seen out of order never moves the sequence back.
    ///     Red: set Sequence to the action's in the RepositoriesLoadStarted reducer, not the larger of the two.
    /// </summary>
    [Fact]
    public void A_start_never_moves_the_sequence_back()
    {
        var state = RepositoriesReducers.On(Initial with { Sequence = 5 }, new RepositoriesLoadStarted(4));

        state.Sequence.Should().Be(5);
    }

    /// <summary>
    ///     The list lands as the repositories, and a refusal as the Forbidden state with no list.
    ///     Red: drop the list in the RepositoriesLoaded reducer; Repositories stays null.
    ///     Red: drop Forbidden in the RepositoriesLoaded reducer; the refusal is not the Forbidden state.
    /// </summary>
    [Fact]
    public void The_list_or_the_refusal_lands()
    {
        var started = RepositoriesReducers.On(Initial, new RepositoriesLoadStarted(1));

        var listed = RepositoriesReducers.On(started, new RepositoriesLoaded(1, RepositoryListing.Loaded([Atlas])));
        var refused = RepositoriesReducers.On(started, new RepositoriesLoaded(1, RepositoryListing.Denied));

        listed.Repositories.Should().Equal(Atlas);
        listed.Forbidden.Should().BeFalse();
        refused.Forbidden.Should().BeTrue();
        refused.Repositories.Should().BeNull();
    }

    /// <summary>
    ///     A failure is the error, with no list.
    ///     Red: drop the error in the RepositoriesLoadFailed reducer; Error stays null.
    /// </summary>
    [Fact]
    public void A_failure_is_the_error()
    {
        var started = RepositoriesReducers.On(Initial, new RepositoriesLoadStarted(1));

        var state = RepositoriesReducers.On(started, new RepositoriesLoadFailed(1, "The server answered 500."));

        state.Error.Should().Be("The server answered 500.");
        state.Repositories.Should().BeNull();
    }

    /// <summary>
    ///     An answer to a read older than the newest one started is dropped, a list or a failure alike, and the newest
    ///     one's own answer lands.
    ///     Red: drop the sequence check from the RepositoriesLoaded reducer; the older list lands.
    ///     Red: drop the sequence check from the RepositoriesLoadFailed reducer; the older failure is the error.
    ///     Red: compare with &lt;= instead of &lt; in the RepositoriesLoaded reducer; the newest list is dropped too.
    /// </summary>
    [Fact]
    public void An_answer_to_an_older_read_is_dropped()
    {
        var state = RepositoriesReducers.On(Initial, new RepositoriesLoadStarted(1));
        state = RepositoriesReducers.On(state, new RepositoriesLoadStarted(2));

        state = RepositoriesReducers.On(state, new RepositoriesLoaded(1, RepositoryListing.Loaded([Atlas])));
        state = RepositoriesReducers.On(state, new RepositoriesLoadFailed(1, "late"));

        state.Repositories.Should().BeNull();
        state.Error.Should().BeNull();

        state = RepositoriesReducers.On(state, new RepositoriesLoaded(2, RepositoryListing.Loaded([Hermes])));
        state.Repositories.Should().Equal(Hermes);
    }

    /// <summary>
    ///     Closing the page clears its list, its refusal and its error, and keeps the sequence, so an
    ///     answer to a read made before the close never lands after it.
    ///     Red: Closed returns s; the list is still there.
    ///     Red: Closed resets to Initial with Sequence 0; the sequence is gone, and the late answer lands.
    /// </summary>
    [Fact]
    public void Closing_clears_the_page_and_keeps_the_sequence()
    {
        var open = Initial with
        {
            Repositories = [Atlas],
            Forbidden = true,
            Error = "boom",
            Sequence = 7,
        };

        var closed = RepositoriesReducers.On(open, new RepositoriesClosed());

        closed.Should().Be(Initial with { Sequence = 7 });
        RepositoriesReducers.On(closed, new RepositoriesLoaded(6, RepositoryListing.Loaded([Hermes]))).Repositories
            .Should().BeNull("a read made before the close does not land after it");
    }
}
