using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Re-declares a query parameter in the OpenAPI document. A parameter bound through <see cref="ApiQuery{T}"/>
///     contributes none of its own (R30), so each port declares the ones its controller had, here, and not with a
///     lambda per parameter. The schema is what MVC's ApiExplorer emitted for the type: an <c>int</c> is
///     <c>integer|string</c> with format <c>int32</c> and an integer pattern, a <c>bool</c> is <c>boolean</c>, a
///     <c>Guid</c> is a <c>uuid</c> string, a <c>string</c> is <c>string</c>; a parameter is never <c>required</c>
///     and carries <c>default</c> only when the controller gave one. These are the types the controllers' remaining
///     <c>[FromQuery]</c> parameters use.
/// </summary>
internal static class OpenApiQueryParameters
{
    /// <summary>Declares query parameter <paramref name="name"/> of type <typeparamref name="T"/> with no default.</summary>
    public static RouteHandlerBuilder WithQueryParameter<T>(this RouteHandlerBuilder builder, string name) =>
        Declare(builder, Describe<T>(name, null));

    /// <summary>Declares query parameter <paramref name="name"/> with the controller parameter's default.</summary>
    public static RouteHandlerBuilder WithQueryParameter<T>(this RouteHandlerBuilder builder, string name, T defaultValue)
        where T : struct =>
        Declare(builder, Describe<T>(name, DefaultNode(defaultValue)));

    /// <summary>The parameter MVC's ApiExplorer described for a <typeparamref name="T"/> query parameter.</summary>
    internal static OpenApiParameter Describe<T>(string name, JsonNode? defaultValue)
    {
        var schema = typeof(T) switch
        {
            var t when t == typeof(int) => new OpenApiSchema
            {
                Pattern = "^-?(?:0|[1-9]\\d*)$",
                Type = JsonSchemaType.Integer | JsonSchemaType.String,
                Format = "int32",
            },
            var t when t == typeof(bool) => new OpenApiSchema { Type = JsonSchemaType.Boolean },
            var t when t == typeof(Guid) => new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
            var t when t == typeof(string) => new OpenApiSchema { Type = JsonSchemaType.String },
            _ => throw new NotSupportedException($"No OpenAPI query schema is defined for {typeof(T)}."),
        };
        schema.Default = defaultValue;

        return new OpenApiParameter { Name = name, In = ParameterLocation.Query, Schema = schema };
    }

    private static JsonValue DefaultNode<T>(T value) =>
        value switch
        {
            int number => JsonValue.Create(number),
            bool flag => JsonValue.Create(flag),
            Guid id => JsonValue.Create(id),
            _ => throw new NotSupportedException($"No OpenAPI query default is defined for {typeof(T)}."),
        };

    private static RouteHandlerBuilder Declare(RouteHandlerBuilder builder, OpenApiParameter parameter) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(parameter);
            return Task.CompletedTask;
        });
}
