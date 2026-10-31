using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Daedalus.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     A <see cref="DefaultHttpContext"/> over the services <see cref="DaedalusEndpoints.AddDaedalusEndpoints"/>
///     registers, with an endpoint carrying the given metadata, and the assertion <c>MvcBehaviourContractTests</c>
///     makes on a problem body, applied to the text written here. The expected values are copied from there.
/// </summary>
internal static class EndpointTestContext
{
    public const string Json = "application/json; charset=utf-8";
    public const string ProblemJson = "application/problem+json; charset=utf-8";

    public const string NotFoundType = "https://tools.ietf.org/html/rfc9110#section-15.5.5";
    public const string UnauthorizedType = "https://tools.ietf.org/html/rfc9110#section-15.5.2";
    public const string BadRequestType = "https://tools.ietf.org/html/rfc9110#section-15.5.1";
    public const string ServiceUnavailableType = "https://tools.ietf.org/html/rfc9110#section-15.6.4";
    public const string UnsupportedMediaTypeType = "https://tools.ietf.org/html/rfc9110#section-15.5.16";
    public const string FilterBadRequestType = "https://tools.ietf.org/html/rfc7231#section-6.5.1";

    public const string ValidationTitle = "One or more validation errors occurred.";
    public const string NonEmptyBody = "A non-empty request body is required.";
    public const string RequestRequired = "The request field is required.";

    public const string TraceIdentifier = "trace-1";

    /// <summary>MVC's relaxed escaping, ruling R19: the expected values are serialized with it.</summary>
    public static readonly JsonSerializerOptions MvcEscaping = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ServiceProvider Services(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection().AddLogging().AddDaedalusEndpoints();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public static DefaultHttpContext Create(IServiceProvider services, params object[] metadata)
    {
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = TraceIdentifier };
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));
        return context;
    }

    /// <summary>A POST whose body is <paramref name="body"/>, as the contract tests send it.</summary>
    public static DefaultHttpContext WithJsonBody(IServiceProvider services, string body, string? contentType = "application/json")
    {
        var context = Create(services);
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = contentType;
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        return context;
    }

    /// <summary>
    ///     Binds <typeparamref name="TSelf"/> through <see cref="IBindableFromHttpContext{TSelf}"/>, as the generated
    ///     request delegate does; the binders implement it explicitly.
    /// </summary>
    public static async Task<TSelf> BindAsync<TSelf>(HttpContext context, System.Reflection.ParameterInfo parameter)
        where TSelf : class, IBindableFromHttpContext<TSelf> =>
        (await TSelf.BindAsync(context, parameter))!;

    public static async Task<string> ExecuteAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        return ReadBody(context);
    }

    public static string ReadBody(HttpContext context)
    {
        var stream = (MemoryStream)context.Response.Body;
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    ///     <c>MvcBehaviourContractTests.AssertProblemAsync</c> over <paramref name="text"/>: the exact member set, then
    ///     every member other than <c>errors</c> and <c>traceId</c> as raw JSON, then <c>errors</c> key for key, then
    ///     <c>traceId</c>. <paramref name="extra"/> holds further members as name and raw JSON.
    /// </summary>
    public static void AssertProblem(
        string text, string? type, string title, int status, string? detail = null,
        IReadOnlyDictionary<string, string[]>? errors = null, string? traceId = TraceIdentifier,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        var expectedValues = new List<string>
        {
            "status=" + status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "title=" + JsonSerializer.Serialize(title, MvcEscaping),
        };
        if (type is not null)
        {
            expectedValues.Add("type=" + JsonSerializer.Serialize(type, MvcEscaping));
        }

        if (detail is not null)
        {
            expectedValues.Add("detail=" + JsonSerializer.Serialize(detail, MvcEscaping));
        }

        foreach (var (name, raw) in extra ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            expectedValues.Add(name + "=" + raw);
        }

        var expectedMembers = expectedValues.Select(value => value[..value.IndexOf('=', StringComparison.Ordinal)]).ToList();
        if (errors is not null)
        {
            expectedMembers.Add("errors");
        }

        if (traceId is not null)
        {
            expectedMembers.Add("traceId");
        }

        root.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal).Should()
            .Equal(expectedMembers.Order(StringComparer.Ordinal), text);

        root.EnumerateObject()
            .Where(member => member.Name is not ("errors" or "traceId"))
            .Select(member => member.Name + "=" + member.Value.GetRawText())
            .Order(StringComparer.Ordinal).Should()
            .Equal(expectedValues.Order(StringComparer.Ordinal), text);

        if (errors is not null)
        {
            root.GetProperty("errors").EnumerateObject()
                .Select(member => member.Name + "=" + member.Value.GetRawText())
                .Order(StringComparer.Ordinal).Should()
                .Equal(errors.Select(error => error.Key + "=" + JsonSerializer.Serialize(error.Value, MvcEscaping))
                    .Order(StringComparer.Ordinal), text);
        }

        if (traceId is not null)
        {
            root.GetProperty("traceId").GetString().Should().Be(traceId, text);
        }
    }
}
