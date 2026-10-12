using Daedalus.Application.DTOs.WorkflowRuns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Daedalus.Api.Hubs;

/// <summary>
///     The console's live channel. Clients only listen: the server sends <see cref="RunChangedMethod"/> with a
///     <c>WorkflowRunChanged</c> whenever the store commits a change, and clients re-fetch through the REST API.
/// </summary>
[Authorize(Policy = "WorkflowResume")]
public sealed class WorkflowRunsHub : Hub
{
    /// <summary>Where the hub is mapped, as <see cref="WorkflowRunsHubContract.Path"/> shares it with the Web.</summary>
    public const string Path = WorkflowRunsHubContract.Path;

    /// <summary>The client method every change is sent to, as <see cref="WorkflowRunsHubContract.RunChangedMethod"/>.</summary>
    public const string RunChangedMethod = WorkflowRunsHubContract.RunChangedMethod;
}
