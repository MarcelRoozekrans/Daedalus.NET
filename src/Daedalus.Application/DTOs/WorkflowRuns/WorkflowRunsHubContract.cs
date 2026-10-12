namespace Daedalus.Application.DTOs.WorkflowRuns;

/// <summary>The run hub's address and client method, shared by the Api that maps it and the Web that connects to it.</summary>
public static class WorkflowRunsHubContract
{
    /// <summary>Where the hub is mapped: root-relative, like every API route.</summary>
    public const string Path = "/hubs/workflow-runs";

    /// <summary>The client method every change is sent to, carrying a <see cref="WorkflowRunChanged"/>.</summary>
    public const string RunChangedMethod = "RunChanged";
}
