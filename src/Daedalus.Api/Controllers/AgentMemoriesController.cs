using Daedalus.Agents;
using Daedalus.Agents.Api;
using Daedalus.Agents.Security;
using Daedalus.Api.Agents;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Thalos;
using Thalos.Memory;
using ZeroAlloc.Authorization;

namespace Daedalus.Api.Controllers;

/// <summary>
///     Browse and forget agent memories. Reads apply exactly the visibility rule Thalos' own <c>memory__*</c> tools apply
///     (<see cref="MemoryScope.Includes"/>): the caller's owner-wide memories, the memories pinned to the agent named by the
///     optional <c>agentId</c> parameter, and the shared owner's owner-wide memories (host-written project knowledge).
///     Deleting is own-only — the shared owner grants read, never forget — and shared-owner memories additionally need the
///     <c>developer</c> policy. Unknown, archived and invisible ids answer 404, never 403, so ids cannot be probed — the same
///     rule the sessions controller follows.
/// </summary>
/// <remarks>
///     <c>agentId</c> is the caller's <em>agent context</em>, not a filter: it widens the scope with that agent's pinned
///     memories (an HTTP caller has no ambient agent, where a tool call does). Like <c>memory__list</c>, the store pages
///     <em>before</em> visibility is applied, so a page can hold fewer than <c>pageSize</c> items and <c>Total</c> can
///     over-count when other agents' pinned memories exist.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/agent-memories")]
[Authorize(Policy = "AgentUse")]
[Produces("application/json")]
public sealed partial class AgentMemoriesController(
    IMemoryService memory,
    IMemoryStore store,
    MemoryConfig config,
    ILogger<AgentMemoriesController> logger) : ControllerBase
{
    /// <summary>Largest page the endpoint serves (Thalos clamps to the same bound).</summary>
    private const int MaxPageSize = MemoryQuery.MaxPageSize;

    private static readonly DeveloperPolicy Developer = new();

    /// <summary>The memories visible to the caller (see the type remarks), most recently updated first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<MemoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? agentId = null,
        [FromQuery] string? kind = null,
        [FromQuery] string? tag = null,
        [FromQuery] bool includeArchived = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        if (!TryScope(caller, agentId, out var scope))
        {
            return AgentError.Validation("agentId is not a valid id.").ToActionResult(this);
        }

        MemoryKind? memoryKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            // TryParse trims and lower-cases; an identifier Thalos would never store is a client mistake, not an empty page.
            if (!MemoryKind.TryParse(kind, out var parsedKind))
            {
                return AgentError.Validation("kind is not a valid memory kind.").ToActionResult(this);
            }

            memoryKind = parsedKind;
        }

        // No AgentId filter: that member pins the query to one agent, while the caller wants everything the scope allows.
        // Visibility is applied below with MemoryScope.Includes — byte for byte what Thalos' memory__list tool does.
        var query = new MemoryQuery
        {
            OwnerIds = VisibleOwners(caller),
            Kinds = memoryKind is null ? null : [memoryKind],
            Tags = string.IsNullOrWhiteSpace(tag) ? null : [tag.Trim()],
            IncludeArchived = includeArchived,
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, MaxPageSize),
        };

        var result = await memory.ListAsync(query, ct);
        if (result.IsFailure)
        {
            return result.Error.ToActionResult(this);
        }

        var items = result.Value.Items
            .Where(r => scope.Includes(r.OwnerId, r.AgentId))
            .Select(r => AgentDtoMapper.ToDto(r, config.SharedOwnerId))
            .ToList();
        return Ok(new PagedResultDto<MemoryDto>(items, result.Value.TotalCount, result.Value.Page, result.Value.PageSize));
    }

    /// <summary>
    ///     One memory visible to the caller (see the type remarks); mainly used to hydrate the ids a <c>memory-recalled</c>
    ///     event carries. An archived memory answers 404 like an unknown one: recall drops archived records before they can
    ///     ever be recalled, so nothing that needs hydrating is archived, and a forgotten memory should stay forgotten.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(MemoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, [FromQuery] string? agentId = null, CancellationToken ct = default)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        if (!MemoryId.TryParse(id, null, out var memoryId))
        {
            return AgentError.Validation("id is not a valid memory id.").ToActionResult(this);
        }

        if (!TryScope(caller, agentId, out var scope))
        {
            return AgentError.Validation("agentId is not a valid id.").ToActionResult(this);
        }

        var record = await store.GetAsync(memoryId, ct);
        if (record.IsFailure)
        {
            return record.Error.ToActionResult(this);
        }

        // GetAsync returns archived records too (the store is the source of truth); the HTTP surface does not.
        if (record.Value.IsArchived || !scope.Includes(record.Value.OwnerId, record.Value.AgentId))
        {
            LogForeignAccess(logger, caller.Id, memoryId);
            return AgentError.MemoryNotFound(memoryId).ToActionResult(this);
        }

        return Ok(AgentDtoMapper.ToDto(record.Value, config.SharedOwnerId));
    }

    /// <summary>
    ///     Forgets a memory: <c>hard=false</c> archives it (recoverable), <c>hard=true</c> deletes it. Own memories only;
    ///     shared-owner memories need the <c>developer</c> (or <c>admin</c>) role. Foreign ids answer 404. Deletion turns on
    ///     <em>ownership</em>, not on the read scope (as in Thalos' <c>memory__forget</c>): an owner may forget their own
    ///     memory even when it is pinned to another agent and therefore invisible to this caller's <c>List</c>.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Forget(string id, [FromQuery] bool hard = false, CancellationToken ct = default)
    {
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        if (!MemoryId.TryParse(id, null, out var memoryId))
        {
            return AgentError.Validation("id is not a valid memory id.").ToActionResult(this);
        }

        var record = await store.GetAsync(memoryId, ct);
        if (record.IsFailure)
        {
            return record.Error.ToActionResult(this);
        }

        string scopeOwner;
        if (string.Equals(record.Value.OwnerId, caller.Id, StringComparison.Ordinal))
        {
            scopeOwner = caller.Id;
        }
        else if (string.Equals(record.Value.OwnerId, config.SharedOwnerId, StringComparison.Ordinal))
        {
            var allowed = await Developer.EvaluateAsync(caller, ct);
            if (allowed.IsFailure)
            {
                return AgentError.MemoryForbidden(memoryId).ToActionResult(this);
            }

            scopeOwner = config.SharedOwnerId;
        }
        else
        {
            LogForeignAccess(logger, caller.Id, memoryId);
            return AgentError.MemoryNotFound(memoryId).ToActionResult(this);
        }

        var result = await memory.ForgetAsync(memoryId, new MemoryScope(scopeOwner, null, null), hard, ct);
        if (result.IsFailure)
        {
            return result.Error.ToActionResult(this);
        }

        LogForgotten(logger, caller.Id, memoryId, hard);
        return NoContent();
    }

    /// <summary>The owners a caller may read: itself plus the shared owner (once, when they differ).</summary>
    private List<string> VisibleOwners(ISecurityContext caller)
    {
        var owners = new List<string> { caller.Id };
        if (!string.Equals(config.SharedOwnerId, caller.Id, StringComparison.Ordinal))
        {
            owners.Add(config.SharedOwnerId);
        }

        return owners;
    }

    /// <summary>Builds the caller's read scope; <see langword="false"/> when <paramref name="agentId"/> is present but unparsable.</summary>
    private bool TryScope(ISecurityContext caller, string? agentId, out MemoryScope scope)
    {
        scope = default;
        AgentId? agent = null;
        if (!string.IsNullOrEmpty(agentId))
        {
            if (!AgentId.TryParse(agentId, null, out var parsed))
            {
                return false;
            }

            agent = parsed;
        }

        scope = new MemoryScope(caller.Id, agent, config.SharedOwnerId);
        return true;
    }

    [LoggerMessage(EventId = 320, Level = LogLevel.Warning, Message = "Caller {Caller} attempted to access memory {MemoryId} it cannot see; answered 404")]
    private static partial void LogForeignAccess(ILogger logger, string caller, MemoryId memoryId);

    [LoggerMessage(EventId = 321, Level = LogLevel.Information, Message = "Caller {Caller} forgot memory {MemoryId} (hard: {Hard})")]
    private static partial void LogForgotten(ILogger logger, string caller, MemoryId memoryId, bool hard);
}
