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
/// <param name="Process">
///     The process to start: <c>manufacture</c> when <see langword="null"/>, or <c>review-replay</c>. Any other name is
///     refused, so a request cannot start an arbitrary stored definition.
/// </param>
/// <param name="BaseBranch">
///     Only for <c>review-replay</c>, where it is required: the <c>manufacture/&lt;run-id&gt;</c> branch of the allow-listed
///     repository whose code the replay reviews, in exactly that form with a lowercase run id. Refused for every other
///     process and in any other shape.
/// </param>
public sealed record ManufactureStartRequest(
    string WorkIntent, string Repository, RunPrincipal StartedBy, string? Process = null, string? BaseBranch = null);
