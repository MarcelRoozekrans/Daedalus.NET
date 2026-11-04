using System.Text;
using Daedalus.Agents;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Middleware;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Validation;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     A throwaway endpoint taking <see cref="ApiBody{T}"/> and <see cref="ApiQuery{T}"/>, mapped through
///     <see cref="DaedalusEndpoints.MapApiGroup"/> the way every port maps its group, and run end to end through the
///     request delegate the request delegate generator compiled for it. This project sets
///     <c>EnableRequestDelegateGenerator</c>, as the API does, so the generator accepts the binders'
///     <c>BindAsync(HttpContext, ParameterInfo)</c> shape here, before any port relies on it.
/// </summary>
public sealed class GeneratedEndpointBindingTests
{
    private const string ValidBody = "{\"projectName\":\"p\",\"description\":\"d\",\"version\":\"1.0\"}";

    [Fact]
    public async Task Endpoint_is_compiled_by_the_request_delegate_generator()
    {
        await using var app = BuildApp(out var endpoint, out _);

        // Red: remove EnableRequestDelegateGenerator from Daedalus.Tests.Unit.csproj; RequestDelegateFactory then
        //      builds the delegate at runtime, outside the generated namespace.
        endpoint.RequestDelegate!.Method.DeclaringType!.FullName.Should().StartWith("Microsoft.AspNetCore.Http.Generated.");
    }

    [Fact]
    public async Task Endpoint_binds_body_and_query()
    {
        await using var app = BuildApp(out var endpoint, out _);
        var context = Request(app, endpoint, "?take=7", ValidBody);

        await endpoint.RequestDelegate!(context);

        // Red: ApiQuery leaves HasValue false for a sent value, so the handler reads 20; or ApiBody leaves Value null.
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK, ReadBody(context));
        ReadBody(context).Should().Be("{\"error\":\"7:p\"}");
    }

    [Fact]
    public async Task Endpoint_uses_the_query_default_when_absent()
    {
        await using var app = BuildApp(out var endpoint, out _);
        var context = Request(app, endpoint, "", ValidBody);

        await endpoint.RequestDelegate!(context);

        // Red: ApiQuery.ValueOr answers Value whatever HasValue says; take is then 0.
        ReadBody(context).Should().Be("{\"error\":\"20:p\"}");
    }

    [Fact]
    public async Task Endpoint_answers_the_binding_problem_through_the_group_filter()
    {
        await using var app = BuildApp(out var endpoint, out _);
        var context = Request(app, endpoint, "?take=abc", ValidBody);

        await endpoint.RequestDelegate!(context);

        // Red: MapApiGroup leaves out the ZeroAllocValidationEndpointFilter; the handler then answers 200 with take 20.
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest, ReadBody(context));
        // Red: the group's ProblemContentType.Json metadata removed; the problem is then application/problem+json.
        context.Response.ContentType.Should().Be(Json);
        AssertProblem(ReadBody(context), BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["take"] = ["The value 'abc' is not valid."] },
            traceId: context.TraceIdentifier);
    }

    [Fact]
    public async Task Endpoint_answers_the_validator_problem_through_the_group_filter()
    {
        await using var app = BuildApp(out var endpoint, out _);
        var context = Request(app, endpoint, "", "{\"projectName\":\"\",\"description\":\"d\",\"version\":\"v\"}");

        await endpoint.RequestDelegate!(context);

        // Red: the CreateProjectDto adapter registration removed; the empty name then reaches the handler, 200.
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest, ReadBody(context));
        AssertProblem(ReadBody(context), FilterBadRequestType, ValidationTitle, 400, detail: "Project name is required.",
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["ProjectName"] = ["Project name is required."] },
            traceId: null);
    }

    [Fact]
    public async Task Workflow_group_answers_503_before_binding_problems()
    {
        await using var app = BuildApp(out _, out var workflowEndpoint);
        var context = Request(app, workflowEndpoint, "?take=abc", "{\"name\":");

        await workflowEndpoint.RequestDelegate!(context);

        // Red: in MapApiGroup, add the validation filter before the engine filter; the binding 400 then answers
        //      where MVC's resource filter answers 503 before model binding.
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, ReadBody(context));
    }

    [Fact]
    public async Task Groups_carry_the_api_group_marker()
    {
        await using var app = BuildApp(out var endpoint, out var workflowEndpoint);

        // Red: MapApiGroup leaves out its DaedalusEndpointGroup metadata, which EndpointGroupTests reads on the host.
        endpoint.Metadata.GetMetadata<DaedalusEndpointGroup>().Should().NotBeNull();
        workflowEndpoint.Metadata.GetMetadata<DaedalusEndpointGroup>().Should().NotBeNull();
    }

    private static WebApplication BuildApp(out RouteEndpoint endpoint, out RouteEndpoint workflowEndpoint)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddDaedalusEndpoints();
        builder.Services.AddSingleton<ValidatorFor<CreateProjectDto>, CreateProjectDtoValidator>();
        builder.Services.AddSingleton<IValidationAdapter, ValidationAdapter<CreateProjectDto>>();
        builder.Services.AddSingleton(new WorkflowConfig { Enabled = false });
        var app = builder.Build();

        app.MapApiGroup("/probe").WithMetadata(ProblemContentType.Json)
            .MapPost("/", (ApiQuery<int> take, ApiBody<CreateProjectDto> request) =>
                TypedResults.Ok(new ErrorBody($"{take.ValueOr(20)}:{request.Value!.ProjectName}")));
        app.MapApiGroup("/workflow", requiresWorkflowEngine: true).WithMetadata(ProblemContentType.Json)
            .MapPost("/", (ApiQuery<int> take, ApiBody<CreateProjectDto> request) =>
                TypedResults.Ok(new ErrorBody($"{take.ValueOr(20)}:{request.Value!.ProjectName}")));

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        endpoint = endpoints.Single(e => e.RoutePattern.RawText!.StartsWith("/probe", StringComparison.Ordinal));
        workflowEndpoint = endpoints.Single(e => e.RoutePattern.RawText!.StartsWith("/workflow", StringComparison.Ordinal));
        return app;
    }

    private static DefaultHttpContext Request(WebApplication app, Endpoint endpoint, string query, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext { RequestServices = app.Services, TraceIdentifier = TraceIdentifier };
        context.Request.Method = HttpMethods.Post;
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);
        return context;
    }
}
