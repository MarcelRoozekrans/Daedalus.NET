using System.Collections.Frozen;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Group or endpoint metadata naming the media type a problem body is written as. Groups whose controller carried
///     <c>[Produces("application/json")]</c> add <see cref="Json"/> (#366); a group without it writes
///     <c>application/problem+json</c>, as MVC does for a controller without <c>[Produces]</c>. The last one in the
///     endpoint's metadata wins, so an endpoint's own overrides its group's (ruling R12).
/// </summary>
/// <remarks>
///     The member is <c>ContentType</c> because <c>EndpointContractSnapshotTests</c> matches this record by its type
///     name and reads that property for the snapshot's <c>content</c> column (rulings R11, R12, R27).
/// </remarks>
public sealed record ProblemContentType(string ContentType)
{
    /// <summary><c>application/json</c>, for the groups whose controller carried <c>[Produces("application/json")]</c>.</summary>
    public static ProblemContentType Json { get; } = new("application/json");

    /// <summary><c>application/problem+json</c>, what MVC writes for a problem when no <c>[Produces]</c> applies.</summary>
    public static ProblemContentType ProblemJson { get; } = new("application/problem+json");
}

/// <summary>
///     The problem bodies MVC writes on its own, for minimal API endpoints: the <c>type</c>, <c>title</c>,
///     <c>status</c>, <c>detail</c> and <c>traceId</c> of MVC's default problem details factory, with the
///     endpoint's <see cref="ProblemContentType"/> and MVC's <c>; charset=utf-8</c>.
/// </summary>
public static class ApiProblem
{
    /// <summary>The title <c>ValidationProblemDetails</c> carries, which MVC's factory keeps.</summary>
    internal const string ValidationTitle = "One or more validation errors occurred.";

    private const string Charset = "; charset=utf-8";

    /// <summary>
    ///     MVC's <c>ApiBehaviorOptions.ClientErrorMapping</c> as <c>ApiBehaviorOptionsSetup</c> fills it in .NET 10;
    ///     422 links to RFC 4918, not RFC 9110. A status not
    ///     listed gets no <c>type</c> and only the title it is given, as from MVC's factory; the Http
    ///     <c>ProblemDetailsDefaults</c> would fall back to the reason phrase instead, so they are not used.
    ///     <c>ApiProblemTests.Of_writes_the_net10_client_error_table</c> pins it, frozen at .NET 10's since MVC left
    ///     the host (R32, R54).
    /// </summary>
    private static readonly FrozenDictionary<int, (string Type, string Title)> ClientErrors =
        new Dictionary<int, (string Type, string Title)>
        {
            [400] = ("https://tools.ietf.org/html/rfc9110#section-15.5.1", "Bad Request"),
            [401] = ("https://tools.ietf.org/html/rfc9110#section-15.5.2", "Unauthorized"),
            [403] = ("https://tools.ietf.org/html/rfc9110#section-15.5.4", "Forbidden"),
            [404] = ("https://tools.ietf.org/html/rfc9110#section-15.5.5", "Not Found"),
            [405] = ("https://tools.ietf.org/html/rfc9110#section-15.5.6", "Method Not Allowed"),
            [406] = ("https://tools.ietf.org/html/rfc9110#section-15.5.7", "Not Acceptable"),
            [407] = ("https://tools.ietf.org/html/rfc9110#section-15.5.8", "Proxy Authentication Required"),
            [408] = ("https://tools.ietf.org/html/rfc9110#section-15.5.9", "Request Timeout"),
            [409] = ("https://tools.ietf.org/html/rfc9110#section-15.5.10", "Conflict"),
            [410] = ("https://tools.ietf.org/html/rfc9110#section-15.5.11", "Gone"),
            [411] = ("https://tools.ietf.org/html/rfc9110#section-15.5.12", "Length Required"),
            [412] = ("https://tools.ietf.org/html/rfc9110#section-15.5.13", "Precondition Failed"),
            [413] = ("https://tools.ietf.org/html/rfc9110#section-15.5.14", "Content Too Large"),
            [414] = ("https://tools.ietf.org/html/rfc9110#section-15.5.15", "URI Too Long"),
            [415] = ("https://tools.ietf.org/html/rfc9110#section-15.5.16", "Unsupported Media Type"),
            [416] = ("https://tools.ietf.org/html/rfc9110#section-15.5.17", "Range Not Satisfiable"),
            [417] = ("https://tools.ietf.org/html/rfc9110#section-15.5.18", "Expectation Failed"),
            [421] = ("https://tools.ietf.org/html/rfc9110#section-15.5.20", "Misdirected Request"),
            [422] = ("https://tools.ietf.org/html/rfc4918#section-11.2", "Unprocessable Entity"),
            [426] = ("https://tools.ietf.org/html/rfc9110#section-15.5.22", "Upgrade Required"),
            [500] = ("https://tools.ietf.org/html/rfc9110#section-15.6.1", "An error occurred while processing your request."),
            [501] = ("https://tools.ietf.org/html/rfc9110#section-15.6.2", "Not Implemented"),
            [502] = ("https://tools.ietf.org/html/rfc9110#section-15.6.3", "Bad Gateway"),
            [503] = ("https://tools.ietf.org/html/rfc9110#section-15.6.4", "Service Unavailable"),
            [504] = ("https://tools.ietf.org/html/rfc9110#section-15.6.5", "Gateway Timeout"),
            [505] = ("https://tools.ietf.org/html/rfc9110#section-15.6.6", "HTTP Version Not Supported"),
        }.ToFrozenDictionary();

    /// <summary>
    ///     The problem a controller's <c>Problem</c> call, or MVC's client-error filter for a bodiless 4xx, wrote:
    ///     the given members, the client-error <c>type</c> and default <c>title</c>, <c>traceId</c>, then
    ///     <paramref name="extensions"/>.
    /// </summary>
    public static ApiProblemResult Of(
        HttpContext context, int statusCode, string? detail = null, string? title = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = new ProblemDetails { Status = statusCode, Title = title, Detail = detail };
        ApplyDefaults(context, problem, statusCode);
        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return new ApiProblemResult(problem, statusCode, ContentTypeOf(context));
    }

    /// <summary>
    ///     Writes <paramref name="problem"/> exactly as given, with its own <c>Status</c>, the endpoint's content type
    ///     and none of <see cref="Of"/>'s defaults: no <c>type</c>, no <c>traceId</c>. That is what MVC writes for an
    ///     action's explicit <c>NotFound(new ProblemDetails { ... })</c>, which its factory never touches (R43).
    ///     The problem must carry its <c>Status</c>, as the status the response takes: a port sets it as MVC's
    ///     <c>NotFound</c> or <c>BadRequest</c> did, and a problem without one throws <see cref="ArgumentException"/>.
    ///     <see cref="Validation"/> does not delegate here: it answers 400 for a problem with no <c>Status</c> and
    ///     leaves the body without one, which this method would refuse or change.
    /// </summary>
    public static ApiProblemResult AsGiven(HttpContext context, ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);

        var statusCode = problem.Status ?? throw new ArgumentException("The problem has no Status.", nameof(problem));
        return new ApiProblemResult(problem, statusCode, ContentTypeOf(context));
    }

    /// <summary>
    ///     Writes <paramref name="problem"/> as it is, with its own status (400 when it has none) and the endpoint's
    ///     content type. Binding problems arrive with MVC's factory defaults already applied; the ZeroAlloc filter's
    ///     problem keeps its rfc7231 <c>type</c> and no <c>traceId</c>, as the MVC filter writes it.
    /// </summary>
    public static ApiProblemResult Validation(HttpContext context, ValidationProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);

        return new ApiProblemResult(problem, problem.Status ?? StatusCodes.Status400BadRequest, ContentTypeOf(context));
    }

    /// <summary>
    ///     The validation problem MVC's <c>ModelStateInvalidFilter</c> answered for binding errors, through its
    ///     factory's validation problem: status 400, the 400 <c>type</c>, the validation title and <c>traceId</c>.
    /// </summary>
    internal static ValidationProblemDetails BindingProblem(HttpContext context, IDictionary<string, string[]> errors)
    {
        var problem = new ValidationProblemDetails(errors) { Status = StatusCodes.Status400BadRequest };
        ApplyDefaults(context, problem, StatusCodes.Status400BadRequest);
        return problem;
    }

    /// <summary>
    ///     What MVC's default factory applied to every problem: the mapped title and type where none was
    ///     given, the trace id, then the host's <c>CustomizeProblemDetails</c>, which MVC's factory also runs.
    /// </summary>
    private static void ApplyDefaults(HttpContext context, ProblemDetails problem, int statusCode)
    {
        if (ClientErrors.TryGetValue(statusCode, out var clientError))
        {
            problem.Title ??= clientError.Title;
            problem.Type ??= clientError.Type;
        }

        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        if (traceId is not null)
        {
            problem.Extensions["traceId"] = traceId;
        }

        var customize = context.RequestServices?.GetService<IOptions<ProblemDetailsOptions>>()?.Value.CustomizeProblemDetails;
        customize?.Invoke(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }

    private static string ContentTypeOf(HttpContext context) =>
        (context.GetEndpoint()?.Metadata.GetMetadata<ProblemContentType>() ?? ProblemContentType.ProblemJson).ContentType + Charset;
}

/// <summary>
///     A problem body written through the endpoints' Http <c>JsonOptions</c>, so it takes their naming, null handling
///     and relaxed escaping, with the type metadata of <see cref="ApiJsonSerializerContext"/>.
/// </summary>
public sealed class ApiProblemResult : IResult, IStatusCodeHttpResult, IValueHttpResult, IValueHttpResult<ProblemDetails>,
    IContentTypeHttpResult
{
    internal ApiProblemResult(ProblemDetails problemDetails, int statusCode, string contentType)
    {
        ProblemDetails = problemDetails;
        StatusCode = statusCode;
        ContentType = contentType;
    }

    /// <summary>The problem written.</summary>
    public ProblemDetails ProblemDetails { get; }

    /// <summary>The response status.</summary>
    public int StatusCode { get; }

    /// <summary>The whole <c>Content-Type</c> header, charset included.</summary>
    public string ContentType { get; }

    int? IStatusCodeHttpResult.StatusCode => StatusCode;

    object? IValueHttpResult.Value => ProblemDetails;

    ProblemDetails? IValueHttpResult<ProblemDetails>.Value => ProblemDetails;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var options = httpContext.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        httpContext.Response.StatusCode = StatusCode;
        return httpContext.Response.WriteAsJsonAsync(
            ProblemDetails, options.GetTypeInfo(ProblemDetails.GetType()), ContentType, httpContext.RequestAborted);
    }
}
