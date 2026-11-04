using Daedalus.Agents.Api;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Thalos;

namespace Daedalus.Api.Endpoints;

/// <summary>Lists the Thalos agents registered in configuration (<c>Thalos:Agents</c>).</summary>
/// <remarks>
///     The first port and the pattern the others follow, ruling R38: one group through
///     <see cref="DaedalusEndpoints.MapApiGroup"/> with the controller's route, policy, tag and, for a controller
///     that had <c>[Produces("application/json")]</c>, <see cref="ProblemContentType.Json"/>; one public static
///     handler per action, named as the action, returning the typed result whose type argument is the type the
///     action's <c>[ProducesResponseType]</c> declared.
/// </remarks>
public static class AgentEndpoints
{
    /// <summary>Maps <c>GET api/agents</c>, under policy <c>AgentUse</c>.</summary>
    public static RouteGroupBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/agents")
            .RequireAuthorization("AgentUse")
            .WithTags("Agents")
            .WithMetadata(ProblemContentType.Json);

        group.MapGet(string.Empty, GetAgents);

        return group;
    }

    /// <summary>All available agents, in registration order.</summary>
    public static Ok<IReadOnlyList<AgentSummaryDto>> GetAgents([FromServices] IAgentCatalog catalog)
    {
        return TypedResults.Ok<IReadOnlyList<AgentSummaryDto>>(catalog.Agents.Select(AgentDtoMapper.ToDto).ToList());
    }
}
