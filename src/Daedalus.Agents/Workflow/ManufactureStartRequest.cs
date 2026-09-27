using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     What <see cref="IManufactureRunStarter.StartAsync"/> needs to start one manufacture run.
/// </summary>
/// <param name="WorkIntent">What the run should manufacture, in the requester's own words.</param>
/// <param name="Repository">
///     The <see cref="RepositoryConfig.Name"/> of an entry in <c>Thalos:Workflow:Repositories</c>. Only a name, never
///     a URL: the remote comes from reviewed configuration, so no request can point a run at an arbitrary remote. A
///     name that is not allow-listed fails the start.
/// </param>
/// <param name="StartedBy">
///     The principal that asked for the run, built by <see cref="Security.RunPrincipals.From"/>. Required: the
///     run records it as <see cref="WorkflowRun.StartedBy"/>, and there is no start without one.
/// </param>
public sealed record ManufactureStartRequest(string WorkIntent, string Repository, RunPrincipal StartedBy);
