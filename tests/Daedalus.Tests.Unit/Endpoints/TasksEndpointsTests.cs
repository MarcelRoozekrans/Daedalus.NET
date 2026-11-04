using System.Reflection;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.CreateTask;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
using Daedalus.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="TasksEndpoints"/> answers what the MVC controller of <c>api/tasks</c> answered: a missing task is a 404
///     <c>{ "error" }</c>, a created task is a 201 whose <c>Location</c> keeps MVC's <c>/api/Tasks</c> casing, a refused
///     create is a 400 problem, and a throwing read is logged and answered as a 500.
/// </summary>
public sealed class TasksEndpointsTests
{
    private const string Category = "Daedalus.Api.Endpoints.TasksEndpoints";

    private static readonly Guid Id = Guid.NewGuid();

    private readonly ITaskQueryService _queries = Substitute.For<ITaskQueryService>();
    private readonly IApplicationCommands _commands = Substitute.For<IApplicationCommands>();

    [Fact]
    public async Task GetAllTasks_answers_a_logged_500_error_body_when_the_service_throws()
    {
        _queries.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<PagedResultDto<TaskDto>>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();
        await using var services = Services();
        var (pageQuery, pageSize) = await PagingQueryBinding.BindAsync(Create(services), string.Empty);

        var result = await TasksEndpoints.GetAllTasks(pageQuery, pageSize, _queries, factory, CancellationToken.None);

        // Red: answer 502 in InternalServerError, the helper both catches use; both 500 tests fail.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: "Internal error" in place of "Internal server error" in InternalServerError.
        json.Value.Should().Be(new ErrorBody("Internal server error"));
        // Red: change EventId 100 to 102 on Log.ErrorRetrievingTasks.
        LoggedEventId(logger).Id.Should().Be(100);
        // Red: pass the category "Other" to CreateLogger in the catch of GetAllTasks.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GetTaskById_answers_a_logged_500_error_body_when_the_service_throws()
    {
        _queries.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<TaskDto?>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();

        var result = await TasksEndpoints.GetTaskById(Id, _queries, factory, CancellationToken.None);

        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: change EventId 101 to 102 on Log.ErrorRetrievingTask.
        LoggedEventId(logger).Id.Should().Be(101);
        // Red: pass the category "Other" to CreateLogger in the catch of GetTaskById.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GetTaskById_for_a_missing_task_answers_a_404_error_body()
    {
        var result = await TasksEndpoints.GetTaskById(Id, _queries, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: StatusCodes.Status410Gone in place of Status404NotFound in TaskNotFound.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: $"Task {id} not found" in place of $"Task with ID {id} not found" in TaskNotFound.
        json.Value.Should().Be(new ErrorBody($"Task with ID {Id} not found"));
    }

    [Fact]
    public async Task CreateTask_answers_201_with_the_absolute_class_cased_location_and_defaults_the_optional_members()
    {
        var created = TaskOf(Id);
        _commands.CreateTaskAsync(Arg.Any<CreateTaskCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<TaskDto>.Success(created));
        await using var services = Services();
        var context = WithJsonBody(services, CreateBody);
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.com");
        context.Request.PathBase = "/base";
        var request = await BindAsync<ApiBody<CreateTaskDto>>(context, CreateParameter());

        var result = await TasksEndpoints.CreateTask(request, context, _commands, CancellationToken.None);

        var response = result.Result.Should().BeOfType<Created<TaskDto>>().Subject;
        // Red: "/api/tasks/{id}" in place of "/api/Tasks/{id}" in CreateTask.
        response.Location.Should().Be($"https://example.com/base/api/Tasks/{Id}");
        response.Value.Should().BeSameAs(created);
        // Red: "Phase-2" in place of the "Phase-1" default in CreateTask; or "TASK_" in place of the "TASK-" prefix.
        await _commands.Received(1).CreateTaskAsync(
            Arg.Is<CreateTaskCommand>(command => command.Phase == "Phase-1" && command.TaskId.StartsWith("TASK-", StringComparison.Ordinal)
                && command.TaskId.Length == 13),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTask_refused_by_the_command_answers_a_400_problem_with_the_reason()
    {
        _commands.CreateTaskAsync(Arg.Any<CreateTaskCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<TaskDto>.Failure("Project x not found"));
        await using var services = Services();
        var context = WithJsonBody(services, CreateBody);
        var request = await BindAsync<ApiBody<CreateTaskDto>>(context, CreateParameter());

        var result = await TasksEndpoints.CreateTask(request, context, _commands, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: StatusCodes.Status422UnprocessableEntity in place of Status400BadRequest in CreateTask; or
        //      ApiProblem.Of(context, StatusCodes.Status400BadRequest) without result.Error.
        AssertProblem(await ExecuteAsync(problem, context), BadRequestType, "Bad Request", 400, detail: "Project x not found");
    }

    private static readonly string CreateBody =
        $"{{\"projectId\":\"{Guid.NewGuid()}\",\"title\":\"T\",\"description\":\"D\",\"priority\":1,\"estimatedComplexity\":1}}";

    private static TaskDto TaskOf(Guid id) => new(
        id, "TASK-1", Guid.NewGuid(), "T", "D", 1, "Phase-1", 1, [], [], 1, "D", "<promise>COMPLETE</promise>", 10, 0,
        null, null, 0, DateTime.UnixEpoch, null, null, null, [], null, null, null, null);

    private static ParameterInfo CreateParameter() =>
        typeof(TasksEndpoints).GetMethod(nameof(TasksEndpoints.CreateTask))!.GetParameters()
            .Single(p => p.ParameterType == typeof(ApiBody<CreateTaskDto>));

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
