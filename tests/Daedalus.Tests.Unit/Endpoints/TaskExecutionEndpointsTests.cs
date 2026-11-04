using System.Reflection;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="TaskExecutionEndpoints"/> answers what <c>TaskExecutionsController</c> answered: both reads call
///     straight through to <see cref="ITaskExecutionQueryService"/> with the sent or default paging, and any failure
///     is logged and answered as a 500 carrying <c>{ "error": "Internal server error" }</c>.
/// </summary>
public sealed class TaskExecutionEndpointsTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly PagedResultDto<TaskExecutionDto> Page = new([], 3, 2, 5);

    [Fact]
    public async Task GetByTaskId_passes_the_sent_paging_and_returns_the_page()
    {
        var service = Substitute.For<ITaskExecutionQueryService>();
        service.GetByTaskIdAsync(Id, 2, 5, Arg.Any<CancellationToken>()).Returns(Page);
        await using var services = Services();
        var (page, pageSize) = await BindPaging(services, "?page=2&pageSize=5");

        var result = await TaskExecutionEndpoints.GetByTaskId(
            Id, page, pageSize, service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: pass 1 and 10 in place of page.ValueOr and pageSize.ValueOr in GetByTaskId; the stub for 2 and 5 is not met.
        result.Result.Should().BeOfType<Ok<PagedResultDto<TaskExecutionDto>>>().Which.Value.Should().BeSameAs(Page);
    }

    [Fact]
    public async Task GetByTaskId_without_paging_asks_for_page_one_of_ten()
    {
        var service = Substitute.For<ITaskExecutionQueryService>();
        service.GetByTaskIdAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Page);
        await using var services = Services();
        var (page, pageSize) = await BindPaging(services, string.Empty);

        await TaskExecutionEndpoints.GetByTaskId(
            Id, page, pageSize, service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: use page.Value and pageSize.Value in GetByTaskId; the service is asked for 0 and 0.
        await service.Received(1).GetByTaskIdAsync(Id, 1, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBySessionId_passes_the_sent_paging_and_returns_the_page()
    {
        var service = Substitute.For<ITaskExecutionQueryService>();
        service.GetBySessionIdAsync(Id, 3, 7, Arg.Any<CancellationToken>()).Returns(Page);
        await using var services = Services();
        var (page, pageSize) = await BindPaging(services, "?page=3&pageSize=7");

        var result = await TaskExecutionEndpoints.GetBySessionId(
            Id, page, pageSize, service, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: pass the default paging in place of the sent one in GetBySessionId; the stub for 3 and 7 is not met.
        result.Result.Should().BeOfType<Ok<PagedResultDto<TaskExecutionDto>>>().Which.Value.Should().BeSameAs(Page);
    }

    [Fact]
    public async Task GetByTaskId_answers_a_logged_500_error_body_when_the_service_throws()
    {
        var service = Substitute.For<ITaskExecutionQueryService>();
        service.GetByTaskIdAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<PagedResultDto<TaskExecutionDto>>>(_ => throw new InvalidOperationException("boom"));
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        await using var services = Services();
        var (page, pageSize) = await BindPaging(services, string.Empty);

        var result = await TaskExecutionEndpoints.GetByTaskId(Id, page, pageSize, service, factory, CancellationToken.None);

        // Red: rethrow in the catch of GetByTaskId, or answer a 400; the result is not a 500 error body.
        var json = result.Result.Should().BeOfType<JsonHttpResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        json.Value.Should().Be(new ErrorBody("Internal server error"));
        // Red: drop the call to Log.ErrorRetrievingExecutionsForTask in the catch of GetByTaskId.
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == nameof(ILogger.Log));
    }

    [Fact]
    public async Task GetBySessionId_answers_a_logged_500_error_body_when_the_service_throws()
    {
        var service = Substitute.For<ITaskExecutionQueryService>();
        service.GetBySessionIdAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<PagedResultDto<TaskExecutionDto>>>(_ => throw new InvalidOperationException("boom"));
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        await using var services = Services();
        var (page, pageSize) = await BindPaging(services, string.Empty);

        var result = await TaskExecutionEndpoints.GetBySessionId(Id, page, pageSize, service, factory, CancellationToken.None);

        // Red: answer StatusCodes.Status503ServiceUnavailable in the catch of GetBySessionId.
        var json = result.Result.Should().BeOfType<JsonHttpResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        json.Value.Should().Be(new ErrorBody("Internal server error"));
        // Red: drop the call to Log.ErrorRetrievingExecutionsForSession in the catch of GetBySessionId.
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == nameof(ILogger.Log));
    }

    private static void PagingQuery(ApiQuery<int> page, ApiQuery<int> pageSize) => _ = (page, pageSize);

    private static async Task<(ApiQuery<int> Page, ApiQuery<int> PageSize)> BindPaging(
        IServiceProvider services, string queryString)
    {
        var context = Create(services);
        context.Request.QueryString = new QueryString(queryString);
        var parameters = typeof(TaskExecutionEndpointsTests)
            .GetMethod(nameof(PagingQuery), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        return (await BindAsync<ApiQuery<int>>(context, parameters[0]), await BindAsync<ApiQuery<int>>(context, parameters[1]));
    }
}
