using System.Text.Json.Nodes;
using Daedalus.Api.Endpoints;
using Microsoft.OpenApi;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="OpenApiQueryParameters.Describe"/> gives each type the schema MVC's ApiExplorer emitted for a query
///     parameter of it, read from the Development document before the port: <c>in</c>, type, format, pattern and
///     default, and never <c>required</c>.
/// </summary>
public sealed class OpenApiQueryParametersTests
{
    [Fact]
    public void Int_is_integer_or_string_with_format_pattern_and_default()
    {
        var parameter = OpenApiQueryParameters.Describe<int>("take", JsonValue.Create(20));

        // Red: Type = JsonSchemaType.Integer alone, Format "int64", another pattern, or ParameterLocation.Path.
        parameter.Name.Should().Be("take");
        parameter.In.Should().Be(ParameterLocation.Query);
        parameter.Required.Should().BeFalse();
        parameter.Schema!.Type.Should().Be(JsonSchemaType.Integer | JsonSchemaType.String);
        parameter.Schema.Format.Should().Be("int32");
        parameter.Schema.Pattern.Should().Be("^-?(?:0|[1-9]\\d*)$");
        // Red: Default = null in Describe, or the wrong default node.
        parameter.Schema.Default!.GetValue<int>().Should().Be(20);
    }

    [Fact]
    public void Int_without_a_default_has_none()
    {
        // Red: always set Default, for instance to JsonValue.Create(0).
        OpenApiQueryParameters.Describe<int>("take", null).Schema!.Default.Should().BeNull();
    }

    [Fact]
    public void Bool_is_boolean_with_its_default()
    {
        var parameter = OpenApiQueryParameters.Describe<bool>("hard", JsonValue.Create(false));

        // Red: give bool the integer schema.
        parameter.Schema!.Type.Should().Be(JsonSchemaType.Boolean);
        parameter.Schema.Format.Should().BeNull();
        parameter.Schema.Pattern.Should().BeNull();
        parameter.Schema.Default!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Guid_is_a_uuid_string()
    {
        var parameter = OpenApiQueryParameters.Describe<Guid>("projectId", null);

        // Red: Format "guid" or no format for Guid.
        parameter.Schema!.Type.Should().Be(JsonSchemaType.String);
        parameter.Schema.Format.Should().Be("uuid");
    }

    [Fact]
    public void String_is_a_plain_string()
    {
        var parameter = OpenApiQueryParameters.Describe<string>("agentId", null);

        // Red: give string a format.
        parameter.Schema!.Type.Should().Be(JsonSchemaType.String);
        parameter.Schema.Format.Should().BeNull();
    }

    [Fact]
    public void An_unsupported_type_is_refused()
    {
        // Red: return a plain string schema for any other type.
        var act = () => OpenApiQueryParameters.Describe<DateTime>("since", null);

        act.Should().Throw<NotSupportedException>();
    }
}
