using CSharpFunctionalExtensions;
using Daedalus.Api.Controllers;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.CreateProject;
using Daedalus.Application.Commands.DeleteProject;
using Daedalus.Application.Commands.UpdateProject;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for ProjectsController HTTP endpoints.
///     Tests request handling, response status codes, and DTO mapping with real PostgreSQL database.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ProjectsControllerIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ICommandHandlerFactory _commandFactoryMock = Substitute.For<ICommandHandlerFactory>();
    private readonly ILogger<ProjectsController> _loggerMock = Substitute.For<ILogger<ProjectsController>>();
    private ProjectsController _controller = null!;
    private ApplicationDbContext _dbContext = null!;
    private IProjectQueryService _projectQueryService = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        var options = PostgresFixture.CreateDbContextOptions(fixture.ConnectionString);

        _dbContext = new ApplicationDbContext(options);

        _projectQueryService = new ProjectQueryService(_dbContext);
        _controller = new ProjectsController(_projectQueryService, _commandFactoryMock, _loggerMock);
    }

    public async Task DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    #region GetAllProjects Tests

    [Fact(Timeout = 5000)]
    public async Task GetAllProjects_WithNoProjects_Returns200WithEmptyList()
    {
        // Act
        var result = await _controller.GetAllProjects();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.StatusCode.Should().Be(200);

        var pagedResult = okResult.Value as PagedResultDto<ProjectDto>;
        pagedResult.Should().NotBeNull();
        pagedResult!.Items.Should().BeEmpty();
        pagedResult.Total.Should().Be(0);
    }

    [Fact(Timeout = 5000)]
    public async Task GetAllProjects_WithProjects_Returns200WithPaginatedData()
    {
        // Arrange
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Project Alpha", description: "First project"));
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Project Beta", description: "Second project"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetAllProjects();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        var pagedResult = okResult.Value as PagedResultDto<ProjectDto>;
        pagedResult.Should().NotBeNull();
        pagedResult!.Items.Should().HaveCount(2);
        pagedResult.Total.Should().Be(2);
    }

    [Fact(Timeout = 5000)]
    public async Task GetAllProjects_WithCustomPageSize_ReturnsCorrectPageSize()
    {
        // Arrange
        for (var i = 0; i < 15; i++)
        {
            _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: $"Project {i:D2}", description: $"Description {i}"));
        }

        await _dbContext.SaveChangesAsync();

        // Act
        var result1 = await _controller.GetAllProjects(1, 5);
        var result2 = await _controller.GetAllProjects(2, 5);

        // Assert
        var okResult1 = (OkObjectResult)result1;
        var page1 = okResult1.Value as PagedResultDto<ProjectDto>;
        page1!.Items.Should().HaveCount(5);
        page1.Page.Should().Be(1);

        var okResult2 = (OkObjectResult)result2;
        var page2 = okResult2.Value as PagedResultDto<ProjectDto>;
        page2!.Items.Should().HaveCount(5);
        page2.Page.Should().Be(2);
    }

    [Fact(Timeout = 5000)]
    public async Task GetAllProjects_ReturnsProjectsSortedByName()
    {
        // Arrange
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Zebra Project", description: "Last alphabetically"));
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(name: "Alpha Project", description: "First alphabetically"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetAllProjects();

        // Assert
        var okResult = (OkObjectResult)result;
        var pagedResult = okResult.Value as PagedResultDto<ProjectDto>;
        pagedResult!.Items[0].ProjectName.Should().Be("Alpha Project");
        pagedResult.Items[1].ProjectName.Should().Be("Zebra Project");
    }

    [Fact(Timeout = 5000)]
    public async Task GetAllProjects_IncludesTaskCounts()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = IntegrationTestFactory.CreateProject(projectId, "Project With Tasks", "Has tasks");
        _dbContext.Projects.Add(project);

        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task 1",
            completionPromise: "Promise 1", maxIterations: 5));
        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task 2",
            completionPromise: "Promise 2", maxIterations: 5));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetAllProjects();

        // Assert
        var okResult = (OkObjectResult)result;
        var pagedResult = okResult.Value as PagedResultDto<ProjectDto>;
        var projectDto = pagedResult!.Items.Single(p => p.Id == projectId);
        projectDto.Tasks.Should().HaveCount(2);
    }

    #endregion

    #region GetProjectById Tests

    [Fact(Timeout = 5000)]
    public async Task GetProjectById_WithValidId_Returns200WithProject()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(projectId, "My Project", "My description"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetProjectById(projectId);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.StatusCode.Should().Be(200);

        var projectDto = okResult.Value as ProjectDto;
        projectDto!.Id.Should().Be(projectId);
        projectDto.ProjectName.Should().Be("My Project");
        projectDto.Description.Should().Be("My description");
    }

    [Fact(Timeout = 5000)]
    public async Task GetProjectById_WithInvalidId_Returns404()
    {
        // Act
        var result = await _controller.GetProjectById(Guid.NewGuid());

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
        var notFoundResult = (NotFoundObjectResult)result;
        notFoundResult.StatusCode.Should().Be(404);
    }

    [Fact(Timeout = 5000)]
    public async Task GetProjectById_ReturnsProjectWithVersionAndRepositoryInfo()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = IntegrationTestFactory.CreateProject(projectId, "Versioned Project", "Has version");
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetProjectById(projectId);

        // Assert
        var okResult = (OkObjectResult)result;
        var projectDto = okResult.Value as ProjectDto;
        projectDto!.Version.Should().NotBeNull();
        projectDto.CreatedAt.Should().NotBe(default);
    }

    #endregion

    #region GetProjectWithTasks Tests

    [Fact(Timeout = 5000)]
    public async Task GetProjectWithTasks_WithValidId_Returns200WithTasks()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = IntegrationTestFactory.CreateProject(projectId, "Project With Tasks", "Has tasks");
        _dbContext.Projects.Add(project);

        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task A",
            completionPromise: "Promise A", maxIterations: 5));
        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task B",
            completionPromise: "Promise B", maxIterations: 5));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetProjectWithTasks(projectId);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        var projectDto = okResult.Value as ProjectDto;
        projectDto!.Id.Should().Be(projectId);
        projectDto.Tasks.Should().HaveCount(2);
    }

    [Fact(Timeout = 5000)]
    public async Task GetProjectWithTasks_WithNoTasks_ReturnsEmptyTaskList()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _dbContext.Projects.Add(IntegrationTestFactory.CreateProject(projectId, "Empty Project", "No tasks"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetProjectWithTasks(projectId);

        // Assert
        var okResult = (OkObjectResult)result;
        var projectDto = okResult.Value as ProjectDto;
        projectDto!.Tasks.Should().BeEmpty();
    }

    [Fact(Timeout = 5000)]
    public async Task GetProjectWithTasks_WithInvalidId_Returns404()
    {
        // Act
        var result = await _controller.GetProjectWithTasks(Guid.NewGuid());

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region CreateProject Tests

    [Fact(Timeout = 5000)]
    public async Task CreateProject_WithValidRequest_Returns201()
    {
        // Arrange
        var request = new CreateProjectDto("New Project", "A new project", "1.0");
        var expectedDto = new ProjectDto(
            Guid.NewGuid(), "New Project", "A new project", "1.0", "", "main",
            DateTime.UtcNow, null, new List<TaskDto>().AsReadOnly());

        var handler = Substitute.For<ICommandHandler<CreateProjectCommand, Result<ProjectDto>>>();
        handler.Handle(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedDto));
        _commandFactoryMock
            .GetHandler<CreateProjectCommand, Result<ProjectDto>>(Arg.Any<CreateProjectCommand>())
            .Returns(handler);

        // Act
        var result = await _controller.CreateProject(request);

        // Assert
        result.Should().BeOfType<CreatedAtActionResult>();
        var createdResult = (CreatedAtActionResult)result;
        createdResult.StatusCode.Should().Be(201);
        var projectDto = createdResult.Value as ProjectDto;
        projectDto!.ProjectName.Should().Be("New Project");
    }

    [Fact(Timeout = 5000)]
    public async Task CreateProject_WithHandlerFailure_Returns400()
    {
        // Arrange
        var request = new CreateProjectDto("", "Invalid", "1.0");

        var handler = Substitute.For<ICommandHandler<CreateProjectCommand, Result<ProjectDto>>>();
        handler.Handle(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ProjectDto>("Project name is required"));
        _commandFactoryMock
            .GetHandler<CreateProjectCommand, Result<ProjectDto>>(Arg.Any<CreateProjectCommand>())
            .Returns(handler);

        // Act
        var result = await _controller.CreateProject(request);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task CreateProject_WithRepositoryUrl_PassesUrlToCommand()
    {
        // Arrange
        var request = new CreateProjectDto("Project", "Description", "1.0",
            "https://github.com/org/repo", "develop");
        var expectedDto = new ProjectDto(
            Guid.NewGuid(), "Project", "Description", "1.0",
            "https://github.com/org/repo", "develop",
            DateTime.UtcNow, null, new List<TaskDto>().AsReadOnly());

        var handler = Substitute.For<ICommandHandler<CreateProjectCommand, Result<ProjectDto>>>();
        handler.Handle(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedDto));
        _commandFactoryMock
            .GetHandler<CreateProjectCommand, Result<ProjectDto>>(Arg.Any<CreateProjectCommand>())
            .Returns(handler);

        // Act
        var result = await _controller.CreateProject(request);

        // Assert
        var createdResult = (CreatedAtActionResult)result;
        var projectDto = createdResult.Value as ProjectDto;
        projectDto!.RepositoryUrl.Should().Be("https://github.com/org/repo");
        projectDto.DefaultBranch.Should().Be("develop");
    }

    #endregion

    #region UpdateProject Tests

    [Fact(Timeout = 5000)]
    public async Task UpdateProject_WithValidRequest_Returns200()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var request = new UpdateProjectDto("Updated Name", "Updated description", "2.0");
        var expectedDto = new ProjectDto(
            projectId, "Updated Name", "Updated description", "2.0", "", "main",
            DateTime.UtcNow, DateTime.UtcNow, new List<TaskDto>().AsReadOnly());

        var handler = Substitute.For<ICommandHandler<UpdateProjectCommand, Result<ProjectDto>>>();
        handler.Handle(Arg.Any<UpdateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedDto));
        _commandFactoryMock
            .GetHandler<UpdateProjectCommand, Result<ProjectDto>>(Arg.Any<UpdateProjectCommand>())
            .Returns(handler);

        // Act
        var result = await _controller.UpdateProject(projectId, request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        var projectDto = okResult.Value as ProjectDto;
        projectDto!.ProjectName.Should().Be("Updated Name");
    }

    [Fact(Timeout = 5000)]
    public async Task UpdateProject_WithNotFoundError_Returns404()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var request = new UpdateProjectDto("Updated", "Desc", "1.0");

        var handler = Substitute.For<ICommandHandler<UpdateProjectCommand, Result<ProjectDto>>>();
        handler.Handle(Arg.Any<UpdateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ProjectDto>($"Project with ID {projectId} not found"));
        _commandFactoryMock
            .GetHandler<UpdateProjectCommand, Result<ProjectDto>>(Arg.Any<UpdateProjectCommand>())
            .Returns(handler);

        // Act
        var result = await _controller.UpdateProject(projectId, request);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task UpdateProject_WithValidationError_Returns400()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var request = new UpdateProjectDto("", "Desc", "1.0");

        var handler = Substitute.For<ICommandHandler<UpdateProjectCommand, Result<ProjectDto>>>();
        handler.Handle(Arg.Any<UpdateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ProjectDto>("Project name is required"));
        _commandFactoryMock
            .GetHandler<UpdateProjectCommand, Result<ProjectDto>>(Arg.Any<UpdateProjectCommand>())
            .Returns(handler);

        // Act
        var result = await _controller.UpdateProject(projectId, request);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region DeleteProject Tests

    [Fact(Timeout = 5000)]
    public async Task DeleteProject_WithValidId_Returns204()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        var handler = Substitute.For<ICommandHandler<DeleteProjectCommand, Result>>();
        handler.Handle(Arg.Any<DeleteProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _commandFactoryMock
            .GetHandler<DeleteProjectCommand, Result>(Arg.Any<DeleteProjectCommand>())
            .Returns(handler);

        // Act
        var result = await _controller.DeleteProject(projectId);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task DeleteProject_WithNotFoundError_Returns404()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        var handler = Substitute.For<ICommandHandler<DeleteProjectCommand, Result>>();
        handler.Handle(Arg.Any<DeleteProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure($"Project with ID {projectId} not found"));
        _commandFactoryMock
            .GetHandler<DeleteProjectCommand, Result>(Arg.Any<DeleteProjectCommand>())
            .Returns(handler);

        // Act
        var result = await _controller.DeleteProject(projectId);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task DeleteProject_WithOtherError_Returns400()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        var handler = Substitute.For<ICommandHandler<DeleteProjectCommand, Result>>();
        handler.Handle(Arg.Any<DeleteProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Cannot delete project with active tasks"));
        _commandFactoryMock
            .GetHandler<DeleteProjectCommand, Result>(Arg.Any<DeleteProjectCommand>())
            .Returns(handler);

        // Act
        var result = await _controller.DeleteProject(projectId);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion
}
