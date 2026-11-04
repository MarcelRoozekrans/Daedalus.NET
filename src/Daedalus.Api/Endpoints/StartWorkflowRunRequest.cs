namespace Daedalus.Api.Endpoints;

/// <summary>Request body for <see cref="WorkflowRunsEndpoints.Start"/>.</summary>
/// <param name="WorkIntent">What the run should manufacture, in the requester's own words. Must not be blank.</param>
/// <param name="Repository">
///     The name of an entry in <c>Thalos:Workflow:Repositories</c>, such as <c>sandbox</c>. Must not be blank. Only a
///     name, never a URL: a name that is not allow-listed is a 400, so no request can point a run at another remote.
/// </param>
/// <param name="Process">
///     Optional. <c>manufacture</c> when left out; <c>review-replay</c> reviews the code of an existing manufacture branch
///     and ends. Any other name is a 400.
/// </param>
/// <param name="BaseBranch">
///     Required for <c>review-replay</c> and refused for every other process: the <c>manufacture/&lt;run-id&gt;</c> branch of
///     the allow-listed repository to review, in exactly that form. A run that worked on another repository than
///     <c>Repository</c> is a 400.
/// </param>
public sealed record StartWorkflowRunRequest(string WorkIntent, string Repository, string? Process = null, string? BaseBranch = null);
