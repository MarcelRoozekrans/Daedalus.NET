using Daedalus.Api.Controllers;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Services.Git;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for RepositoriesController endpoints.
///     Uses mocked IRepositoryConfigurationRepository and IGitConnectionTester.
/// </summary>
public class RepositoriesControllerIntegrationTests
{
    private readonly IRepositoryConfigurationRepository _repositoryMock =
        Substitute.For<IRepositoryConfigurationRepository>();

    private readonly IGitConnectionTester _connectionTesterMock = Substitute.For<IGitConnectionTester>();
    private readonly ILogger<RepositoriesController> _loggerMock = Substitute.For<ILogger<RepositoriesController>>();
    private readonly RepositoriesController _controller;

    public RepositoriesControllerIntegrationTests()
    {
        _controller = new RepositoriesController(_repositoryMock, _loggerMock, _connectionTesterMock);
    }

    #region GetAll

    [Fact(Timeout = 5000)]
    public async Task GetAll_WithEntities_Returns200()
    {
        // Arrange
        var entities = new List<RepositoryConfiguration>
        {
            IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 1"),
            IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 2")
        };
        _repositoryMock.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<RepositoryConfiguration>>(entities));

        // Act
        var result = await _controller.GetAll(CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result.Result!;
        ok.StatusCode.Should().Be(200);
    }

    [Fact(Timeout = 5000)]
    public async Task GetAll_WithNoEntities_ReturnsEmptyList()
    {
        // Arrange
        _repositoryMock.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<RepositoryConfiguration>>(new List<RepositoryConfiguration>()));

        // Act
        var result = await _controller.GetAll(CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetAll_WhenRepositoryFails_Returns500()
    {
        // Arrange
        _repositoryMock.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<RepositoryConfiguration>>("Database error"));

        // Act
        var result = await _controller.GetAll(CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region GetById

    [Fact(Timeout = 5000)]
    public async Task GetById_WithExistingId_Returns200()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Found Repo");
        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));

        // Act
        var result = await _controller.GetById(entity.Id, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result.Result!;
        var dto = ok.Value as RepositoryConfigurationDto;
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Found Repo");
    }

    [Fact(Timeout = 5000)]
    public async Task GetById_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RepositoryConfiguration>("not found"));

        // Act
        var result = await _controller.GetById(id, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region Create

    [Fact(Timeout = 5000)]
    public async Task Create_WithValidDto_Returns201()
    {
        // Arrange
        var dto = new CreateRepositoryConfigurationDto
        {
            Name = "New Repo",
            Url = "https://github.com/org/new-repo",
            Platform = "GitHub",
            DefaultBranch = "main"
        };

        _repositoryMock.AddAsync(Arg.Any<RepositoryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result.Success(callInfo.Arg<RepositoryConfiguration>()));

        // Act
        var result = await _controller.Create(dto, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var created = (CreatedAtActionResult)result.Result!;
        created.StatusCode.Should().Be(201);
        var returnedDto = created.Value as RepositoryConfigurationDto;
        returnedDto.Should().NotBeNull();
        returnedDto!.Name.Should().Be("New Repo");
    }

    [Fact(Timeout = 5000)]
    public async Task Create_WithInvalidPlatform_Returns400()
    {
        // Arrange
        var dto = new CreateRepositoryConfigurationDto
        {
            Name = "Repo",
            Url = "https://example.com/repo",
            Platform = "InvalidPlatform"
        };

        // Act
        var result = await _controller.Create(dto, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task Create_WhenSaveFails_Returns500()
    {
        // Arrange
        var dto = new CreateRepositoryConfigurationDto
        {
            Name = "Repo",
            Url = "https://github.com/org/repo",
            Platform = "GitHub"
        };

        _repositoryMock.AddAsync(Arg.Any<RepositoryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RepositoryConfiguration>("Database error"));

        // Act
        var result = await _controller.Create(dto, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region Update

    [Fact(Timeout = 5000)]
    public async Task Update_WithValidDto_Returns204()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Original");
        var dto = new UpdateRepositoryConfigurationDto
        {
            Name = "Updated",
            Url = "https://github.com/org/repo",
            DefaultBranch = "main",
            IsActive = true
        };

        _repositoryMock.GetByIdTrackingAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));
        _repositoryMock.UpdateAsync(entity, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _controller.Update(entity.Id, dto, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task Update_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        var dto = new UpdateRepositoryConfigurationDto
        {
            Name = "Updated",
            Url = "https://github.com/org/repo"
        };

        _repositoryMock.GetByIdTrackingAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RepositoryConfiguration>("not found"));

        // Act
        var result = await _controller.Update(id, dto, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task Update_WithConcurrencyConflict_Returns409()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Original");
        var dto = new UpdateRepositoryConfigurationDto
        {
            Name = "Updated",
            Url = "https://github.com/org/repo",
            DefaultBranch = "main",
            IsActive = true
        };

        _repositoryMock.GetByIdTrackingAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));
        _repositoryMock.UpdateAsync(entity, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("The repository was modified by another operation"));

        // Act
        var result = await _controller.Update(entity.Id, dto, CancellationToken.None);

        // Assert
        result.Should().BeOfType<ConflictObjectResult>();
        var conflict = (ConflictObjectResult)result;
        conflict.StatusCode.Should().Be(409);
    }

    [Fact(Timeout = 5000)]
    public async Task Update_WithInvalidName_Returns400()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Original");
        var dto = new UpdateRepositoryConfigurationDto
        {
            Name = "",
            Url = "https://github.com/org/repo",
            DefaultBranch = "main",
            IsActive = true
        };

        _repositoryMock.GetByIdTrackingAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));

        // Act
        var result = await _controller.Update(entity.Id, dto, CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region Delete

    [Fact(Timeout = 5000)]
    public async Task Delete_WithExistingId_Returns204()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.DeleteAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _controller.Delete(id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task Delete_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.DeleteAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Repository configuration not found"));

        // Act
        var result = await _controller.Delete(id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region TestConnection

    [Fact(Timeout = 5000)]
    public async Task TestConnection_WhenSuccessful_Returns200WithConnected()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(
            name: "Connectable", url: "https://github.com/org/repo");

        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));
        _connectionTesterMock.TestConnectionAsync(
                "https://github.com/org/repo", null, null, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GitConnectionResult
            {
                Connected = true,
                Message = "Connection successful"
            }));

        // Act
        var result = await _controller.TestConnection(entity.Id, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result.Result!;
        var response = ok.Value as TestConnectionResponse;
        response.Should().NotBeNull();
        response!.Connected.Should().BeTrue();
        response.Message.Should().Be("Connection successful");
    }

    [Fact(Timeout = 5000)]
    public async Task TestConnection_WhenFails_Returns400()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(
            name: "Unreachable", url: "https://github.com/org/unreachable");

        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(entity));
        _connectionTesterMock.TestConnectionAsync(
                "https://github.com/org/unreachable", null, null, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GitConnectionResult>("Connection refused"));

        // Act
        var result = await _controller.TestConnection(entity.Id, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task TestConnection_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RepositoryConfiguration>("not found"));

        // Act
        var result = await _controller.TestConnection(id, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion
}
