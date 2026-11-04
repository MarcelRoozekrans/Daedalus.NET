using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Services.Git;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Endpoints;

/// <summary>Repository configuration reads and writes over <see cref="IRepositoryConfigurationRepository"/>.</summary>
/// <remarks>
///     Ported from <c>RepositoriesController</c> on the pattern of <see cref="ProjectsEndpoints"/> (R42, R45). The
///     controller wrote every failure as an explicit <c>ProblemDetails</c> with a title and the repository's error and no
///     <c>type</c> or <c>traceId</c>, so each goes out through <see cref="ApiProblem.AsGiven"/> (R43). The one body that
///     is not a problem is the failed connection test, a 400 <see cref="TestConnectionResponse"/>, written through
///     <see cref="ApiJson.Of"/> (R45). The 201's <c>Location</c> keeps the class-name casing MVC's
///     <c>CreatedAtAction</c> produced, <c>/api/Repositories/{id}</c> (R21). The controller had no catch-all, so
///     neither have the handlers.
/// </remarks>
public static partial class RepositoriesEndpoints
{
    /// <summary>Maps <c>api/repositories</c>, Admin only, with a rate limit on the writes.</summary>
    public static RouteGroupBuilder MapRepositoriesEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/repositories")
            .RequireAuthorization()
            .WithTags("Repositories")
            .WithMetadata(ProblemContentType.Json);

        group.MapGet(string.Empty, GetAllRepositories)
            .RequireAuthorization("Admin");

        group.MapGet("{id:guid}", GetRepositoryById)
            .RequireAuthorization("Admin")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        group.MapPost(string.Empty, CreateRepository)
            .RequireAuthorization("Admin")
            .RequireRateLimiting("write-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json");

        group.MapPut("{id:guid}", UpdateRepository)
            .RequireAuthorization("Admin")
            .RequireRateLimiting("write-operations")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapDelete("{id:guid}", DeleteRepository)
            .RequireAuthorization("Admin")
            .RequireRateLimiting("write-operations")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        group.MapPost("{id:guid}/test-connection", TestRepositoryConnection)
            .RequireAuthorization("Admin")
            .RequireRateLimiting("write-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        return group;
    }

    /// <summary>Get all repository configurations.</summary>
    public static async Task<Results<Ok<IEnumerable<RepositoryConfigurationDto>>, ApiProblemResult>> GetAllRepositories(
        HttpContext context, [FromServices] IRepositoryConfigurationRepository repository,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(RepositoriesEndpoints));
        Log.FetchingAllRepositories(logger);

        var result = await repository.GetAllAsync(ct);

        if (result.IsFailure)
        {
            return InternalError(context, result.Error);
        }

        return TypedResults.Ok(result.Value.Select(MapToDto));
    }

    /// <summary>Get a specific repository configuration by ID.</summary>
    public static async Task<Results<Ok<RepositoryConfigurationDto>, ApiProblemResult>> GetRepositoryById(
        Guid id, HttpContext context, [FromServices] IRepositoryConfigurationRepository repository,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(RepositoriesEndpoints));
        Log.FetchingRepositoryById(logger, id);

        var result = await repository.GetByIdAsync(id, ct);

        if (result.IsFailure)
        {
            return NotFound(context, result.Error);
        }

        return TypedResults.Ok(MapToDto(result.Value));
    }

    /// <summary>Create a new repository configuration.</summary>
    public static async Task<Results<Created<RepositoryConfigurationDto>, ApiProblemResult>> CreateRepository(
        ApiBody<CreateRepositoryConfigurationDto> request, HttpContext context,
        [FromServices] IRepositoryConfigurationRepository repository, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var dto = request.Value!;
        var logger = loggerFactory.CreateLogger(typeof(RepositoriesEndpoints));
        Log.CreatingRepository(logger, dto.Name);

        var createResult = RepositoryConfiguration.Create(
            Guid.NewGuid(),
            dto.Name,
            dto.Url,
            dto.Platform,
            dto.DefaultBranch,
            dto.AuthenticationMethod,
            dto.CredentialIdentifier,
            dto.Description);

        if (createResult.IsFailure)
        {
            return ValidationError(context, createResult.Error);
        }

        var saveResult = await repository.AddAsync(createResult.Value, ct);

        if (saveResult.IsFailure)
        {
            return InternalError(context, saveResult.Error);
        }

        var resultDto = MapToDto(saveResult.Value);
        return TypedResults.Created(ApiLocation.For(context.Request, $"/api/Repositories/{resultDto.Id}"), resultDto);
    }

    /// <summary>Update a repository configuration.</summary>
    public static async Task<Results<NoContent, ApiProblemResult>> UpdateRepository(
        Guid id, ApiBody<UpdateRepositoryConfigurationDto> request, HttpContext context,
        [FromServices] IRepositoryConfigurationRepository repository, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var dto = request.Value!;
        var logger = loggerFactory.CreateLogger(typeof(RepositoriesEndpoints));
        Log.UpdatingRepository(logger, id);

        // Fetch with change tracking enabled for proper optimistic concurrency
        var getResult = await repository.GetByIdTrackingAsync(id, ct);
        if (getResult.IsFailure)
        {
            return NotFound(context, getResult.Error);
        }

        var entity = getResult.Value;
        var updateResult = entity.Update(
            dto.Name,
            dto.Url,
            dto.DefaultBranch,
            dto.AuthenticationMethod,
            dto.CredentialIdentifier,
            dto.IsActive,
            dto.Description);

        if (updateResult.IsFailure)
        {
            return ValidationError(context, updateResult.Error);
        }

        var saveResult = await repository.UpdateAsync(entity, ct);

        if (saveResult.IsFailure)
        {
            if (saveResult.Error.Contains("modified by another", StringComparison.OrdinalIgnoreCase))
            {
                return ApiProblem.AsGiven(context, new ProblemDetails
                {
                    Title = "Conflict",
                    Detail = saveResult.Error,
                    Status = StatusCodes.Status409Conflict
                });
            }

            return InternalError(context, saveResult.Error);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Delete a repository configuration.</summary>
    public static async Task<Results<NoContent, ApiProblemResult>> DeleteRepository(
        Guid id, HttpContext context, [FromServices] IRepositoryConfigurationRepository repository,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(RepositoriesEndpoints));
        Log.DeletingRepository(logger, id);

        var result = await repository.DeleteAsync(id, ct);

        if (result.IsFailure)
        {
            return result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? NotFound(context, result.Error)
                : InternalError(context, result.Error);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Test connection to a repository.</summary>
    public static async Task<Results<Ok<TestConnectionResponse>, ApiJsonResult<TestConnectionResponse>, ApiProblemResult>>
        TestRepositoryConnection(
            Guid id, HttpContext context, [FromServices] IRepositoryConfigurationRepository repository,
            [FromServices] IGitConnectionTester gitConnectionTester, [FromServices] ILoggerFactory loggerFactory,
            CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(RepositoriesEndpoints));
        Log.TestingConnection(logger, id);

        var getResult = await repository.GetByIdAsync(id, ct);
        if (getResult.IsFailure)
        {
            return NotFound(context, getResult.Error);
        }

        var entity = getResult.Value;
        var result = await gitConnectionTester.TestConnectionAsync(
            entity.Url,
            entity.AuthenticationMethod,
            ct: ct);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(new TestConnectionResponse { Connected = true, Message = result.Value.Message });
        }

        return ApiJson.Of(new TestConnectionResponse { Connected = false, Message = result.Error }, StatusCodes.Status400BadRequest);
    }

    private static ApiProblemResult NotFound(HttpContext context, string detail) =>
        ApiProblem.AsGiven(context, new ProblemDetails
        {
            Title = "Not Found",
            Detail = detail,
            Status = StatusCodes.Status404NotFound
        });

    private static ApiProblemResult ValidationError(HttpContext context, string detail) =>
        ApiProblem.AsGiven(context, new ProblemDetails
        {
            Title = "Validation Error",
            Detail = detail,
            Status = StatusCodes.Status400BadRequest
        });

    private static ApiProblemResult InternalError(HttpContext context, string detail) =>
        ApiProblem.AsGiven(context, new ProblemDetails
        {
            Title = "Internal Error",
            Detail = detail,
            Status = StatusCodes.Status500InternalServerError
        });

    /// <summary>Maps a RepositoryConfiguration entity to a RepositoryConfigurationDto.</summary>
    private static RepositoryConfigurationDto MapToDto(RepositoryConfiguration entity) =>
        new()
        {
            Id = entity.Id,
            Name = entity.Name,
            Url = entity.Url,
            Platform = entity.Platform,
            DefaultBranch = entity.DefaultBranch,
            AuthenticationMethod = entity.AuthenticationMethod,
            CredentialIdentifier = entity.CredentialIdentifier,
            IsActive = entity.IsActive,
            CreatedAt = entity.CreatedAt,
            LastUsedAt = entity.LastUsedAt,
            Description = entity.Description
        };

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Fetching all repository configurations")]
        public static partial void FetchingAllRepositories(ILogger logger);

        [LoggerMessage(EventId = 101, Level = LogLevel.Information,
            Message = "Fetching repository configuration with ID {Id}")]
        public static partial void FetchingRepositoryById(ILogger logger, Guid id);

        [LoggerMessage(EventId = 102, Level = LogLevel.Information,
            Message = "Creating new repository configuration: {Name}")]
        public static partial void CreatingRepository(ILogger logger, string name);

        [LoggerMessage(EventId = 103, Level = LogLevel.Information,
            Message = "Updating repository configuration with ID {Id}")]
        public static partial void UpdatingRepository(ILogger logger, Guid id);

        [LoggerMessage(EventId = 104, Level = LogLevel.Information,
            Message = "Deleting repository configuration with ID {Id}")]
        public static partial void DeletingRepository(ILogger logger, Guid id);

        [LoggerMessage(EventId = 105, Level = LogLevel.Information,
            Message = "Testing connection to repository with ID {Id}")]
        public static partial void TestingConnection(ILogger logger, Guid id);
    }
}
