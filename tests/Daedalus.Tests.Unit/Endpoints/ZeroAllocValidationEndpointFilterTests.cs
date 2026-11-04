using System.Reflection;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Middleware;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Http;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="ZeroAllocValidationEndpointFilter"/> answers in MVC's order: a body MVC cannot read is 415, then the
///     binding problems of every parameter as one problem, then the ZeroAlloc validator, with the MVC filter's own
///     problem shape (rfc7231 type, a detail, no traceId), as <c>Empty_string_is_not_implicitly_required</c> pins it.
/// </summary>
public sealed class ZeroAllocValidationEndpointFilterTests
{
    private const string ValidBody = "{\"projectName\":\"p\",\"description\":\"d\",\"version\":\"1.0\"}";

    [Fact]
    public async Task Filter_binding_problem_answers_before_validator()
    {
        await using var services = Services();
        var context = WithJsonBody(services, "{}");
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(ProblemContentType.Json), "t"));
        var body = await Body(context);
        var next = new Next();

        var text = await InvokeAsync(context, next, body);

        // Red: run the validators before the binding problems; the answer is then the ZeroAlloc problem, rfc7231
        //      typed with "Project name is required.", which ModelStateInvalidFilter never lets MVC reach.
        AssertProblem(text, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["ProjectName"] = ["The ProjectName field is required."],
                ["Description"] = ["The Description field is required."],
                ["Version"] = ["The Version field is required."],
            });
        // Red: call next after writing the problem.
        next.Called.Should().BeFalse();
        context.Response.ContentType.Should().Be(Json);
    }

    [Fact]
    public async Task Filter_invalid_dto_answers_rfc7231_validation_problem()
    {
        await using var services = Services();
        var context = WithJsonBody(services, "{\"projectName\":\"\",\"description\":\"d\",\"version\":\"v\"}");
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(ProblemContentType.Json), "t"));
        var next = new Next();

        var text = await InvokeAsync(context, next, await Body(context));

        // Red: the filter's problem built through ApiProblem.Of, which types it rfc9110 and adds a traceId; or its
        //      detail left out. The MVC filter's exact type, title and detail are the reference.
        AssertProblem(text, FilterBadRequestType, ValidationTitle, 400, detail: "Project name is required.",
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["ProjectName"] = ["Project name is required."] },
            traceId: null);
        // Red: ApiProblem.Validation answers 422 whatever the problem's status; the body still says 400.
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().Be(Json);
        next.Called.Should().BeFalse();
    }

    [Fact]
    public async Task Filter_matches_the_mvc_filters_problem_for_several_failures()
    {
        await using var services = Services();
        var context = WithJsonBody(services, "{\"projectName\":\" \",\"description\":\" \",\"version\":\"v\"}");

        var text = await InvokeAsync(context, new Next(), await Body(context));

        // Red: the detail taken from the last failure rather than the first.
        AssertProblem(text, FilterBadRequestType, ValidationTitle, 400, detail: "Project name is required.",
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["ProjectName"] = ["Project name is required."],
                ["Description"] = ["Description is required."],
            },
            traceId: null);
    }

    [Fact]
    public async Task Filter_valid_dto_calls_next()
    {
        await using var services = Services();
        var context = WithJsonBody(services, ValidBody);
        var next = new Next();

        await InvokeAsync(context, next, await Body(context));

        // Red: answer a problem for a valid body, or never call next.
        next.Called.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Filter_validates_an_argument_that_is_not_a_binding()
    {
        await using var services = Services();
        var context = Create(services);
        var next = new Next();

        var text = await InvokeAsync(context, next, new CreateProjectDto("", "d", "v"));

        // Red: validate IApiBinding arguments only; the MVC filter validates every action argument with an adapter.
        next.Called.Should().BeFalse();
        AssertProblem(text, FilterBadRequestType, ValidationTitle, 400, detail: "Project name is required.",
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["ProjectName"] = ["Project name is required."] },
            traceId: null);
    }

    [Fact]
    public async Task Filter_merges_the_binding_problems_of_every_parameter()
    {
        await using var services = Services();
        var context = WithJsonBody(services, "null");
        context.Request.QueryString = new QueryString("?take=abc");
        var take = await BindAsync<ApiQuery<int>>(context, TakeParameter());

        var text = await InvokeAsync(context, new Next(), await Body(context), take);

        // Red: answer the first binding problem alone; MVC's model state holds every parameter's errors in one problem.
        AssertProblem(text, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [""] = [NonEmptyBody],
                ["request"] = [RequestRequired],
                ["take"] = ["The value 'abc' is not valid."],
            });
    }

    [Fact]
    public async Task Filter_unsupported_media_type_answers_415_first()
    {
        await using var services = Services();
        var context = WithJsonBody(services, ValidBody, "text/plain");
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(ProblemContentType.Json), "t"));
        context.Request.QueryString = new QueryString("?take=abc");
        var take = await BindAsync<ApiQuery<int>>(context, TakeParameter());
        var next = new Next();

        var text = await InvokeAsync(context, next, take, await Body(context));

        // Red: let the query's binding problem answer first; MVC's UnsupportedContentTypeFilter runs before
        //      ModelStateInvalidFilter and answers the bodiless 415, which ClientErrorResultFilter turns into its
        //      client-error problem.
        context.Response.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        // Red: answer the 415 with TypedResults.Problem, which ignores the group's ProblemContentType; MVC's 415 on
        //      Projects is application/json, Unreadable_body_answers_415_problem.
        context.Response.ContentType.Should().Be(Json);
        AssertProblem(text, UnsupportedMediaTypeType, "Unsupported Media Type", 415);
        next.Called.Should().BeFalse();
    }

    private static async Task<string> InvokeAsync(HttpContext context, Next next, params object?[] arguments)
    {
        var filter = new ZeroAllocValidationEndpointFilter(
            [new ValidationAdapter<CreateProjectDto>(new CreateProjectDtoValidator())]);

        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(context, arguments), next.Delegate);
        if (result is IResult written)
        {
            await written.ExecuteAsync(context);
        }

        return ReadBody(context);
    }

    private static async Task<ApiBody<CreateProjectDto>> Body(HttpContext context) =>
        (await BindAsync<ApiBody<CreateProjectDto>>(context, typeof(ZeroAllocValidationEndpointFilterTests)
            .GetMethod(nameof(Probe), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters()[0]))!;

    private static ParameterInfo TakeParameter() =>
        typeof(ZeroAllocValidationEndpointFilterTests).GetMethod(nameof(Probe), BindingFlags.NonPublic | BindingFlags.Static)!
            .GetParameters()[1];

    private static void Probe(ApiBody<CreateProjectDto> request, ApiQuery<int> take) => _ = (request, take);

    private sealed class Next
    {
        public bool Called { get; private set; }

        public EndpointFilterDelegate Delegate => _ =>
        {
            Called = true;
            return ValueTask.FromResult<object?>(null);
        };
    }
}
