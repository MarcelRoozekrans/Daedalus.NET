using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <c>Rehydrate</c> on the task and its execution records.</summary>
public sealed class TaskRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    /// <summary>
    ///     A stored task loads as it is, even one <see cref="DomainTask.Create"/> would refuse today: a task id over 50
    ///     characters, a blank title, description, phase and prompt, and a parallel group below 1; with every other value,
    ///     its two lists, its row version and its executions kept. Red: validate in <c>Rehydrate</c>, or drop any one
    ///     assignment.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        var longTaskId = new string('T', 60);
        DomainTask.Create(Guid.NewGuid(), Guid.NewGuid(), longTaskId, " ", " ", Priority.High, " ", 0, Complexity.Low, " ")
            .IsFailure.Should().BeTrue("the values below are ones Create refuses");
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var execution = TaskExecution.Rehydrate(
            Guid.NewGuid(), id, sessionId, 2, "p", "r", true, _at, TimeSpan.FromSeconds(3), "e", 4, "m", 5);

        var task = DomainTask.Rehydrate(
            id, longTaskId, projectId, " ", " ", Priority.High, DomainTaskStatus.Failed, " ", 0, Complexity.Low, " ",
            "promise", 7, sessionId, "result", 8, _at, _at.AddHours(1), "learned", _at.AddHours(2), ["A", "B"],
            ["x.cs"], runId, 42u, [execution]);

        task.Id.Should().Be(id);
        task.TaskId.Should().Be(longTaskId);
        task.ProjectId.Should().Be(projectId);
        task.Title.Should().Be(" ");
        task.Description.Should().Be(" ");
        task.Priority.Should().Be(Priority.High);
        task.Status.Should().Be(DomainTaskStatus.Failed);
        task.Phase.Should().Be(" ");
        task.ParallelGroup.Should().Be(0);
        task.EstimatedComplexity.Should().Be(Complexity.Low);
        task.Prompt.Should().Be(" ");
        task.CompletionPromise.Should().Be("promise");
        task.MaxIterations.Should().Be(7);
        task.CurrentSessionId.Should().Be(sessionId);
        task.Result.Should().Be("result");
        task.IterationCount.Should().Be(8);
        task.CreatedAt.Should().Be(_at);
        task.CompletedAt.Should().Be(_at.AddHours(1));
        task.Learnings.Should().Be("learned");
        task.LearningsUpdatedAt.Should().Be(_at.AddHours(2));
        task.Dependencies.Should().Equal("A", "B");
        task.FilesToModify.Should().Equal("x.cs");
        task.WorkflowRunId.Should().Be(runId);
        task.RowVersion.Should().Be(42u);
        task.Executions.Should().ContainSingle().Which.Should().BeSameAs(execution);
    }

    /// <summary>Every value of a stored execution record is kept as given. Red: drop any one assignment.</summary>
    [Fact]
    public void Execution_Rehydrate_keeps_every_value()
    {
        var id = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var execution = TaskExecution.Rehydrate(
            id, taskId, sessionId, -1, "", "response", true, _at, TimeSpan.FromMilliseconds(1500), "boom", -2, "model", -3);

        execution.Id.Should().Be(id);
        execution.TaskId.Should().Be(taskId);
        execution.SessionId.Should().Be(sessionId);
        execution.IterationNumber.Should().Be(-1);
        execution.Prompt.Should().BeEmpty();
        execution.LlmResponse.Should().Be("response");
        execution.CompletionPromiseFound.Should().BeTrue();
        execution.ExecutedAt.Should().Be(_at);
        execution.ExecutionDuration.Should().Be(TimeSpan.FromMilliseconds(1500));
        execution.Error.Should().Be("boom");
        execution.InputTokens.Should().Be(-2);
        execution.ModelId.Should().Be("model");
        execution.OutputTokens.Should().Be(-3);
    }
}
