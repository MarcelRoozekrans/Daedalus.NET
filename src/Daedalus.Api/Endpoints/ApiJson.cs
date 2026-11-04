using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     JSON bodies that are not a typed <c>Ok</c>, <c>Created</c> or <c>BadRequest</c> result, such as the
///     <c>{ "error": "…" }</c> bodies of a 400 or 500 (R45).
/// </summary>
/// <remarks>
///     The Json factory of <c>TypedResults</c> is not used: its overload with options reaches the reflection-based serializer and
///     raises IL2026 and IL3050 under the AOT analysis. <see cref="ApiJsonResult{T}"/> writes with the type metadata
///     of <see cref="ApiJsonSerializerContext"/> instead, as <see cref="ApiProblemResult"/> does.
/// </remarks>
public static class ApiJson
{
    /// <summary>A body of <typeparamref name="T"/> written with <paramref name="statusCode"/>.</summary>
    public static ApiJsonResult<T> Of<T>(T value, int statusCode) => new(value, statusCode);
}

/// <summary>
///     A JSON body written through the endpoints' Http <c>JsonOptions</c>, with the status it is given and
///     <c>application/json; charset=utf-8</c>, as the Json factory of <c>TypedResults</c> wrote it. It declares no response
///     metadata, so a union containing it adds nothing to the endpoint's declared responses.
/// </summary>
public sealed class ApiJsonResult<T> : IResult, IStatusCodeHttpResult, IValueHttpResult, IValueHttpResult<T>,
    IContentTypeHttpResult
{
    internal ApiJsonResult(T value, int statusCode)
    {
        Value = value;
        StatusCode = statusCode;
    }

    /// <summary>The body written.</summary>
    public T Value { get; }

    /// <summary>The response status.</summary>
    public int StatusCode { get; }

    /// <summary>The whole <c>Content-Type</c> header, charset included.</summary>
    public string ContentType => "application/json; charset=utf-8";

    int? IStatusCodeHttpResult.StatusCode => StatusCode;

    object? IValueHttpResult.Value => Value;

    T? IValueHttpResult<T>.Value => Value;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var options = httpContext.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        httpContext.Response.StatusCode = StatusCode;
        return httpContext.Response.WriteAsJsonAsync(
            Value, options.GetTypeInfo(typeof(T)), ContentType, httpContext.RequestAborted);
    }
}
