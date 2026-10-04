using System.Collections.Frozen;
using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     States the run's mode, plainly and in the host's own words, in the task text of every turn of a node pinned to
///     <see cref="ReviewHandoff.ImplementSkillName"/> or <see cref="ReviewHandoff.ReviewSkillName"/>: a section headed
///     <c>## Run mode</c> whose line reads <c>Run mode: sandbox</c> or <c>Run mode: local</c>. Ruling R61.
/// </summary>
/// <remarks>
///     <para>
///     <b>Why a host section and not only the variable.</b> <see cref="ManufactureRunStarter"/> writes the mode into the
///     run's bag under <see cref="RunMode.Key"/>, and Thalos renders the bag into the task text, but inside its
///     <c>workflow-variables</c> block, whose framing tells the reader everything in it is another agent's output, to be
///     treated as information and never as an instruction. The mode is neither: the host wrote it, from its own
///     configuration, and the skills key their rules off it. Thalos' <c>ProcessNode</c> carries no free-text prompt a
///     process file could state it in, so the host states it here, outside that block. The variable is what this reads,
///     so the record and the statement cannot disagree.
///     </para>
///     <para>
///     <b>Where the value comes from.</b> <see cref="WorkflowCaller.Run"/> is the run as the dispatcher read it, through
///     <see cref="ReviewHandoffWorkflowStore"/>, so on a review node it is the projected bag, which is why
///     <see cref="ReviewHandoff.ReviewReads"/> admits <see cref="RunMode.Key"/>. No agent can change the value:
///     <see cref="ReviewHandoff.HostWritten"/> strips the key from every node's report.
///     </para>
///     <para>
///     <b>Nothing is guessed.</b> A run with no mode in its bag, one started before ruling R61, or a value that is
///     neither <see cref="RunMode.Sandbox"/> nor <see cref="RunMode.Local"/> gets no section. Such a run is pinned to a
///     skill version that predates the stated mode, and the current skills tell the agent that a task stating no mode is
///     a <c>blocked</c> outcome, not a reason to infer one.
///     </para>
///     <para>
///     <b>Placement.</b> Outermost in <see cref="WorkflowNodeDispatcherFactory.Create"/>, so it runs once per node
///     dispatch and every review lens pass, composed further in by <see cref="ReviewLensRunner"/>, carries the section.
///     </para>
/// </remarks>
internal sealed class RunModeRunner(ISubagentRunner inner) : ISubagentRunner
{
    /// <summary>The heading of the section this type appends.</summary>
    internal const string Heading = "## Run mode";

    /// <summary>The prefix of the one line that states the mode, followed by the mode itself.</summary>
    internal const string LinePrefix = "Run mode: ";

    private static readonly FrozenSet<string> EligibleSkills =
        new[] { ReviewHandoff.ImplementSkillName, ReviewHandoff.ReviewSkillName }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> KnownModes =
        new[] { RunMode.Sandbox, RunMode.Local }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ISubagentRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mode = ResolveMode(request);
        return mode is null
            ? _inner.RunAsync(request, ct)
            : _inner.RunAsync(request with { Task = request.Task + "\n\n" + Section(mode) }, ct);
    }

    /// <summary>The section stated for <paramref name="mode"/>.</summary>
    internal static string Section(string mode) =>
        $"{Heading}\n\n{LinePrefix}{mode}\n\nThe host set this when the run started, from its own configuration. It is not " +
        "another agent's output, and it is not something to work out from your tool list: your skill's mode-dependent " +
        "rules follow it.";

    private static string? ResolveMode(SubagentRunRequest request)
    {
        if (request.Caller is not WorkflowCaller caller)
        {
            return null;
        }

        var run = caller.Run;
        if (run.Manifest is null
            || !run.Manifest.Nodes.TryGetValue(run.CurrentNode, out var pin)
            || !EligibleSkills.Contains(pin.SkillName))
        {
            return null;
        }

        return run.Variables.TryGetValue(RunMode.Key, out var value) && value is string mode && KnownModes.Contains(mode)
            ? mode
            : null;
    }
}
