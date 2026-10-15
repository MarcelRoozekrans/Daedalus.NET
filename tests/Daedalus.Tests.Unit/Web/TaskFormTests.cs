using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The create and edit dialogs' form: what it sends for a new task and for an edit.</summary>
public sealed class TaskFormTests
{
    /// <summary>
    ///     A new task sends its project, title and description, and nothing of the retired loop: no phase, parallel group,
    ///     dependencies or files. A blank prompt is sent as null, so the API uses the description.
    ///     Red: send the blank prompt as is; the prompt is "  ". Red: send ParallelGroup 0; it is not null.
    ///     Red: send an empty phase; it is not null.
    /// </summary>
    [Fact]
    public void A_new_task_sends_no_prompt_and_no_loop_fields()
    {
        var project = Guid.NewGuid();
        var form = new TaskForm { ProjectId = project, Title = " Title ", Description = " Description ", Prompt = "  " };

        var dto = form.ToCreate();

        dto.Should().BeEquivalentTo(new CreateTaskDto(project, null, "Title", "Description", 1, null, null, 1, null, null, null));
    }

    /// <summary>Red: send null as the prompt always; a written prompt is lost.</summary>
    [Fact]
    public void A_written_prompt_is_sent() =>
        new TaskForm { ProjectId = Guid.NewGuid(), Title = "t", Description = "d", Prompt = "Do it" }.ToCreate().Prompt.Should().Be("Do it");

    /// <summary>
    ///     With exactly one project it is preselected; with several, none is.
    ///     Red: preselect only when there are none; the single project is not chosen.
    ///     Red: preselect the first of several; the second case has a project.
    ///     Red: preselect at a count of one or less; the empty list throws.
    /// </summary>
    [Fact]
    public void Only_a_single_project_is_preselected()
    {
        var only = Project();
        TaskForm.Preselect([only]).Should().Be(only.Id);
        TaskForm.Preselect([Project(), Project()]).Should().BeNull();
        TaskForm.Preselect([]).Should().BeNull();
    }

    /// <summary>
    ///     An edit leaves the phase and parallel group alone, sends only a changed priority, and sends the description for a
    ///     blank prompt, as a create does.
    ///     Red: send the task's phase; it is not null. Red: send an unchanged priority; it is not null.
    ///     Red: send null for a blank prompt; the description is not the prompt.
    /// </summary>
    [Fact]
    public void An_edit_keeps_the_loop_fields_and_uses_the_description_for_a_blank_prompt()
    {
        var task = new TaskDto(Guid.NewGuid(), "T-1", Guid.NewGuid(), "t", "d", 1, "build", 2, [], [], 1, "old", "", 0, 0,
            null, null, 0, DateTime.UnixEpoch, null, null, null, [], null, null);
        var form = TaskForm.From(task);
        form.Description = "new description";
        form.Prompt = "";
        form.EstimatedComplexity = 3;

        var dto = form.ToUpdate(task);

        dto.Should().BeEquivalentTo(new UpdateTaskDto("t", "new description", null, null, null, 3, "new description"));
    }

    private static ProjectDto Project() => new(Guid.NewGuid(), "p", "", "1.0", null, DateTime.UnixEpoch, null, []);
}
