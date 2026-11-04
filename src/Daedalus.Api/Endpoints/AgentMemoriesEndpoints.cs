using Daedalus.Agents;
using Daedalus.Agents.Api;
using Daedalus.Agents.Security;
using Daedalus.Api.Agents;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Thalos;
using Thalos.Memory;
using ZeroAlloc.Authorization;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Browse and forget agent memories. Reads apply exactly the visibility rule Thalos' own <c>memory__*</c> tools apply
///     (<see cref="MemoryScope.Includes"/>): the caller's owner-wide memories, the memories pinned to the agent named by the
///     optional <c>agentId</c> parameter, and the shared owner's owner-wide memories (host-written project knowledge).
///     Deleting is own-only — the shared owner grants read, never forget — and shared-owner memories additionally need the
///     <c>developer</c> policy. Unknown, archived and invisible ids answer 404, never 403, so ids cannot be probed — the same
///     rule the sessions endpoints follow.
/// </summary>
/// <remarks>
///     <para>
///         Ported from <c>AgentMemoriesController</c> on the pattern of <see cref="BrainstormEndpoints"/> (R38, R42, R49).
///         <c>agentId</c> is the caller's <em>agent context</em>, not a filter: it widens the scope with that agent's pinned
///         memories (an HTTP caller has no ambient agent, where a tool call does). Like <c>memory__list</c>, the store pages
///         <em>before</em> visibility is applied, so a page can hold fewer than <c>pageSize</c> items and <c>Total</c> can
///         over-count when other agents' pinned memories exist.
///     </para>
///     <para>
///         Every failure is a problem built by <see cref="ApiProblem.Of"/> or
///         <see cref="AgentErrorResults.ToProblem"/>, with the group's <c>application/json</c> content type.
///     </para>
/// </remarks>
public static partial class AgentMemoriesEndpoints
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;

    /// <summary>Largest page the endpoint serves (Thalos clamps to the same bound).</summary>
    private const int MaxPageSize = MemoryQuery.MaxPageSize;

    private static readonly DeveloperPolicy Developer = new();

    /// <summary>Maps <c>api/agent-memories</c> under the <c>AgentUse</c> policy.</summary>
    public static RouteGroupBuilder MapAgentMemoriesEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/agent-memories")
            .RequireAuthorization("AgentUse")
            .WithTags("AgentMemories")
            .WithMetadata(ProblemContentType.Json);

        group.MapGet(string.Empty, List)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .WithQueryParameter<string>("agentId")
            .WithQueryParameter<string>("kind")
            .WithQueryParameter<string>("tag")
            .WithQueryParameter("includeArchived", false)
            .WithQueryParameter("page", DefaultPage)
            .WithQueryParameter("pageSize", DefaultPageSize);

        group.MapGet("{id}", Get)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .WithQueryParameter<string>("agentId");

        group.MapDelete("{id}", Forget)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status403Forbidden, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .WithQueryParameter("hard", false);

        return group;
    }

    /// <summary>The memories visible to the caller (see the type remarks), most recently updated first.</summary>
    public static async Task<Results<Ok<PagedResultDto<MemoryDto>>, ApiProblemResult>> List(
        ApiQuery<string> agentId, ApiQuery<string> kind, ApiQuery<string> tag, ApiQuery<bool> includeArchived,
        ApiQuery<int> page, ApiQuery<int> pageSize, HttpContext context,
        [FromServices] IMemoryService memory, [FromServices] MemoryConfig config, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        if (!TryScope(caller, agentId.Value, config, out var scope))
        {
            return AgentError.Validation("agentId is not a valid id.").ToProblem(context);
        }

        MemoryKind? memoryKind = null;
        if (!string.IsNullOrWhiteSpace(kind.Value))
        {
            // TryParse trims and lower-cases; an identifier Thalos would never store is a client mistake, not an empty page.
            if (!MemoryKind.TryParse(kind.Value, out var parsedKind))
            {
                return AgentError.Validation("kind is not a valid memory kind.").ToProblem(context);
            }

            memoryKind = parsedKind;
        }

        // No AgentId filter: that member pins the query to one agent, while the caller wants everything the scope allows.
        // Visibility is applied below with MemoryScope.Includes — byte for byte what Thalos' memory__list tool does.
        var query = new MemoryQuery
        {
            OwnerIds = VisibleOwners(caller, config),
            Kinds = memoryKind is null ? null : [memoryKind],
            Tags = string.IsNullOrWhiteSpace(tag.Value) ? null : [tag.Value.Trim()],
            IncludeArchived = includeArchived.ValueOr(false),
            Page = Math.Max(1, page.ValueOr(DefaultPage)),
            PageSize = Math.Clamp(pageSize.ValueOr(DefaultPageSize), 1, MaxPageSize),
        };

        var result = await memory.ListAsync(query, ct);
        if (result.IsFailure)
        {
            return result.Error.ToProblem(context);
        }

        var items = result.Value.Items
            .Where(r => scope.Includes(r.OwnerId, r.AgentId))
            .Select(r => AgentDtoMapper.ToDto(r, config.SharedOwnerId))
            .ToList();
        return TypedResults.Ok(new PagedResultDto<MemoryDto>(items, result.Value.TotalCount, result.Value.Page, result.Value.PageSize));
    }

    /// <summary>
    ///     One memory visible to the caller (see the type remarks); mainly used to hydrate the ids a <c>memory-recalled</c>
    ///     event carries. An archived memory answers 404 like an unknown one: recall drops archived records before they can
    ///     ever be recalled, so nothing that needs hydrating is archived, and a forgotten memory should stay forgotten.
    /// </summary>
    public static async Task<Results<Ok<MemoryDto>, ApiProblemResult>> Get(
        string id, ApiQuery<string> agentId, HttpContext context,
        [FromServices] IMemoryStore store, [FromServices] MemoryConfig config, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        if (!MemoryId.TryParse(id, null, out var memoryId))
        {
            return AgentError.Validation("id is not a valid memory id.").ToProblem(context);
        }

        if (!TryScope(caller, agentId.Value, config, out var scope))
        {
            return AgentError.Validation("agentId is not a valid id.").ToProblem(context);
        }

        var record = await store.GetAsync(memoryId, ct);
        if (record.IsFailure)
        {
            return record.Error.ToProblem(context);
        }

        // GetAsync returns archived records too (the store is the source of truth); the HTTP surface does not.
        if (record.Value.IsArchived || !scope.Includes(record.Value.OwnerId, record.Value.AgentId))
        {
            Log.ForeignAccess(loggerFactory.CreateLogger(typeof(AgentMemoriesEndpoints)), caller.Id, memoryId);
            return AgentError.MemoryNotFound(memoryId).ToProblem(context);
        }

        return TypedResults.Ok(AgentDtoMapper.ToDto(record.Value, config.SharedOwnerId));
    }

    /// <summary>
    ///     Forgets a memory: <c>hard=false</c> archives it (recoverable), <c>hard=true</c> deletes it. Own memories only;
    ///     shared-owner memories need the <c>developer</c> (or <c>admin</c>) role. Foreign ids answer 404. Deletion turns on
    ///     <em>ownership</em>, not on the read scope (as in Thalos' <c>memory__forget</c>): an owner may forget their own
    ///     memory even when it is pinned to another agent and therefore invisible to this caller's <c>List</c>.
    /// </summary>
    public static async Task<Results<NoContent, ApiProblemResult>> Forget(
        string id, ApiQuery<bool> hard, HttpContext context,
        [FromServices] IMemoryService memory, [FromServices] IMemoryStore store, [FromServices] MemoryConfig config,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        if (!MemoryId.TryParse(id, null, out var memoryId))
        {
            return AgentError.Validation("id is not a valid memory id.").ToProblem(context);
        }

        var record = await store.GetAsync(memoryId, ct);
        if (record.IsFailure)
        {
            return record.Error.ToProblem(context);
        }

        var logger = loggerFactory.CreateLogger(typeof(AgentMemoriesEndpoints));
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
                return AgentError.MemoryForbidden(memoryId).ToProblem(context);
            }

            scopeOwner = config.SharedOwnerId;
        }
        else
        {
            Log.ForeignAccess(logger, caller.Id, memoryId);
            return AgentError.MemoryNotFound(memoryId).ToProblem(context);
        }

        var hardDelete = hard.ValueOr(false);
        var result = await memory.ForgetAsync(memoryId, new MemoryScope(scopeOwner, null, null), hardDelete, ct);
        if (result.IsFailure)
        {
            return result.Error.ToProblem(context);
        }

        Log.Forgotten(logger, caller.Id, memoryId, hardDelete);
        return TypedResults.NoContent();
    }

    /// <summary>The owners a caller may read: itself plus the shared owner (once, when they differ).</summary>
    private static List<string> VisibleOwners(ISecurityContext caller, MemoryConfig config)
    {
        var owners = new List<string> { caller.Id };
        if (!string.Equals(config.SharedOwnerId, caller.Id, StringComparison.Ordinal))
        {
            owners.Add(config.SharedOwnerId);
        }

        return owners;
    }

    /// <summary>Builds the caller's read scope; <see langword="false"/> when <paramref name="agentId"/> is present but unparsable.</summary>
    private static bool TryScope(ISecurityContext caller, string? agentId, MemoryConfig config, out MemoryScope scope)
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 320, Level = LogLevel.Warning, Message = "Caller {Caller} attempted to access memory {MemoryId} it cannot see; answered 404")]
        public static partial void ForeignAccess(ILogger logger, string caller, MemoryId memoryId);

        [LoggerMessage(EventId = 321, Level = LogLevel.Information, Message = "Caller {Caller} forgot memory {MemoryId} (hard: {Hard})")]
        public static partial void Forgotten(ILogger logger, string caller, MemoryId memoryId, bool hard);
    }
}
