using System.Reflection;
using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Complexity = Daedalus.Domain.Entities.Complexity;
using Priority = Daedalus.Domain.Entities.Priority;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="PrdEndpoints"/> answers what <c>PrdController</c> answered: an empty requirement or item list is a
///     400 <c>{ "error" }</c>, a failed <see cref="IPrdService"/> result is a 400 carrying its error, a success is a
///     200, and any exception is logged and answered as a 500 carrying <c>error</c> and <c>details</c>.
/// </summary>
public sealed class PrdEndpointsTests
{
    private const string Category = "Daedalus.Api.Endpoints.PrdEndpoints";

    private static readonly Guid ProjectId = Guid.NewGuid();

    private readonly IPrdService _service = Substitute.For<IPrdService>();

    [Fact]
    public async Task GeneratePrd_WithRequirements_ReturnsThePrdAndPassesTheRequestThrough()
    {
        var prd = NewPrd();
        _service.GeneratePrdAsync(ProjectId, "Build it", "ctx", Arg.Any<CancellationToken>())
            .Returns(Result<PrdResponseDto>.Success(prd));
        var (factory, logger) = CreateLogging();
        var request = await BindGenerate($"{{\"projectId\":\"{ProjectId}\",\"userRequirements\":\"Build it\",\"context\":\"ctx\"}}");

        var result = await PrdEndpoints.GeneratePrd(request, _service, factory, CancellationToken.None);

        // Red: answer a BadRequest in place of Ok in the success branch of GeneratePrd.
        result.Result.Should().BeOfType<Ok<PrdResponseDto>>().Which.Value.Should().BeSameAs(prd);
        // Red: pass null in place of request.Context to GeneratePrdAsync in GeneratePrd.
        await _service.Received(1).GeneratePrdAsync(ProjectId, "Build it", "ctx", Arg.Any<CancellationToken>());
        // Red: drop LogGeneratingPrd, or change its EventId 100 to 108; or drop LogPrdGeneratedSuccessfully, or change 102.
        LoggedEventIds(logger).Should().Equal(100, 102);
        // Red: pass the category "Other" to CreateLogger in GeneratePrd.
        factory.Received(1).CreateLogger(Category);
    }

    [Theory]
    [InlineData("{\"projectId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"userRequirements\":\"\"}")]
    [InlineData("{\"userRequirements\":\"   \"}")]
    public async Task GeneratePrd_WithBlankRequirements_Answers400WithoutCallingTheService(string body)
    {
        var request = await BindGenerate(body);

        var result = await PrdEndpoints.GeneratePrd(request, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: delete the IsNullOrWhiteSpace check in GeneratePrd; the service is called and the result is not a 400.
        var json = result.Result.Should().BeOfType<JsonHttpResult<ErrorBody>>().Subject;
        // Red: answer 422 in place of 400 for blank requirements in GeneratePrd.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: change the message "User requirements cannot be empty" in GeneratePrd.
        json.Value.Should().Be(new ErrorBody("User requirements cannot be empty"));
        // Red: call the service before the blank check in GeneratePrd.
        await _service.DidNotReceiveWithAnyArgs().GeneratePrdAsync(Guid.Empty, string.Empty, null, CancellationToken.None);
    }

    [Fact]
    public async Task GeneratePrd_WhenTheServiceFails_Answers400CarryingTheErrorAndLogsAWarning()
    {
        _service.GeneratePrdAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<PrdResponseDto>.Failure("LLM unavailable"));
        var (factory, logger) = CreateLogging();
        var request = await BindGenerate($"{{\"projectId\":\"{ProjectId}\",\"userRequirements\":\"Build it\"}}");

        var result = await PrdEndpoints.GeneratePrd(request, _service, factory, CancellationToken.None);

        // Red: ignore result.IsFailure in GeneratePrd; the result is Ok with a null value.
        var json = result.Result.Should().BeOfType<JsonHttpResult<ErrorBody>>().Subject;
        // Red: answer 404 in place of 400 for a failed result in GeneratePrd.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: answer a constant message in place of result.Error in GeneratePrd.
        json.Value.Should().Be(new ErrorBody("LLM unavailable"));
        // Red: drop LogPrdGenerationFailed, or change its EventId 101 to 109.
        LoggedEventIds(logger).Should().Equal(100, 101);
    }

    [Fact]
    public async Task GeneratePrd_WhenTheServiceThrows_Answers500WithErrorAndDetailsAndLogsIt()
    {
        _service.GeneratePrdAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<PrdResponseDto>>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();
        var request = await BindGenerate($"{{\"projectId\":\"{ProjectId}\",\"userRequirements\":\"Build it\"}}");

        var result = await PrdEndpoints.GeneratePrd(request, _service, factory, CancellationToken.None);

        // Red: rethrow in the catch of GeneratePrd, or answer a 400; the result is not a 500 body.
        var json = result.Result.Should().BeOfType<JsonHttpResult<ErrorDetailBody>>().Subject;
        // Red: answer 400 in place of 500 in the catch of GeneratePrd.
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: change the error text or drop ex.Message from details in the catch of GeneratePrd.
        json.Value.Should().Be(new ErrorDetailBody("Error generating PRD", "boom"));
        // Red: drop LogErrorGeneratingPrd in the catch of GeneratePrd, or change its EventId 103.
        LoggedEventIds(logger).Should().Equal(100, 103);
        // Red: pass the category "Other" to CreateLogger in GeneratePrd.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task ConvertToTasks_WithItems_ReturnsTheTasksAndPassesTheItemsThrough()
    {
        var tasks = new List<TaskDto>();
        _service.ConvertToTasksAsync(ProjectId, Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<TaskDto>>.Success(tasks));
        var (factory, logger) = CreateLogging();
        var request = await BindConvert(ConversionBody(ProjectId, 2));

        var result = await PrdEndpoints.ConvertToTasks(request, _service, factory, CancellationToken.None);

        // Red: answer a BadRequest in place of Ok in the success branch of ConvertToTasks.
        result.Result.Should().BeOfType<Ok<List<TaskDto>>>().Which.Value.Should().BeSameAs(tasks);
        // Red: pass an empty list in place of request.PrdItems to ConvertToTasksAsync in ConvertToTasks.
        await _service.Received(1).ConvertToTasksAsync(
            ProjectId, Arg.Is<IReadOnlyList<PrdItemForConversionDto>>(items => items.Count == 2), Arg.Any<CancellationToken>());
        // Red: drop LogConvertingPrdItems, or change its EventId 104 to 110; or drop LogConversionSuccessful, or change 106.
        LoggedEventIds(logger).Should().Equal(104, 106);
        // Red: pass the category "Other" to CreateLogger in ConvertToTasks.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task ConvertToTasks_WithNoItems_Answers400WithoutCallingTheService()
    {
        var request = await BindConvert(ConversionBody(ProjectId, 0));

        var result = await PrdEndpoints.ConvertToTasks(request, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: delete the Count == 0 check in ConvertToTasks; the service is called and the result is not a 400.
        var json = result.Result.Should().BeOfType<JsonHttpResult<ErrorBody>>().Subject;
        // Red: answer 422 in place of 400 for no items in ConvertToTasks.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: change the message "No PRD items to convert" in ConvertToTasks.
        json.Value.Should().Be(new ErrorBody("No PRD items to convert"));
        // Red: call the service before the empty check in ConvertToTasks.
        await _service.DidNotReceiveWithAnyArgs().ConvertToTasksAsync(Guid.Empty, [], CancellationToken.None);
    }

    [Fact]
    public async Task ConvertToTasks_WhenTheServiceFails_Answers400CarryingTheErrorAndLogsAWarning()
    {
        _service.ConvertToTasksAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<TaskDto>>.Failure("project not found"));
        var (factory, logger) = CreateLogging();
        var request = await BindConvert(ConversionBody(ProjectId, 1));

        var result = await PrdEndpoints.ConvertToTasks(request, _service, factory, CancellationToken.None);

        // Red: ignore result.IsFailure in ConvertToTasks; the result is Ok with a null value.
        var json = result.Result.Should().BeOfType<JsonHttpResult<ErrorBody>>().Subject;
        // Red: answer 404 in place of 400 for a failed result in ConvertToTasks.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: answer a constant message in place of result.Error in ConvertToTasks.
        json.Value.Should().Be(new ErrorBody("project not found"));
        // Red: drop LogConversionFailed, or change its EventId 105 to 111.
        LoggedEventIds(logger).Should().Equal(104, 105);
    }

    [Fact]
    public async Task ConvertToTasks_WhenTheServiceThrows_Answers500WithErrorAndDetailsAndLogsIt()
    {
        _service.ConvertToTasksAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<List<TaskDto>>>>(_ => throw new InvalidOperationException("boom"));
        var (factory, logger) = CreateLogging();
        var request = await BindConvert(ConversionBody(ProjectId, 1));

        var result = await PrdEndpoints.ConvertToTasks(request, _service, factory, CancellationToken.None);

        // Red: rethrow in the catch of ConvertToTasks, or answer a 400; the result is not a 500 body.
        var json = result.Result.Should().BeOfType<JsonHttpResult<ErrorDetailBody>>().Subject;
        // Red: answer 400 in place of 500 in the catch of ConvertToTasks.
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: change the error text or drop ex.Message from details in the catch of ConvertToTasks.
        json.Value.Should().Be(new ErrorDetailBody("Error converting PRD items to tasks", "boom"));
        // Red: drop LogErrorConvertingPrdItems in the catch of ConvertToTasks, or change its EventId 107.
        LoggedEventIds(logger).Should().Equal(104, 107);
        // Red: pass the category "Other" to CreateLogger in ConvertToTasks.
        factory.Received(1).CreateLogger(Category);
    }

    [Theory]
    [InlineData(true, "{\"error\":\"Error generating PRD\",\"details\":\"boom\"}")]
    [InlineData(false, "{\"error\":\"Error converting PRD items to tasks\",\"details\":\"boom\"}")]
    public async Task The500Body_IsWrittenAsErrorAndDetailsThroughTheEndpointJsonOptions(bool generate, string expected)
    {
        _service.GeneratePrdAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<PrdResponseDto>>>(_ => throw new InvalidOperationException("boom"));
        _service.ConvertToTasksAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<List<TaskDto>>>>(_ => throw new InvalidOperationException("boom"));
        var factory = CreateLogging().Factory;
        IResult result = generate
            ? (await PrdEndpoints.GeneratePrd(
                await BindGenerate($"{{\"projectId\":\"{ProjectId}\",\"userRequirements\":\"Build it\"}}"),
                _service, factory, CancellationToken.None)).Result
            : (await PrdEndpoints.ConvertToTasks(
                await BindConvert(ConversionBody(ProjectId, 1)), _service, factory, CancellationToken.None)).Result;
        await using var services = Services();
        var context = Create(services);

        var body = await ExecuteAsync(result, context);

        // Red: remove ErrorDetailBody from ApiJsonSerializerContext, or rename its Details member; the write throws or the bytes differ.
        body.Should().Be(expected);
        // Red: write the body with another content type than JSON in the 500 result.
        context.Response.ContentType.Should().Be(Json);
    }

    private static PrdResponseDto NewPrd()
    {
        var item = new PrdItemDto("t", "d", "a", Priority.Medium, Complexity.Low);
        return new PrdResponseDto("id", ProjectId, "overview", ["g"], ["a"], [item], [item, item], DateTime.UnixEpoch);
    }

    private static string ConversionBody(Guid projectId, int items)
    {
        var item = "{\"title\":\"t\",\"description\":\"d\",\"acceptanceCriteria\":\"a\",\"priority\":1,\"complexity\":1,"
            + "\"dependencies\":[],\"phase\":\"p\",\"parallelGroup\":1}";
        return $"{{\"projectId\":\"{projectId}\",\"prdItems\":[{string.Join(',', Enumerable.Repeat(item, items))}]}}";
    }

    private static async Task<ApiBody<PrdRequestDto>> BindGenerate(string body) => await Bind<PrdRequestDto>(body, "request");

    private static async Task<ApiBody<PrdConversionRequestDto>> BindConvert(string body) =>
        await Bind<PrdConversionRequestDto>(body, "request");

    private static async Task<ApiBody<T>> Bind<T>(string body, string parameterName)
        where T : class
    {
        await using var services = Services();
        var context = WithJsonBody(services, body);
        var parameter = typeof(PrdEndpoints).GetMethods()
            .SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(ApiBody<T>) && string.Equals(p.Name, parameterName, StringComparison.Ordinal));
        return await BindAsync<ApiBody<T>>(context, parameter);
    }

    private static (ILoggerFactory Factory, ILogger Logger) CreateLogging()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        return (factory, logger);
    }

    private static int[] LoggedEventIds(ILogger logger) =>
        [.. logger.ReceivedCalls()
            .Where(call => string.Equals(call.GetMethodInfo().Name, nameof(ILogger.Log), StringComparison.Ordinal))
            .Select(call => call.GetArguments()[1] is EventId id ? id.Id : throw new InvalidOperationException("No EventId."))];
}
