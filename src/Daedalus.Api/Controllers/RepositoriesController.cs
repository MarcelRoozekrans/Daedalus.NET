using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Services.Git;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Daedalus.Api.Controllers;

/// <summary>
///     Repository configuration API endpoints
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public sealed partial class RepositoriesController(
    IRepositoryConfigurationRepository repositoryConfigRepository,
    ILogger<RepositoriesController> logger,
    IGitConnectionTester gitConnectionTester) : ControllerBase
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Fetching all repository configurations")]
    private static partial void LogFetchingAllRepositories(ILogger logger);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information,
        Message = "Fetching repository configuration with ID {Id}")]
    private static partial void LogFetchingRepositoryById(ILogger logger, Guid id);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "Creating new repository configuration: {Name}")]
    private static partial void LogCreatingRepository(ILogger logger, string name);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information,
        Message = "Updating repository configuration with ID {Id}")]
    private static partial void LogUpdatingRepository(ILogger logger, Guid id);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information,
        Message = "Deleting repository configuration with ID {Id}")]
    private static partial void LogDeletingRepository(ILogger logger, Guid id);

    [LoggerMessage(EventId = 105, Level = LogLevel.Information,
        Message = "Testing connection to repository with ID {Id}")]
    private static partial void LogTestingConnection(ILogger logger, Guid id);

    /// <summary>
    ///     Get all repository configurations
    /// </summary>
    [Authorize(Policy = "Admin")]
    [HttpGet]
    [ProducesResponseType<IEnumerable<RepositoryConfigurationDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RepositoryConfigurationDto>>> GetAll(CancellationToken ct)
    {
        LogFetchingAllRepositories(logger);

        var result = await repositoryConfigRepository.GetAllAsync(ct);

        if (result.IsFailure)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal Error",
                Detail = result.Error,
                Status = StatusCodes.Status500InternalServerError
            });
        }

        var dtos = result.Value.Select(MapToDto);
        return Ok(dtos);
    }

    /// <summary>
    ///     Get a specific repository configuration by ID
    /// </summary>
    [Authorize(Policy = "Admin")]
    [HttpGet("{id:guid}")]
    [ProducesResponseType<RepositoryConfigurationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RepositoryConfigurationDto>> GetById(Guid id, CancellationToken ct)
    {
        LogFetchingRepositoryById(logger, id);

        var result = await repositoryConfigRepository.GetByIdAsync(id, ct);

        if (result.IsFailure)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Not Found",
                Detail = result.Error,
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(MapToDto(result.Value));
    }

    /// <summary>
    ///     Create a new repository configuration
    /// </summary>
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting("write-operations")]
    [HttpPost]
    [ProducesResponseType<RepositoryConfigurationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RepositoryConfigurationDto>> Create(
        CreateRepositoryConfigurationDto dto,
        CancellationToken ct)
    {
        LogCreatingRepository(logger, dto.Name);

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
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = createResult.Error,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var saveResult = await repositoryConfigRepository.AddAsync(createResult.Value, ct);

        if (saveResult.IsFailure)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal Error",
                Detail = saveResult.Error,
                Status = StatusCodes.Status500InternalServerError
            });
        }

        var resultDto = MapToDto(saveResult.Value);
        return CreatedAtAction(nameof(GetById), new { id = resultDto.Id }, resultDto);
    }

    /// <summary>
    ///     Update a repository configuration
    /// </summary>
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting("write-operations")]
    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, UpdateRepositoryConfigurationDto dto, CancellationToken ct)
    {
        LogUpdatingRepository(logger, id);

        // Fetch with change tracking enabled for proper optimistic concurrency
        var getResult = await repositoryConfigRepository.GetByIdTrackingAsync(id, ct);
        if (getResult.IsFailure)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Not Found",
                Detail = getResult.Error,
                Status = StatusCodes.Status404NotFound
            });
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
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = updateResult.Error,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var saveResult = await repositoryConfigRepository.UpdateAsync(entity, ct);

        if (saveResult.IsFailure)
        {
            if (saveResult.Error.Contains("modified by another", StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Conflict",
                    Detail = saveResult.Error,
                    Status = StatusCodes.Status409Conflict
                });
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal Error",
                Detail = saveResult.Error,
                Status = StatusCodes.Status500InternalServerError
            });
        }

        return NoContent();
    }

    /// <summary>
    ///     Delete a repository configuration
    /// </summary>
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting("write-operations")]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        LogDeletingRepository(logger, id);

        var result = await repositoryConfigRepository.DeleteAsync(id, ct);

        if (result.IsFailure)
        {
            if (result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Not Found",
                    Detail = result.Error,
                    Status = StatusCodes.Status404NotFound
                });
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal Error",
                Detail = result.Error,
                Status = StatusCodes.Status500InternalServerError
            });
        }

        return NoContent();
    }

    /// <summary>
    ///     Test connection to a repository
    /// </summary>
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting("write-operations")]
    [HttpPost("{id:guid}/test-connection")]
    [ProducesResponseType<TestConnectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TestConnectionResponse>> TestConnection(Guid id, CancellationToken ct)
    {
        LogTestingConnection(logger, id);

        var getResult = await repositoryConfigRepository.GetByIdAsync(id, ct);
        if (getResult.IsFailure)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Not Found",
                Detail = getResult.Error,
                Status = StatusCodes.Status404NotFound
            });
        }

        var entity = getResult.Value;
        var result = await gitConnectionTester.TestConnectionAsync(
            entity.Url,
            entity.AuthenticationMethod,
            ct: ct);

        if (result.IsSuccess)
        {
            return Ok(new TestConnectionResponse { Connected = true, Message = result.Value.Message });
        }

        return BadRequest(new TestConnectionResponse { Connected = false, Message = result.Error });
    }

    /// <summary>
    ///     Maps a RepositoryConfiguration entity to a RepositoryConfigurationDto.
    /// </summary>
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
}
