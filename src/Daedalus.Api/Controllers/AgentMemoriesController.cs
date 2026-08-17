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
///     Browse and forget agent memories. A caller sees their own memories plus the shared owner's (host-written project
///     knowledge); deleting is own-only, shared-owner memories additionally need the <c>developer</c> policy. Unknown and
///     foreign ids answer 404, never 403, so ids cannot be probed — the same rule the sessions controller follows.
/// </summary>
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

    /// <summary>The caller's own memories plus the shared owner's, most recently updated first.</summary>
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

        AgentId? agent = null;
        if (!string.IsNullOrEmpty(agentId))
        {
            if (!AgentId.TryParse(agentId, null, out var parsed))
            {
                return AgentError.Validation("agentId is not a valid id.").ToActionResult(this);
            }

            agent = parsed;
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

        var query = new MemoryQuery
        {
            OwnerIds = VisibleOwners(caller),
            AgentId = agent,
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

        var items = result.Value.Items.Select(r => AgentDtoMapper.ToDto(r, config.SharedOwnerId)).ToList();
        return Ok(new PagedResultDto<MemoryDto>(items, result.Value.TotalCount, result.Value.Page, result.Value.PageSize));
    }

    /// <summary>One memory the caller can see (their own or the shared owner's). Anything else answers 404.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(MemoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
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

        if (!IsVisible(record.Value, caller))
        {
            LogForeignAccess(logger, caller.Id, memoryId);
            return AgentError.MemoryNotFound(memoryId).ToActionResult(this);
        }

        return Ok(AgentDtoMapper.ToDto(record.Value, config.SharedOwnerId));
    }

    /// <summary>
    ///     Forgets a memory: <c>hard=false</c> archives it (recoverable), <c>hard=true</c> deletes it. Own memories only;
    ///     shared-owner memories need the <c>developer</c> (or <c>admin</c>) role. Foreign ids answer 404.
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

    private bool IsVisible(MemoryRecord record, ISecurityContext caller) =>
        string.Equals(record.OwnerId, caller.Id, StringComparison.Ordinal) ||
        string.Equals(record.OwnerId, config.SharedOwnerId, StringComparison.Ordinal);

    [LoggerMessage(EventId = 320, Level = LogLevel.Warning, Message = "Caller {Caller} attempted to access memory {MemoryId} it cannot see; answered 404")]
    private static partial void LogForeignAccess(ILogger logger, string caller, MemoryId memoryId);

    [LoggerMessage(EventId = 321, Level = LogLevel.Information, Message = "Caller {Caller} forgot memory {MemoryId} (hard: {Hard})")]
    private static partial void LogForgotten(ILogger logger, string caller, MemoryId memoryId, bool hard);
}
