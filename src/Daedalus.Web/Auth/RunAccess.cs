using System.Security.Claims;

namespace Daedalus.Web.Auth;

/// <summary>Who may see the console and who may retry, mirroring the API's WorkflowResume and Admin policies.</summary>
public static class RunAccess
{
    /// <summary>Whether <paramref name="user"/> may see runs and act at the gate.</summary>
    public static bool CanView(ClaimsPrincipal user) => user.IsInRole("developer") || user.IsInRole("admin");

    /// <summary>Whether <paramref name="user"/> may retry a failed run.</summary>
    public static bool CanRetry(ClaimsPrincipal user) => user.IsInRole("admin");
}
