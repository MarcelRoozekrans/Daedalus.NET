using System.Text;
using System.Text.Json;
using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.CreateProject;
using Daedalus.Application.Commands.DeleteProject;
using Daedalus.Application.Commands.UpdateProject;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Integration.Fixtures;
using Daedalus.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for ProjectsEndpoints handlers.
///     Tests request handling, response status codes, and DTO mapping with real PostgreSQL database.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ProjectsEndpointsIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly IApplicationCommands _commandsMock = Substitute.For<IApplicationCommands>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private ApplicationDbContext _dbContext = null!;
    private IProjectQueryService _projectQueryService = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        var options = PostgresFixture.CreateDbContextOptions(fixture.ConnectionString);

        _dbContext = new ApplicationDbContext(options);

        _projectQueryService = new ProjectQueryService(_dbContext, new DisabledWorkflowRunStatusReader());
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
        var result = (await GetAllProjects()).Result;

        // Assert
        result.Should().BeOfType<Ok<PagedResultDto<ProjectDto>>>();
        var okResult = (Ok<PagedResultDto<ProjectDto>>)result;
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
        var result = (await GetAllProjects()).Result;

        // Assert
        result.Should().BeOfType<Ok<PagedResultDto<ProjectDto>>>();
        var okResult = (Ok<PagedResultDto<ProjectDto>>)result;
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
        var result1 = (await GetAllProjects("?page=1&pageSize=5")).Result;
        var result2 = (await GetAllProjects("?page=2&pageSize=5")).Result;

        // Assert
        var okResult1 = (Ok<PagedResultDto<ProjectDto>>)result1;
        var page1 = okResult1.Value as PagedResultDto<ProjectDto>;
        page1!.Items.Should().HaveCount(5);
        page1.Page.Should().Be(1);

        var okResult2 = (Ok<PagedResultDto<ProjectDto>>)result2;
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
        var result = (await GetAllProjects()).Result;

        // Assert
        var okResult = (Ok<PagedResultDto<ProjectDto>>)result;
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

        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task 1"));
        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task 2"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = (await GetAllProjects()).Result;

        // Assert
        var okResult = (Ok<PagedResultDto<ProjectDto>>)result;
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
        var result = (await ProjectsEndpoints.GetProjectById(projectId, _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<Ok<ProjectDto>>();
        var okResult = (Ok<ProjectDto>)result;
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
        var result = (await ProjectsEndpoints.GetProjectById(Guid.NewGuid(), _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
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
        var result = (await ProjectsEndpoints.GetProjectById(projectId, _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        var okResult = (Ok<ProjectDto>)result;
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

        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task A"));
        _dbContext.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task B"));
        await _dbContext.SaveChangesAsync();

        // Act
        var result = (await ProjectsEndpoints.GetProjectWithTasks(projectId, _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<Ok<ProjectDto>>();
        var okResult = (Ok<ProjectDto>)result;
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
        var result = (await ProjectsEndpoints.GetProjectWithTasks(projectId, _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        var okResult = (Ok<ProjectDto>)result;
        var projectDto = okResult.Value as ProjectDto;
        projectDto!.Tasks.Should().BeEmpty();
    }

    [Fact(Timeout = 5000)]
    public async Task GetProjectWithTasks_WithInvalidId_Returns404()
    {
        // Act
        var result = (await ProjectsEndpoints.GetProjectWithTasks(Guid.NewGuid(), _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
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

        _commandsMock
            .CreateProjectAsync(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Success(expectedDto));

        // Act
        var result = (await ProjectsEndpoints.CreateProject(await BindCreate(request), CreateContext(), _commandsMock, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<Created<ProjectDto>>();
        var createdResult = (Created<ProjectDto>)result;
        createdResult.StatusCode.Should().Be(201);
        var projectDto = createdResult.Value as ProjectDto;
        projectDto!.ProjectName.Should().Be("New Project");
    }

    [Fact(Timeout = 5000)]
    public async Task CreateProject_WithHandlerFailure_Returns400()
    {
        // Arrange
        var request = new CreateProjectDto("", "Invalid", "1.0");

        _commandsMock
            .CreateProjectAsync(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Failure("Project name is required"));

        // Act
        var result = (await ProjectsEndpoints.CreateProject(await BindCreate(request), CreateContext(), _commandsMock, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
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

        _commandsMock
            .CreateProjectAsync(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Success(expectedDto));

        // Act
        var result = (await ProjectsEndpoints.CreateProject(await BindCreate(request), CreateContext(), _commandsMock, CancellationToken.None)).Result;

        // Assert
        var createdResult = (Created<ProjectDto>)result;
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

        _commandsMock
            .UpdateProjectAsync(Arg.Any<UpdateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Success(expectedDto));

        // Act
        var result = (await ProjectsEndpoints.UpdateProject(projectId, await BindUpdate(request), _commandsMock, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<Ok<ProjectDto>>();
        var okResult = (Ok<ProjectDto>)result;
        var projectDto = okResult.Value as ProjectDto;
        projectDto!.ProjectName.Should().Be("Updated Name");
    }

    [Fact(Timeout = 5000)]
    public async Task UpdateProject_WithNotFoundError_Returns404()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var request = new UpdateProjectDto("Updated", "Desc", "1.0");

        _commandsMock
            .UpdateProjectAsync(Arg.Any<UpdateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Failure($"Project with ID {projectId} not found"));

        // Act
        var result = (await ProjectsEndpoints.UpdateProject(projectId, await BindUpdate(request), _commandsMock, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact(Timeout = 5000)]
    public async Task UpdateProject_WithValidationError_Returns400()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var request = new UpdateProjectDto("", "Desc", "1.0");

        _commandsMock
            .UpdateProjectAsync(Arg.Any<UpdateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Failure("Project name is required"));

        // Act
        var result = (await ProjectsEndpoints.UpdateProject(projectId, await BindUpdate(request), _commandsMock, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    #endregion

    #region DeleteProject Tests

    [Fact(Timeout = 5000)]
    public async Task DeleteProject_WithValidId_Returns204()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        _commandsMock
            .DeleteProjectAsync(Arg.Any<DeleteProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = (await ProjectsEndpoints.DeleteProject(projectId, _commandsMock, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<NoContent>();
    }

    [Fact(Timeout = 5000)]
    public async Task DeleteProject_WithNotFoundError_Returns404()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        _commandsMock
            .DeleteProjectAsync(Arg.Any<DeleteProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure($"Project with ID {projectId} not found"));

        // Act
        var result = (await ProjectsEndpoints.DeleteProject(projectId, _commandsMock, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact(Timeout = 5000)]
    public async Task DeleteProject_WithOtherError_Returns400()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        _commandsMock
            .DeleteProjectAsync(Arg.Any<DeleteProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Cannot delete project with active tasks"));

        // Act
        var result = (await ProjectsEndpoints.DeleteProject(projectId, _commandsMock, CancellationToken.None)).Result;

        // Assert
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    #endregion

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private async Task<Results<Ok<PagedResultDto<ProjectDto>>, ApiJsonResult<ErrorBody>>> GetAllProjects(string queryString = "")
    {
        var (page, pageSize) = await PagingQueryBinding.BindAsync(new DefaultHttpContext(), queryString);
        return await ProjectsEndpoints.GetAllProjects(page, pageSize, _projectQueryService, _loggerFactory, CancellationToken.None);
    }

    private static DefaultHttpContext CreateContext(string? body = null)
    {
        var services = new ServiceCollection().AddLogging().AddDaedalusEndpoints().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
        }

        return context;
    }

    private static Task<ApiBody<CreateProjectDto>> BindCreate(CreateProjectDto request) => Bind<CreateProjectDto>(request);

    private static Task<ApiBody<UpdateProjectDto>> BindUpdate(UpdateProjectDto request) => Bind<UpdateProjectDto>(request);

    /// <summary>Binds <paramref name="dto"/> as the request delegate binds the body, through its JSON text.</summary>
    private static async Task<ApiBody<T>> Bind<T>(T dto)
        where T : class
    {
        var context = CreateContext(JsonSerializer.Serialize(dto, WebJson));
        var parameter = typeof(ProjectsEndpoints).GetMethods().SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(ApiBody<T>));
        return await BindVia<ApiBody<T>>(context, parameter);
    }

    private static async Task<TSelf> BindVia<TSelf>(HttpContext context, System.Reflection.ParameterInfo parameter)
        where TSelf : class, IBindableFromHttpContext<TSelf> => (await TSelf.BindAsync(context, parameter))!;
}
