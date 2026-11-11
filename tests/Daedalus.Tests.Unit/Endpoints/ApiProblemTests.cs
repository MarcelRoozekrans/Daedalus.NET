using System.Diagnostics;
using System.Text.Json;
using Daedalus.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="ApiProblem"/> writes what MVC's <c>ProblemDetailsFactory</c> and output formatter wrote, as
///     <c>MvcBehaviourContractTests</c> pins it over HTTP (rulings R13, R15, R18, R19).
/// </summary>
public sealed class ApiProblemTests
{
    [Fact]
    public async Task Of_404_writes_mvc_client_error_members()
    {
        await using var services = Services();
        var context = Create(services, ProblemContentType.Json);

        var text = await ExecuteAsync(ApiProblem.Of(context, StatusCodes.Status404NotFound), context);

        // Red: return status 410 from ApiProblemResult.ExecuteAsync.
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: the 404 row of ApiProblem's client-error table pointed at section-15.5.4; a detail written as "".
        AssertProblem(text, NotFoundType, "Not Found", 404);
    }

    [Fact]
    public async Task Of_401_writes_mvc_client_error_members()
    {
        await using var services = Services();
        var context = Create(services);

        var text = await ExecuteAsync(ApiProblem.Of(context, StatusCodes.Status401Unauthorized), context);

        // Red: the 401 row of the client-error table titled "Unauthorised".
        AssertProblem(text, UnauthorizedType, "Unauthorized", 401);
    }

    [Fact]
    public async Task Of_uses_group_content_type()
    {
        await using var services = Services();
        var context = Create(services, ProblemContentType.Json);

        await ExecuteAsync(ApiProblem.Of(context, StatusCodes.Status404NotFound), context);

        // Red: ApiProblem ignores the endpoint's ProblemContentType and always writes application/problem+json.
        context.Response.ContentType.Should().Be(Json);
    }

    [Fact]
    public async Task Of_defaults_to_problem_json()
    {
        await using var services = Services();
        var context = Create(services);

        await ExecuteAsync(ApiProblem.Of(context, StatusCodes.Status404NotFound), context);

        // Red: the default content type changed to application/json, or the "; charset=utf-8" suffix dropped.
        context.Response.ContentType.Should().Be(ProblemJson);
    }

    [Fact]
    public async Task Of_takes_the_last_content_type_so_an_endpoint_overrides_its_group()
    {
        await using var services = Services();
        var context = Create(services, ProblemContentType.Json, ProblemContentType.ProblemJson);

        await ExecuteAsync(ApiProblem.Of(context, StatusCodes.Status400BadRequest), context);

        // Red: read the first ProblemContentType in the metadata instead of the last, ruling R12.
        context.Response.ContentType.Should().Be(ProblemJson);
    }

    [Fact]
    public async Task Of_writes_detail_title_and_extensions()
    {
        await using var services = Services();
        var context = Create(services, ProblemContentType.Json);

        var text = await ExecuteAsync(
            ApiProblem.Of(context, StatusCodes.Status409Conflict, detail: "It's taken.", title: "Busy",
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "SessionBusy" }),
            context);

        // Red: a given title overwritten by the table's "Conflict"; the extensions not copied; the apostrophe
        //      escaped by setting the default JavaScriptEncoder in AddDaedalusEndpoints. The Http options' own
        //      default is the relaxed encoder MVC writes with (R19, R33).
        AssertProblem(text, "https://tools.ietf.org/html/rfc9110#section-15.5.10", "Busy", 409, detail: "It's taken.",
            extra: new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = "\"SessionBusy\"" });
    }

    private const string Rfc9110 = "https://tools.ietf.org/html/rfc9110#section-";

    /// <summary>
    ///     The client-error table as .NET 10's MVC filled <c>ApiBehaviorOptions.ClientErrorMapping</c>, 26 rows, and
    ///     two statuses it does not map, 429 and the agents' 499, which get no <c>type</c> and no <c>title</c>. Until
    ///     MVC left the host this was read from MVC's own <c>ProblemDetailsFactory</c>; it is frozen at .NET 10's
    ///     table since (R32, R54).
    /// </summary>
    public static TheoryData<int, string?, string?> ClientErrorRows() => new()
    {
        { 400, Rfc9110 + "15.5.1", "Bad Request" },
        { 401, Rfc9110 + "15.5.2", "Unauthorized" },
        { 403, Rfc9110 + "15.5.4", "Forbidden" },
        { 404, Rfc9110 + "15.5.5", "Not Found" },
        { 405, Rfc9110 + "15.5.6", "Method Not Allowed" },
        { 406, Rfc9110 + "15.5.7", "Not Acceptable" },
        { 407, Rfc9110 + "15.5.8", "Proxy Authentication Required" },
        { 408, Rfc9110 + "15.5.9", "Request Timeout" },
        { 409, Rfc9110 + "15.5.10", "Conflict" },
        { 410, Rfc9110 + "15.5.11", "Gone" },
        { 411, Rfc9110 + "15.5.12", "Length Required" },
        { 412, Rfc9110 + "15.5.13", "Precondition Failed" },
        { 413, Rfc9110 + "15.5.14", "Content Too Large" },
        { 414, Rfc9110 + "15.5.15", "URI Too Long" },
        { 415, Rfc9110 + "15.5.16", "Unsupported Media Type" },
        { 416, Rfc9110 + "15.5.17", "Range Not Satisfiable" },
        { 417, Rfc9110 + "15.5.18", "Expectation Failed" },
        { 421, Rfc9110 + "15.5.20", "Misdirected Request" },
        { 422, "https://tools.ietf.org/html/rfc4918#section-11.2", "Unprocessable Entity" },
        { 426, Rfc9110 + "15.5.22", "Upgrade Required" },
        { 500, Rfc9110 + "15.6.1", "An error occurred while processing your request." },
        { 501, Rfc9110 + "15.6.2", "Not Implemented" },
        { 502, Rfc9110 + "15.6.3", "Bad Gateway" },
        { 503, Rfc9110 + "15.6.4", "Service Unavailable" },
        { 504, Rfc9110 + "15.6.5", "Gateway Timeout" },
        { 505, Rfc9110 + "15.6.6", "HTTP Version Not Supported" },
        { 429, null, null },
        { 499, null, null },
    };

    [Theory]
    [MemberData(nameof(ClientErrorRows))]
    public async Task Of_writes_the_net10_client_error_table(int status, string? type, string? title)
    {
        await using var services = Services();
        var context = Create(services);

        var actual = ApiProblem.Of(context, status, detail: "d").ProblemDetails;

        // Red: drop the 410 row of ApiProblem's client-error table; or add a 429 row to it.
        Describe(actual).Should().Be($"{type}|{title}|{status}|d||traceId={TraceIdentifier}");

        static string Describe(ProblemDetails problem) =>
            $"{problem.Type}|{problem.Title}|{problem.Status}|{problem.Detail}|{problem.Instance}|"
            + string.Join(',', problem.Extensions.Select(e => $"{e.Key}={e.Value}"));
    }

    [Fact]
    public async Task Of_trace_id_prefers_the_current_activity()
    {
        await using var services = Services();
        var context = Create(services);
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        var text = await ExecuteAsync(ApiProblem.Of(context, StatusCodes.Status404NotFound), context);

        // Red: compute traceId as httpContext.TraceIdentifier alone.
        AssertProblem(text, NotFoundType, "Not Found", 404, traceId: activity.Id);
    }

    [Fact]
    public async Task Of_applies_customize_problem_details_as_mvc_factory_does()
    {
        await using var services = Services(collection =>
            collection.AddProblemDetails(options => options.CustomizeProblemDetails = c => c.ProblemDetails.Extensions["extra"] = "x"));
        var context = Create(services);

        var text = await ExecuteAsync(ApiProblem.Of(context, StatusCodes.Status404NotFound), context);

        // Red: drop the CustomizeProblemDetails call from ApiProblem; MVC's DefaultProblemDetailsFactory made it.
        AssertProblem(text, NotFoundType, "Not Found", 404,
            extra: new Dictionary<string, string>(StringComparer.Ordinal) { ["extra"] = "\"x\"" });
    }

    [Fact]
    public async Task AsGiven_writes_only_the_members_the_problem_carries()
    {
        await using var services = Services();
        var context = Create(services, ProblemContentType.Json);
        var problem = new ProblemDetails { Title = "Not Found", Detail = "No such model.", Status = 404 };

        var result = ApiProblem.AsGiven(context, problem);
        var text = await ExecuteAsync(result, context);

        // Red: AsGiven applies the client-error defaults, as Of does; the body gains type and traceId.
        AssertProblem(text, null, "Not Found", 404, detail: "No such model.", traceId: null);
        // Red: AsGiven answers the status 500 in place of the problem's Status.
        result.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: AsGiven ignores the endpoint's ProblemContentType and writes application/problem+json.
        context.Response.ContentType.Should().Be(Json);
    }

    [Fact]
    public void AsGiven_refuses_a_problem_without_a_status()
    {
        using var services = Services();
        var context = Create(services);

        var act = () => ApiProblem.AsGiven(context, new ProblemDetails { Title = "Not Found" });

        // Red: AsGiven falls back to 200 for a missing Status in place of throwing.
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Validation_writes_the_problem_as_given()
    {
        await using var services = Services();
        var context = Create(services, ProblemContentType.Json);
        var problem = new ValidationProblemDetails(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ProjectName"] = ["Project name is required."],
        })
        {
            Status = 400,
            Title = ValidationTitle,
            Detail = "Project name is required.",
            Type = FilterBadRequestType,
        };

        var text = await ExecuteAsync(ApiProblem.Validation(context, problem), context);

        // Red: ApiProblem.Validation sets traceId, or replaces the rfc7231 type with the table's.
        AssertProblem(text, FilterBadRequestType, ValidationTitle, 400, detail: "Project name is required.",
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["ProjectName"] = ["Project name is required."] },
            traceId: null);
        // Red: Validation answers 422.
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().Be(Json);
    }

    [Fact]
    public void Result_exposes_status_value_and_content_type()
    {
        using var services = Services();
        var context = Create(services, ProblemContentType.Json);

        var result = ApiProblem.Of(context, StatusCodes.Status503ServiceUnavailable, detail: "off");

        // Red: IStatusCodeHttpResult.StatusCode answers null; IContentTypeHttpResult.ContentType drops the charset.
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(503);
        ((IContentTypeHttpResult)result).ContentType.Should().Be(Json);
        // Red: IValueHttpResult of ProblemDetails answers a copy without the detail.
        ((IValueHttpResult<ProblemDetails>)result).Value!.Detail.Should().Be("off");
    }

    [Fact]
    public void Endpoint_json_reads_and_writes_at_mvcs_max_depth()
    {
        using var services = Services();

        var options = services.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

        // Red: drop MaxDepth = 32 from AddDaedalusEndpoints; the Http default, 0, means 64.
        options.MaxDepth.Should().Be(32);
    }

    [Fact]
    public void Error_body_serializes_as_one_error_member()
    {
        using var services = Services();
        var options = services.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

        var json = JsonSerializer.Serialize(new ErrorBody("Project with ID x isn't there"), options.GetTypeInfo<ErrorBody>());

        // Red: ErrorBody missing from ApiJsonSerializerContext throws NotSupportedException here; setting the
        //      default JavaScriptEncoder in AddDaedalusEndpoints writes the apostrophe escaped.
        json.Should().Be("{\"error\":\"Project with ID x isn't there\"}");
    }

    [Theory]
    [InlineData("http", "localhost", "", "/api/Tasks/42", "http://localhost/api/Tasks/42")]
    [InlineData("https", "example.com:8443", "", "/api/agents/sessions/s1", "https://example.com:8443/api/agents/sessions/s1")]
    [InlineData("http", "localhost", "/daedalus", "/api/Projects/7", "http://localhost/daedalus/api/Projects/7")]
    public void Location_is_absolute_as_created_at_action_writes_it(
        string scheme, string host, string pathBase, string path, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.PathBase = new PathString(pathBase);

        // Red: leave out PathBase, which MVC's link generator includes; or lower-case the path.
        ApiLocation.For(context.Request, path).Should().Be(expected);
    }
}
