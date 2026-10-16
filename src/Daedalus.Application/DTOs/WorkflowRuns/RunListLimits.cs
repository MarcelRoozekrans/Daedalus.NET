namespace Daedalus.Application.DTOs.WorkflowRuns;

/// <summary>The bounds of one <c>GET /api/workflow-runs</c> request, shared by the API that applies them and the console.</summary>
public static class RunListLimits
{
    /// <summary>
    ///     The most run summaries one list request reads from the store while it looks for runs that match
    ///     <c>signal</c> or <c>updatedSince</c>, which the store cannot filter on. A request that reaches it returns what
    ///     it found and a cursor, so the caller knows the list may hold more.
    /// </summary>
    public const int ScanCap = 500;
}
