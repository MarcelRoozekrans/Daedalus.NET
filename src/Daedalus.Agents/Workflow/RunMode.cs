namespace Daedalus.Agents.Workflow;

/// <summary>
///     Phase 2.6 ruling R61: the mode a manufacture run executes in, which the host knows and therefore states, so no
///     node's agent ever has to infer it. <see cref="ManufactureRunStarter"/> writes it under <see cref="Key"/> as one of
///     the run's opening variables, from <see cref="SandboxConfig.Enabled"/>, and <see cref="RunModeRunner"/> states it in
///     the task text of the <c>implement</c> and <c>review</c> nodes.
/// </summary>
/// <remarks>
///     <b>Why the host states it.</b> The phase 2.6 live proof ran a sandboxed run whose implementer, handed a long tool
///     list, decided it held no <c>sandbox__build</c> and was therefore in local mode, and reported <c>blocked</c> on a
///     <c>.csproj</c> change the sandbox would have allowed. The skills told it to infer the mode from which tools it was
///     offered. A rule that depends on a model reading its own tool list correctly fails the way that run did; the
///     start already knows the answer.
///     <para>
///     <b>Only the start writes it.</b> <see cref="ReviewHandoff.HostWritten"/> lists <see cref="Key"/> with no writing
///     action, so <see cref="ReviewHandoffWorkflowStore"/> strips it from every node's report and an agent cannot change
///     the mode a later node is told.
///     </para>
/// </remarks>
public static class RunMode
{
    /// <summary>The run variable the mode is written under.</summary>
    public const string Key = "run_mode";

    /// <summary>The run's worktree lives in a per-run sandbox container.</summary>
    public const string Sandbox = "sandbox";

    /// <summary>The run's worktree is the host's own, the local mode of development hosts.</summary>
    public const string Local = "local";

    /// <summary>The mode a run started under <paramref name="sandbox"/> executes in.</summary>
    public static string For(SandboxConfig sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return sandbox.Enabled ? Sandbox : Local;
    }
}
