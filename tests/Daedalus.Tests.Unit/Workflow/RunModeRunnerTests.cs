using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Ruling R61: <see cref="RunModeRunner"/> states the run's mode, from its <c>run_mode</c> variable, in the task text
///     of the implement and review nodes, and states nothing it would have to guess.
/// </summary>
public sealed class RunModeRunnerTests
{
    private static WorkflowRun RunAt(string node, string skillName, object? mode)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.WorkIntentKey] = "Do the thing" };
        if (mode is not null)
        {
            variables[RunMode.Key] = mode;
        }

        return new WorkflowRun
        {
            Id = Guid.NewGuid(),
            Process = "manufacture",
            ProcessVersion = 8,
            CurrentNode = node,
            CurrentSeq = 1,
            Status = WorkflowStatus.Running,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { [node] = 1 },
            Variables = variables,
            Manifest = new RunManifest
            {
                Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal)
                {
                    [node] = new NodePin("Agent", AgentId.New(), null, skillName, "hash"),
                },
                Documents = new Dictionary<string, string>(StringComparer.Ordinal),
            },
        };
    }

    private static async Task<string> TaskSentAsync(WorkflowRun run)
    {
        var inner = new CapturingRunner();
        await new RunModeRunner(inner).RunAsync(
            new SubagentRunRequest { AgentId = AgentId.New(), Task = "the engine's task", Caller = new WorkflowCaller(run, grant: null) },
            CancellationToken.None);

        inner.Captured.Should().NotBeNull("the request must be forwarded to the inner runner");
        return inner.Captured!.Task;
    }

    /// <summary>
    ///     Both nodes, in both modes, are told the mode on a line of its own, after the engine's task. Red, applied once:
    ///     drop <see cref="ReviewHandoff.ReviewSkillName"/> from the runner's eligible skills, and the review rows fail;
    ///     write a constant instead of the variable's value, and the rows of the other mode fail.
    /// </summary>
    [Theory]
    [InlineData("implement", ReviewHandoff.ImplementSkillName, "sandbox")]
    [InlineData("implement", ReviewHandoff.ImplementSkillName, "local")]
    [InlineData("review", ReviewHandoff.ReviewSkillName, "sandbox")]
    [InlineData("review", ReviewHandoff.ReviewSkillName, "local")]
    public async Task The_implement_and_review_nodes_are_told_the_run_mode(string node, string skill, string mode)
    {
        var task = await TaskSentAsync(RunAt(node, skill, mode));

        task.Should().StartWith("the engine's task");
        task.Split('\n').Should().Contain($"Run mode: {mode}");
        task.Should().Contain(RunModeRunner.Heading);
    }

    /// <summary>
    ///     Retrospect is not a mode-dependent node, so it is left alone. Red, applied once: make every pinned node
    ///     eligible, and this fails.
    /// </summary>
    [Fact]
    public async Task Retrospect_is_not_told_a_mode()
    {
        (await TaskSentAsync(RunAt("retrospect", ReviewHandoff.RetrospectSkillName, RunMode.Sandbox))).Should().Be("the engine's task");
    }

    /// <summary>
    ///     A run with no mode, or a value that is not one of the two, is told nothing rather than a guess. Red, applied
    ///     once: drop the known-mode check, and the unknown row states "Run mode: docker"; default an absent mode to
    ///     local, and the absent row states it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("docker")]
    [InlineData(42)]
    public async Task A_missing_or_unknown_mode_states_nothing(object? mode)
    {
        (await TaskSentAsync(RunAt("implement", ReviewHandoff.ImplementSkillName, mode))).Should().Be("the engine's task");
    }

    private sealed class CapturingRunner : ISubagentRunner
    {
        public SubagentRunRequest? Captured { get; private set; }

        public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
        {
            Captured = request;
            return new ValueTask<Result<AgentTurnResult, AgentError>>(Result<AgentTurnResult, AgentError>.Failure(AgentError.Validation("captured")));
        }
    }
}
