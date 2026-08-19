using Microsoft.AspNetCore.Mvc;
using Thalos;

namespace Daedalus.Api.Agents;

/// <summary>
///     The one place that maps a Thalos <see cref="AgentError"/> to HTTP. The <see cref="AgentErrorCode"/> name is exposed both
///     as the ProblemDetails <c>title</c> and as the <c>code</c> extension so clients can switch on it without parsing text.
///     <see cref="AgentError.Detail"/> never carries raw exception text (Thalos guarantees type names only), so it is safe to return.
/// </summary>
internal static class AgentErrorResults
{
    /// <summary>Non-standard status used by nginx and friends for "client closed request"; a turn cancelled by the caller.</summary>
    public const int ClientClosedRequest = 499;

    /// <summary>Maps an error code to its HTTP status.</summary>
    public static int ToStatusCode(AgentErrorCode code) => code switch
    {
        AgentErrorCode.Validation => StatusCodes.Status400BadRequest,
        AgentErrorCode.Unauthorized => StatusCodes.Status403Forbidden,
        // Non-owner reads are SessionNotFound by design (no probing of session ids) → 404 like the other lookups.
        AgentErrorCode.AgentNotFound or AgentErrorCode.SessionNotFound or AgentErrorCode.ToolNotFound => StatusCodes.Status404NotFound,
        AgentErrorCode.SessionBusy or AgentErrorCode.SessionClosed => StatusCodes.Status409Conflict,
        AgentErrorCode.Quarantined or AgentErrorCode.ToolDenied => StatusCodes.Status422UnprocessableEntity,
        AgentErrorCode.Cancelled => ClientClosedRequest,
        AgentErrorCode.MemoryValidationFailed => StatusCodes.Status400BadRequest,
        AgentErrorCode.MemoryForbidden => StatusCodes.Status403Forbidden,
        AgentErrorCode.MemoryNotFound => StatusCodes.Status404NotFound,
        AgentErrorCode.MemoryIndexUnavailable => StatusCodes.Status503ServiceUnavailable,
        AgentErrorCode.MemoryStoreFailed or AgentErrorCode.MemoryIndexFailed => StatusCodes.Status502BadGateway,
        AgentErrorCode.SkillValidationFailed => StatusCodes.Status400BadRequest,
        AgentErrorCode.SkillNotFound => StatusCodes.Status404NotFound,
        AgentErrorCode.SkillSearchUnavailable => StatusCodes.Status503ServiceUnavailable,
        AgentErrorCode.SkillStoreFailed => StatusCodes.Status502BadGateway,
        // ProviderError, StoreError and anything Thalos adds later: an upstream dependency failed.
        _ => StatusCodes.Status502BadGateway,
    };

    /// <summary>Builds the RFC 7807 problem response for <paramref name="error"/>.</summary>
    public static IActionResult ToActionResult(this AgentError error, ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        var code = error.Code.ToString();
        return controller.Problem(
            title: code,
            detail: error.Detail is null ? error.Message : $"{error.Message} ({error.Detail})",
            statusCode: ToStatusCode(error.Code),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code });
    }
}
