using Daedalus.Application.DTOs;
using Daedalus.Web.State.Prd;
using Complexity = Daedalus.Domain.Entities.Complexity;
using Priority = Daedalus.Domain.Entities.Priority;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The PRD reducers: the wizard's steps, the generated PRD, the created tasks, the error, the sequence guard and the clears.</summary>
public sealed class PrdReducersTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static readonly PrdResponseDto Prd = new(
        "prd-1", ProjectId, "A widget shop.", ["Sell widgets"], [],
        [new PrdItemDto("Cart", "A cart", "Holds widgets", Priority.High, Complexity.Medium)], [], DateTime.UnixEpoch);

    private static readonly TaskDto Task = new(
        Guid.NewGuid(), "TASK-1", ProjectId, "Cart", "A cart", 2, "Implementation", 1, [], [], 2, "p", "DONE", 10, 0, null,
        null, 0, DateTime.UnixEpoch, null, null, null, [], null, null, null, null);

    private static PrdState Initial => PrdState.Initial(null!);

    private static PrdState Requirements => PrdReducers.On(Initial, new PrdProjectSelected(ProjectId));

    private static PrdState Asked(long sequence = 1) => PrdReducers.On(Requirements, new PrdRequestStarted(sequence));

    /// <summary>
    ///     The wizard starts on project selection with nothing held.
    ///     Red: start PrdState.Empty on RequirementsInput; the step is not ProjectSelection.
    /// </summary>
    [Fact]
    public void The_wizard_starts_on_project_selection()
    {
        Initial.Step.Should().Be(PrdStep.ProjectSelection);
    }

    /// <summary>
    ///     Picking a project moves the wizard to the requirements, for that project.
    ///     Red: drop Step in the PrdProjectSelected reducer; the wizard stays on project selection.
    ///     Red: drop ProjectId in the PrdProjectSelected reducer; the project stays empty.
    /// </summary>
    [Fact]
    public void Picking_a_project_asks_for_the_requirements()
    {
        Requirements.Step.Should().Be(PrdStep.RequirementsInput);
        Requirements.ProjectId.Should().Be(ProjectId);
    }

    /// <summary>
    ///     A request start clears the last failure and takes its number; a start seen out of order never moves it back.
    ///     Red: keep Error in the PrdRequestStarted reducer; the old failure is still there.
    ///     Red: leave Sequence unset in the PrdRequestStarted reducer; it stays 0.
    ///     Red: set Sequence to the action's in the PrdRequestStarted reducer; an out-of-order start moves it back to 2.
    /// </summary>
    [Fact]
    public void A_request_start_clears_the_error_and_takes_its_number()
    {
        var failed = Requirements with { Error = "boom" };

        var started = PrdReducers.On(failed, new PrdRequestStarted(4));

        started.Error.Should().BeNull();
        started.Sequence.Should().Be(4);
        PrdReducers.On(started, new PrdRequestStarted(2)).Sequence.Should().Be(4);
    }

    /// <summary>
    ///     A generated PRD shows for review.
    ///     Red: drop Prd in the PrdGenerated reducer; the PRD stays null.
    ///     Red: drop Step in the PrdGenerated reducer; the wizard stays on the requirements.
    /// </summary>
    [Fact]
    public void A_generated_prd_shows_for_review()
    {
        var generated = PrdReducers.On(Asked(), new PrdGenerated(1, Prd));

        generated.Prd.Should().Be(Prd);
        generated.Step.Should().Be(PrdStep.PrdReview);
    }

    /// <summary>
    ///     The created tasks show on the last step.
    ///     Red: drop CreatedTasks in the PrdConverted reducer; the tasks stay empty.
    ///     Red: drop Step in the PrdConverted reducer; the wizard stays where it was.
    /// </summary>
    [Fact]
    public void The_created_tasks_show_on_the_last_step()
    {
        var converted = PrdReducers.On(Asked(), new PrdConverted(1, [Task]));

        converted.CreatedTasks.Should().Equal(Task);
        converted.Step.Should().Be(PrdStep.TasksCreated);
    }

    /// <summary>
    ///     A failed request is the error, and the wizard stays on its step.
    ///     Red: drop Error in the PrdRequestFailed reducer; the error stays null.
    ///     Red: move Step to PrdReview in the PrdRequestFailed reducer; the wizard leaves the requirements.
    /// </summary>
    [Fact]
    public void A_failed_request_is_the_error_on_the_same_step()
    {
        var failed = PrdReducers.On(Asked(), new PrdRequestFailed(1, "The model could not be reached."));

        failed.Error.Should().Be("The model could not be reached.");
        failed.Step.Should().Be(PrdStep.RequirementsInput);
    }

    /// <summary>
    ///     An answer to a request that is not the newest changes nothing.
    ///     Red: drop the sequence check in the PrdGenerated reducer; the older PRD lands.
    ///     Red: drop the sequence check in the PrdConverted reducer; the older tasks land.
    ///     Red: drop the sequence check in the PrdRequestFailed reducer; the older failure lands.
    /// </summary>
    [Fact]
    public void An_answer_to_an_older_request_changes_nothing()
    {
        var newest = Asked(2);

        PrdReducers.On(newest, new PrdGenerated(1, Prd)).Should().Be(newest);
        PrdReducers.On(newest, new PrdConverted(1, [Task])).Should().Be(newest);
        PrdReducers.On(newest, new PrdRequestFailed(1, "boom")).Should().Be(newest);
    }

    /// <summary>
    ///     Closing the alert clears the failure.
    ///     Red: return the state unchanged in the PrdErrorDismissed reducer; the failure stays.
    /// </summary>
    [Fact]
    public void Closing_the_alert_clears_the_error()
    {
        var failed = Asked() with { Error = "boom" };

        PrdReducers.On(failed, new PrdErrorDismissed()).Error.Should().BeNull();
    }

    /// <summary>
    ///     Starting over, and closing the page, clear everything the wizard held and keep the sequence.
    ///     Red: return the state unchanged in the PrdReset reducer; the PRD is still held.
    ///     Red: return PrdState.Empty in the PrdReset reducer; the sequence goes back to 0.
    ///     Red: return the state unchanged in the PrdClosed reducer; the PRD is still held.
    ///     Red: return PrdState.Empty in the PrdClosed reducer; the sequence goes back to 0.
    /// </summary>
    [Fact]
    public void Starting_over_and_closing_clear_the_wizard_and_keep_the_sequence()
    {
        var held = PrdReducers.On(Asked(3), new PrdGenerated(3, Prd)) with { Error = "boom" };

        PrdReducers.On(held, new PrdReset()).Should().Be(PrdState.Empty with { Sequence = 3 });
        PrdReducers.On(held, new PrdClosed()).Should().Be(PrdState.Empty with { Sequence = 3 });
    }
}
