using Daedalus.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="ApiJson"/> writes what the Json factory of <c>TypedResults</c> wrote, through the endpoints' Http
///     <c>JsonOptions</c> and without the reflection-based serializer (R45).
/// </summary>
public sealed class ApiJsonTests
{
    [Fact]
    public async Task Of_writes_the_given_status()
    {
        await using var services = Services();
        var context = Create(services);

        await ExecuteAsync(ApiJson.Of(new ErrorBody("x"), StatusCodes.Status500InternalServerError), context);

        // Red: write the fixed status 200 in ApiJsonResult.ExecuteAsync.
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task Of_writes_json_with_charset()
    {
        await using var services = Services();
        var context = Create(services);

        await ExecuteAsync(ApiJson.Of(new ErrorBody("x"), StatusCodes.Status400BadRequest), context);

        // Red: write application/problem+json, or leave out the charset, as ApiJsonResult.ContentType.
        context.Response.ContentType.Should().Be(Json);
    }

    [Fact]
    public async Task Of_writes_the_bytes_through_the_endpoint_json_options()
    {
        await using var services = Services();
        var context = Create(services);

        var body = await ExecuteAsync(
            ApiJson.Of(new ErrorDetailBody("a<b>&'é", "line"), StatusCodes.Status500InternalServerError), context);

        // Red: serialize with default JsonSerializerOptions; the names are PascalCase and "<" is escaped as <.
        body.Should().Be("{\"error\":\"a<b>&'é\",\"details\":\"line\"}");
    }

    [Fact]
    public void Of_exposes_status_content_type_and_value_as_a_typed_result()
    {
        var value = new ErrorBody("x");

        var result = ApiJson.Of(value, StatusCodes.Status400BadRequest);

        // Red: return null from IValueHttpResult.Value, or drop IStatusCodeHttpResult, so the result is not seen as a 400.
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        ((IValueHttpResult)result).Value.Should().BeSameAs(value);
        ((IContentTypeHttpResult)result).ContentType.Should().Be(Json);
    }
}
