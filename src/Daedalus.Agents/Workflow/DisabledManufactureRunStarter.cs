using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The default <see cref="IManufactureRunStarter"/>, registered whenever <c>Thalos:Workflow:Enabled</c> is
///     <see langword="false"/> — every Integration test host but <c>ProcessDefinitionSyncEndToEndTests</c>' own,
///     and any production host that rolls the engine back. Reports the engine is off instead of leaving
///     <see cref="IManufactureRunStarter"/> unresolvable, so <c>WorkflowRunsController</c> and
///     <see cref="Tools.DaedalusManufactureTools"/> stay constructible either way — a 503 from
///     <c>POST /api/workflow-runs</c> is a deliberate, mapped response, not an unhandled DI failure.
/// </summary>
public sealed class DisabledManufactureRunStarter : IManufactureRunStarter
{
    /// <summary>
    ///     The failure text of a disabled engine. It is reported as <see cref="ManufactureStartFailureKind.Disabled"/>,
    ///     which <c>WorkflowRunsController.Start</c> maps to 503 without Retry-After.
    /// </summary>
    public const string DisabledMessage = "The manufacturing workflow engine is disabled on this host.";

    /// <inheritdoc />
    public ValueTask<Result<Guid, ManufactureStartFailure>> StartAsync(ManufactureStartRequest request, CancellationToken ct) =>
        new(Result<Guid, ManufactureStartFailure>.Failure(
            new ManufactureStartFailure(ManufactureStartFailureKind.Disabled, DisabledMessage)));
}
