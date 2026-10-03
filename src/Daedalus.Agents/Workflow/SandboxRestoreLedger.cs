using System.Collections.Concurrent;
using Thalos.Workspaces;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Phase 2.6, task B5: which runs <see cref="RunToolServersReadyGate"/> has already read a sandbox's restore state
///     for, so it reads it once per run however many task nodes the run dispatches, and however many dispatch at once.
/// </summary>
/// <remarks>
///     <para>
///     <b>An entry lives as long as the run's sandbox.</b> As an <see cref="IRunWorkspaceObserver"/>, the ledger
///     forgets a run when its workspace is about to be removed: when the sweeper removes a finished run's sandbox, and
///     when a run is parked, which deletes its container. So a long-lived host holds at most one entry per live sandbox,
///     never one per run it ever dispatched. A run's next sandbox, after a park, restores again, so the gate reads it
///     again; the run record store, not this ledger, is what keeps the record to one per run, across sandboxes and
///     host restarts alike.
///     </para>
///     <para>
///     No dependencies of its own on purpose: the sandbox provider resolves every observer when it is built, and the
///     gate depends on that provider, so an observer that depended on the gate or the provider would be a cycle.
///     </para>
/// </remarks>
internal sealed class SandboxRestoreLedger : IRunWorkspaceObserver
{
    private readonly ConcurrentDictionary<Guid, bool> _read = new();

    /// <summary>
    ///     Claims <paramref name="runId"/>'s one read: true for exactly one caller until <see cref="Release"/> or the
    ///     sandbox's removal, atomically, so two nodes of one run dispatching at once read once.
    /// </summary>
    public bool TryClaim(Guid runId) => _read.TryAdd(runId, true);

    /// <summary>Gives a claim back, so the next node of the run reads again: the read before it observed nothing.</summary>
    public void Release(Guid runId) => _read.TryRemove(runId, out _);

    /// <inheritdoc />
    public ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc />
    /// <remarks>A repeat is a no-op, as the contract requires.</remarks>
    public ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Release(workspace.RunId);
        return ValueTask.CompletedTask;
    }
}
