using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     What <see cref="IManufactureRunStarter.StartAsync"/> needs to start one manufacture run.
/// </summary>
/// <param name="WorkIntent">What the run should manufacture, in the requester's own words.</param>
/// <param name="StartedBy">
///     The principal that asked for the run, built by <see cref="Security.RunPrincipals.From"/>. Required: the
///     run records it as <see cref="WorkflowRun.StartedBy"/>, and there is no start without one.
/// </param>
public sealed record ManufactureStartRequest(string WorkIntent, RunPrincipal StartedBy);
