using Daedalus.Agents.Api;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Thalos;

namespace Daedalus.Api.Controllers;

/// <summary>Lists the Thalos agents registered in configuration (<c>Thalos:Agents</c>).</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/agents")]
[Authorize(Policy = "AgentUse")]
[Produces("application/json")]
public sealed class AgentsController(IAgentCatalog catalog) : ControllerBase
{
    /// <summary>All available agents, in registration order.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AgentSummaryDto>), StatusCodes.Status200OK)]
    public IActionResult GetAgents() => Ok(catalog.Agents.Select(AgentDtoMapper.ToDto).ToList());
}
