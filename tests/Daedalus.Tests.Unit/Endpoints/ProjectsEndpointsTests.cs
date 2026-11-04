using System.Reflection;
using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.CreateProject;
using Daedalus.Application.Commands.UpdateProject;
using Daedalus.Application.DTOs;
using Daedalus.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="ProjectsEndpoints"/> answers what <c>ProjectsController</c> answered: reads call straight through,
///     a missing project is a 404 <c>{ "error" }</c>, a created project is a 201 whose <c>Location</c> keeps MVC's
///     <c>/api/Projects</c> casing, a failed command is a 400 or a 404 carrying its error, and a throwing read is
///     logged and answered as a 500.
/// </summary>
public sealed class ProjectsEndpointsTests
{
    private const string Category = "Daedalus.Api.Endpoints.ProjectsEndpoints";

    private static readonly Guid Id = Guid.NewGuid();
    private static readonly ProjectDto Project = new(
        Id, "P", "D", "1.0", "", "main", DateTime.UnixEpoch, null, new List<TaskDto>().AsReadOnly());

    private readonly IProjectQueryService _queries = Substitute.For<IProjectQueryService>();
    private readonly IApplicationCommands _commands = Substitute.For<IApplicationCommands>();

    [Fact]
    public async Task GetAllProjects_passes_the_sent_paging_and_returns_the_page()
    {
        var page = new PagedResultDto<ProjectDto>([Project], 1, 2, 5);
        _queries.GetAllAsync(2, 5, Arg.Any<CancellationToken>()).Returns(page);
        await using var services = Services();
        var (pageQuery, pageSize) = await PagingQueryBinding.BindAsync(Create(services), "?page=2&pageSize=5");

        var result = await ProjectsEndpoints.GetAllProjects(
            pageQuery, pageSize, _queries, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: pass 1 and 10 in place of page.ValueOr and pageSize.ValueOr in GetAllProjects; the stub for 2 and 5 is not met.
        result.Result.Should().BeOfType<Ok<PagedResultDto<ProjectDto>>>().Which.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task GetAllProjects_without_paging_asks_for_page_one_of_ten()
    {
        _queries.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResultDto<ProjectDto>([], 0, 1, 10));
        await using var services = Services();
        var (pageQuery, pageSize) = await PagingQueryBinding.BindAsync(Create(services), string.Empty);

        await ProjectsEndpoints.GetAllProjects(
            pageQuery, pageSize, _queries, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: use page.Value and pageSize.Value in GetAllProjects; the service is asked for 0 and 0.
        await _queries.Received(1).GetAllAsync(1, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetProjectById_returns_the_project()
    {
        _queries.GetByIdAsync(Id, Arg.Any<CancellationToken>()).Returns(Project);

        var result = await ProjectsEndpoints.GetProjectById(
            Id, _queries, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer a 404 in place of Ok in the found branch of GetProjectById.
        result.Result.Should().BeOfType<Ok<ProjectDto>>().Which.Value.Should().BeSameAs(Project);
    }

    [Fact]
    public async Task GetProjectById_for_a_missing_project_answers_a_404_error_body()
    {
        var result = await ProjectsEndpoints.GetProjectById(
            Id, _queries, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer a bodiless 404 or status 400 in GetProjectById.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: change the text "Project with ID {id} not found" in GetProjectById.
        json.Value.Should().Be(new ErrorBody($"Project with ID {Id} not found"));
    }

    [Fact]
    public async Task GetProjectWithTasks_returns_the_project_and_404s_a_missing_one()
    {
        _queries.GetWithTasksAsync(Id, Arg.Any<CancellationToken>()).Returns(Project);
        var missing = Guid.NewGuid();

        var found = await ProjectsEndpoints.GetProjectWithTasks(
            Id, _queries, Substitute.For<ILoggerFactory>(), CancellationToken.None);
        var notFound = await ProjectsEndpoints.GetProjectWithTasks(
            missing, _queries, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: call GetByIdAsync in place of GetWithTasksAsync in GetProjectWithTasks; the stub is not met.
        found.Result.Should().BeOfType<Ok<ProjectDto>>().Which.Value.Should().BeSameAs(Project);
        // Red: answer a 200 in place of 404 for a null project in GetProjectWithTasks.
        var json = notFound.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        json.Value.Should().Be(new ErrorBody($"Project with ID {missing} not found"));
    }

    [Fact]
    public async Task GetAllProjects_answers_a_logged_500_error_body_when_the_service_throws()
    {
        _queries.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<PagedResultDto<ProjectDto>>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();
        await using var services = Services();
        var (pageQuery, pageSize) = await PagingQueryBinding.BindAsync(Create(services), string.Empty);

        var result = await ProjectsEndpoints.GetAllProjects(pageQuery, pageSize, _queries, factory, CancellationToken.None);

        // Red: answer 502 in InternalServerError, the helper all three catches use; the three 500 tests fail.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        json.Value.Should().Be(new ErrorBody("Internal server error"));
        // Red: drop Log.ErrorRetrievingProjects in the catch of GetAllProjects, or change EventId 100 to 103.
        LoggedEventId(logger).Id.Should().Be(100);
        // Red: pass the category "Other" to CreateLogger in the catch of GetAllProjects.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GetProjectById_answers_a_logged_500_error_body_when_the_service_throws()
    {
        _queries.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProjectDto?>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();

        var result = await ProjectsEndpoints.GetProjectById(Id, _queries, factory, CancellationToken.None);

        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: drop Log.ErrorRetrievingProject in the catch of GetProjectById, or change EventId 101 to 103.
        LoggedEventId(logger).Id.Should().Be(101);
        // Red: pass the category "Other" to CreateLogger in the catch of GetProjectById.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GetProjectWithTasks_answers_a_logged_500_error_body_when_the_service_throws()
    {
        _queries.GetWithTasksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProjectDto?>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();

        var result = await ProjectsEndpoints.GetProjectWithTasks(Id, _queries, factory, CancellationToken.None);

        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: drop Log.ErrorRetrievingProjectWithTasks in the catch of GetProjectWithTasks, or change EventId 102 to 103.
        LoggedEventId(logger).Id.Should().Be(102);
        // Red: pass the category "Other" to CreateLogger in the catch of GetProjectWithTasks.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task CreateProject_answers_201_with_the_absolute_class_cased_location_and_defaults_the_optional_members()
    {
        _commands.CreateProjectAsync(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Success(Project));
        await using var services = Services();
        var context = WithJsonBody(services, "{\"projectName\":\"P\",\"description\":null,\"version\":null}");
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.com");
        var request = await BindAsync<ApiBody<CreateProjectDto>>(context, Parameter<ApiBody<CreateProjectDto>>("request"));

        var result = await ProjectsEndpoints.CreateProject(request, context, _commands, CancellationToken.None);

        // Red: answer Ok in place of Created in the success branch of CreateProject.
        var created = result.Result.Should().BeOfType<Created<ProjectDto>>().Subject;
        created.Value.Should().BeSameAs(Project);
        // Red: build the location as /api/projects/{id} (lower case), or without the host.
        created.Location.Should().Be($"https://example.com/api/Projects/{Id}");
        // Red: pass description and version through without the string.Empty and "1.0" defaults in CreateProject.
        await _commands.Received(1).CreateProjectAsync(
            Arg.Is<CreateProjectCommand>(command => command.ProjectName == "P"
                && command.Description == string.Empty && command.Version == "1.0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateProject_answers_400_with_the_error_when_the_command_fails()
    {
        _commands.CreateProjectAsync(Arg.Any<CreateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Failure("Project name is required"));
        await using var services = Services();
        var context = WithJsonBody(services, "{\"projectName\":\"\",\"description\":\"d\",\"version\":\"1.0\"}");
        var request = await BindAsync<ApiBody<CreateProjectDto>>(context, Parameter<ApiBody<CreateProjectDto>>("request"));

        var result = await ProjectsEndpoints.CreateProject(request, context, _commands, CancellationToken.None);

        // Red: answer 422 in place of 400, or drop result.Error from the body, in the failure branch of CreateProject.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        json.Value.Should().Be(new ErrorBody("Project name is required"));
    }

    [Fact]
    public async Task UpdateProject_returns_the_updated_project_and_passes_the_route_id()
    {
        _commands.UpdateProjectAsync(Arg.Any<UpdateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Success(Project));
        var request = await BindUpdate();

        var result = await ProjectsEndpoints.UpdateProject(Id, request, _commands, CancellationToken.None);

        // Red: answer a 400 in place of Ok in the success branch of UpdateProject.
        result.Result.Should().BeOfType<Ok<ProjectDto>>().Which.Value.Should().BeSameAs(Project);
        // Red: pass Guid.Empty in place of id to UpdateProjectCommand in UpdateProject.
        await _commands.Received(1).UpdateProjectAsync(
            Arg.Is<UpdateProjectCommand>(command => command.Id == Id && command.ProjectName == "N"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Project with ID x not found", StatusCodes.Status404NotFound)]
    [InlineData("PROJECT NOT FOUND", StatusCodes.Status404NotFound)]
    [InlineData("Project name is required", StatusCodes.Status400BadRequest)]
    public async Task UpdateProject_maps_a_failed_command_to_404_or_400(string error, int status)
    {
        _commands.UpdateProjectAsync(Arg.Any<UpdateProjectCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProjectDto>.Failure(error));
        var request = await BindUpdate();

        var result = await ProjectsEndpoints.UpdateProject(Id, request, _commands, CancellationToken.None);

        // Red: drop StringComparison.OrdinalIgnoreCase in IsNotFound, or map every failure to 400 in UpdateProject.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(status);
        json.Value.Should().Be(new ErrorBody(error));
    }

    [Fact]
    public async Task DeleteProject_answers_204_on_success()
    {
        _commands.DeleteProjectAsync(default, default).ReturnsForAnyArgs(Result.Success());

        var result = await ProjectsEndpoints.DeleteProject(Id, _commands, CancellationToken.None);

        // Red: answer a 400 in place of NoContent in the success branch of DeleteProject.
        result.Result.Should().BeOfType<NoContent>();
    }

    [Fact]
    public async Task DeleteProject_maps_any_other_failure_to_400_carrying_the_error()
    {
        _commands.DeleteProjectAsync(default, default).ReturnsForAnyArgs(Result.Failure("Cannot delete"));

        var result = await ProjectsEndpoints.DeleteProject(Id, _commands, CancellationToken.None);

        // Red: map the final failure to 404 in place of 400 in DeleteProject.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        json.Value.Should().Be(new ErrorBody("Cannot delete"));
    }

    private static async Task<ApiBody<UpdateProjectDto>> BindUpdate()
    {
        await using var services = Services();
        var context = WithJsonBody(services, "{\"projectName\":\"N\",\"description\":\"d\",\"version\":\"2.0\"}");
        return await BindAsync<ApiBody<UpdateProjectDto>>(context, Parameter<ApiBody<UpdateProjectDto>>("request"));
    }

    private static ParameterInfo Parameter<T>(string name) =>
        typeof(ProjectsEndpoints).GetMethods().SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(T) && string.Equals(p.Name, name, StringComparison.Ordinal));

    private static (ILoggerFactory Factory, ILogger Logger) CreateLogging()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        return (factory, logger);
    }

    private static EventId LoggedEventId(ILogger logger) =>
        logger.ReceivedCalls().Single(call => string.Equals(call.GetMethodInfo().Name, nameof(ILogger.Log), StringComparison.Ordinal))
            .GetArguments()[1] is EventId id ? id : throw new InvalidOperationException("No EventId argument.");
}
