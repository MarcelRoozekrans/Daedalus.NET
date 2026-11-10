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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Project = Daedalus.Domain.Entities.Project;
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
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddDaedalusEndpoints().BuildServiceProvider();
    private SeedBatch _seed = null!;
    private IProjectQueryService _projectQueryService = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        _seed = new SeedBatch(fixture.Db, fixture.ConnectionString);

        _projectQueryService = new ProjectQueryService(fixture.Db, new DisabledWorkflowRunStatusReader());
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
    }

    #region GetAllProjects Tests

    [Fact(Timeout = 5000)]
    public async Task GetAllProjects_WithNoProjects_Returns200WithEmptyList()
    {
        // Act
        var result = (await GetAllProjects()).Result;

        // Assert
        // Red: return Error("x", 410) in place of TypedResults.Ok in GetAllProjects.
        result.Should().BeOfType<Ok<PagedResultDto<ProjectDto>>>();
        var okResult = (Ok<PagedResultDto<ProjectDto>>)result;
        okResult.StatusCode.Should().Be(200);

        var pagedResult = okResult.Value;
        pagedResult.Should().NotBeNull();
        pagedResult!.Items.Should().BeEmpty();
        pagedResult.Total.Should().Be(0);
    }

    [Fact(Timeout = 5000)]
    public async Task GetAllProjects_WithProjects_Returns200WithPaginatedData()
    {
        // Arrange
        _seed.Projects.Add(IntegrationTestFactory.CreateProject(name: "Project Alpha", description: "First project"));
        _seed.Projects.Add(IntegrationTestFactory.CreateProject(name: "Project Beta", description: "Second project"));
        await _seed.SaveChangesAsync();

        // Act
        var result = (await GetAllProjects()).Result;

        // Assert
        // Red: answer an empty PagedResultDto in place of the service page in GetAllProjects.
        result.Should().BeOfType<Ok<PagedResultDto<ProjectDto>>>();
        var okResult = (Ok<PagedResultDto<ProjectDto>>)result;
        var pagedResult = okResult.Value;
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
            _seed.Projects.Add(IntegrationTestFactory.CreateProject(name: $"Project {i:D2}", description: $"Description {i}"));
        }

        await _seed.SaveChangesAsync();

        // Act
        var result1 = (await GetAllProjects("?page=1&pageSize=5")).Result;
        var result2 = (await GetAllProjects("?page=2&pageSize=5")).Result;

        // Assert
        // Red: answer an empty PagedResultDto in place of the service page in GetAllProjects.
        var okResult1 = (Ok<PagedResultDto<ProjectDto>>)result1;
        var page1 = okResult1.Value;
        page1!.Items.Should().HaveCount(5);
        page1.Page.Should().Be(1);

        var okResult2 = (Ok<PagedResultDto<ProjectDto>>)result2;
        var page2 = okResult2.Value;
        page2!.Items.Should().HaveCount(5);
        page2.Page.Should().Be(2);
    }

    [Fact(Timeout = 5000)]
    public async Task GetAllProjects_ReturnsProjectsSortedByName()
    {
        // Arrange
        _seed.Projects.Add(IntegrationTestFactory.CreateProject(name: "Zebra Project", description: "Last alphabetically"));
        _seed.Projects.Add(IntegrationTestFactory.CreateProject(name: "Alpha Project", description: "First alphabetically"));
        await _seed.SaveChangesAsync();

        // Act
        var result = (await GetAllProjects()).Result;

        // Assert
        // Red: answer an empty PagedResultDto in place of the service page in GetAllProjects.
        var okResult = (Ok<PagedResultDto<ProjectDto>>)result;
        var pagedResult = okResult.Value;
        pagedResult!.Items[0].ProjectName.Should().Be("Alpha Project");
        pagedResult.Items[1].ProjectName.Should().Be("Zebra Project");
    }

    [Fact(Timeout = 5000)]
    public async Task GetAllProjects_IncludesTaskCounts()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = IntegrationTestFactory.CreateProject(projectId, "Project With Tasks", "Has tasks");
        _seed.Projects.Add(project);

        _seed.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task 1"));
        _seed.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task 2"));
        await _seed.SaveChangesAsync();

        // Act
        var result = (await GetAllProjects()).Result;

        // Assert
        // Red: answer an empty PagedResultDto in place of the service page in GetAllProjects.
        var okResult = (Ok<PagedResultDto<ProjectDto>>)result;
        var pagedResult = okResult.Value;
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
        _seed.Projects.Add(IntegrationTestFactory.CreateProject(projectId, "My Project", "My description"));
        await _seed.SaveChangesAsync();

        // Act
        var result = (await ProjectsEndpoints.GetProjectById(projectId, _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        // Red: return Error("x", 410) for a found project in GetProjectById.
        result.Should().BeOfType<Ok<ProjectDto>>();
        var okResult = (Ok<ProjectDto>)result;
        okResult.StatusCode.Should().Be(200);

        var projectDto = okResult.Value;
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
        // Red: return TypedResults.Ok(project!) for a null project in GetProjectById.
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact(Timeout = 5000)]
    public async Task GetProjectById_ReturnsProjectWithVersionAndRepositoryInfo()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = IntegrationTestFactory.CreateProject(projectId, "Versioned Project", "Has version");
        _seed.Projects.Add(project);
        await _seed.SaveChangesAsync();

        // Act
        var result = (await ProjectsEndpoints.GetProjectById(projectId, _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        // Red: return Error("x", 410) for a found project in GetProjectById.
        var okResult = (Ok<ProjectDto>)result;
        var projectDto = okResult.Value;
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
        _seed.Projects.Add(project);

        _seed.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task A"));
        _seed.Tasks.Add(IntegrationTestFactory.CreateTask(projectId: projectId, prompt: "Task B"));
        await _seed.SaveChangesAsync();

        // Act
        var result = (await ProjectsEndpoints.GetProjectWithTasks(projectId, _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        // Red: return Error("x", 410) for a found project in GetProjectWithTasks.
        result.Should().BeOfType<Ok<ProjectDto>>();
        var okResult = (Ok<ProjectDto>)result;
        var projectDto = okResult.Value;
        projectDto!.Id.Should().Be(projectId);
        projectDto.Tasks.Should().HaveCount(2);
    }

    [Fact(Timeout = 5000)]
    public async Task GetProjectWithTasks_WithNoTasks_ReturnsEmptyTaskList()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _seed.Projects.Add(IntegrationTestFactory.CreateProject(projectId, "Empty Project", "No tasks"));
        await _seed.SaveChangesAsync();

        // Act
        var result = (await ProjectsEndpoints.GetProjectWithTasks(projectId, _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        // Red: return Error("x", 410) for a found project in GetProjectWithTasks.
        var okResult = (Ok<ProjectDto>)result;
        var projectDto = okResult.Value;
        projectDto!.Tasks.Should().BeEmpty();
    }

    [Fact(Timeout = 5000)]
    public async Task GetProjectWithTasks_WithInvalidId_Returns404()
    {
        // Act
        var result = (await ProjectsEndpoints.GetProjectWithTasks(Guid.NewGuid(), _projectQueryService, _loggerFactory, CancellationToken.None)).Result;

        // Assert
        // Red: return TypedResults.Ok(project!) for a null project in GetProjectWithTasks.
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
        // Red: return Error("x", 400) in place of TypedResults.Created in CreateProject.
        result.Should().BeOfType<Created<ProjectDto>>();
        var createdResult = (Created<ProjectDto>)result;
        createdResult.StatusCode.Should().Be(201);
        var projectDto = createdResult.Value;
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
        // Red: answer 422 in place of 400 for a failed command in CreateProject.
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
        // Red: return Error("x", 400) in place of TypedResults.Created in CreateProject.
        var createdResult = (Created<ProjectDto>)result;
        var projectDto = createdResult.Value;
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
        // Red: return Error("x", 400) in place of TypedResults.Ok in UpdateProject.
        result.Should().BeOfType<Ok<ProjectDto>>();
        var okResult = (Ok<ProjectDto>)result;
        var projectDto = okResult.Value;
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
        // Red: IsNotFound answers false.
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
        // Red: answer 404 for every failed command in UpdateProject.
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    ///     Ruling R74, owner decision: an update that loses a race to another write of the project answers 409 Conflict
    ///     with the retry message, through the real handler and repository. Another write lands between the handler's
    ///     load and its save; the other write's value stands. Red: map only not-found to 404 and every other failure to
    ///     400 in UpdateProject, as before; the lost race answers 400.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UpdateProject_that_loses_a_race_returns_409_with_the_retry_message()
    {
        // Arrange
        var project = IntegrationTestFactory.CreateProject(name: "Original", description: "Original description");
        var projects = new ProjectRepository(fixture.Db, NullLogger<ProjectRepository>.Instance);
        (await projects.AddAsync(project, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var racing = new RacingProjectRepository(projects, fixture.ConnectionString);
        var handler = new UpdateProjectCommandHandler(racing, NullLogger<UpdateProjectCommandHandler>.Instance);
        _commandsMock
            .UpdateProjectAsync(Arg.Any<UpdateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<UpdateProjectCommand>(), call.Arg<CancellationToken>()));
        var request = new UpdateProjectDto("Loser", "Loser description", "2.0");

        // Act
        var result = (await ProjectsEndpoints.UpdateProject(project.Id, await BindUpdate(request), _commandsMock, CancellationToken.None)).Result;

        // Assert
        var json = result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        json.Value.Should().Be(new ErrorBody("The project was modified by another operation. Please retry."));
        var stored = (await projects.GetByIdAsync(project.Id, CancellationToken.None)).Value;
        stored.ProjectName.Should().Be("Winner");
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
        // Red: return Error("x", 400) in place of TypedResults.NoContent in DeleteProject.
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
        // Red: IsNotFound answers false.
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
        // Red: answer 404 for every failed command in DeleteProject.
        result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    #endregion

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private async Task<Results<Ok<PagedResultDto<ProjectDto>>, ApiJsonResult<ErrorBody>>> GetAllProjects(string queryString = "")
    {
        var (page, pageSize) = await PagingQueryBinding.BindAsync(new DefaultHttpContext(), queryString);
        return await ProjectsEndpoints.GetAllProjects(page, pageSize, _projectQueryService, _loggerFactory, CancellationToken.None);
    }

    private DefaultHttpContext CreateContext(string? body = null)
    {
        var context = new DefaultHttpContext { RequestServices = _services };
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

    private Task<ApiBody<CreateProjectDto>> BindCreate(CreateProjectDto request) => Bind<CreateProjectDto>(request);

    private Task<ApiBody<UpdateProjectDto>> BindUpdate(UpdateProjectDto request) => Bind<UpdateProjectDto>(request);

    /// <summary>Binds <paramref name="dto"/> as the request delegate binds the body, through its JSON text.</summary>
    private async Task<ApiBody<T>> Bind<T>(T dto)
        where T : class
    {
        var context = CreateContext(JsonSerializer.Serialize(dto, WebJson));
        var parameter = typeof(ProjectsEndpoints).GetMethods().SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(ApiBody<T>));
        return await BindVia<ApiBody<T>>(context, parameter);
    }

    private static async Task<TSelf> BindVia<TSelf>(HttpContext context, System.Reflection.ParameterInfo parameter)
        where TSelf : class, IBindableFromHttpContext<TSelf> => (await TSelf.BindAsync(context, parameter))!;
    /// <summary>
    ///     Loads through the real repository, then renames the project with a separate connection, the other write
    ///     that wins the race before the caller's save.
    /// </summary>
    private sealed class RacingProjectRepository(IProjectRepository inner, string connectionString) : IProjectRepository
    {
        public async Task<Result<Project>> GetByIdAsync(Guid id, CancellationToken ct)
        {
            var loaded = await inner.GetByIdAsync(id, ct);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                """UPDATE "Projects" SET "ProjectName" = 'Winner' WHERE "Id" = @id""", connection);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(ct);
            return loaded;
        }

        public Task<Result<Project>> AddAsync(Project project, CancellationToken ct) => inner.AddAsync(project, ct);

        public Task<Result> UpdateAsync(Project project, CancellationToken ct) => inner.UpdateAsync(project, ct);

        public Task<Result> DeleteAsync(Guid id, CancellationToken ct) => inner.DeleteAsync(id, ct);
    }
}
