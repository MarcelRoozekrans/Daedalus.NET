using System.Text.Json;
using System.Text.Json.Nodes;
using Daedalus.Agents.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Review Focus 2, the half <see cref="ReviewResponseGoldenTests"/> does not pin: <see cref="ReviewResponseRecord.ToPayloadJson"/>
///     writes a reported value through Thalos' JSON-shaped variable contract, <c>WorkflowVariableJson.ToElement</c>, rather
///     than through the reflection-based serializer. So a value outside the contract is refused naming its type, on the JIT
///     as well as native, and the leaf types the goldens do not cover still write the bytes the serializer wrote. Written
///     against <c>ToPayloadJson</c>, not the 0.18.0 stand-in, so they keep holding when Task 20 swaps in the Thalos call.
/// </summary>
public sealed class ReviewResponsePayloadContractTests
{
    private const string Allowed = "Allowed: string, number, bool, null, JsonElement, JsonNode, and lists or dictionaries of those.";

    // Red: in ToPayloadJson, build the element with JsonSerializer.SerializeToElement instead of through
    // PlainJson; the Guid is written as a string and nothing throws.
    [Fact]
    public void ToPayloadJson_refuses_a_non_JSON_value_naming_its_type()
    {
        var write = () => ReviewResponseRecord.ToPayloadJson(Guid.Empty);

        write.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith($"Workflow variable '$' has type 'System.Guid', which is not JSON-shaped. {Allowed}");
    }

    // Red: in PlainJson, name every refusal at the root; the message names '$' instead of '[1].meta'.
    [Fact]
    public void A_nested_refusal_names_the_path_to_the_value()
    {
        var value = new List<object?> { "a", new Dictionary<string, object?>(StringComparer.Ordinal) { ["meta"] = Guid.Empty } };

        var write = () => ReviewResponseRecord.ToPayloadJson(value);

        write.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith($"Workflow variable '[1].meta' has type 'System.Guid', which is not JSON-shaped. {Allowed}");
    }

    // Red: in PlainJson, drop the double.IsFinite and float.IsFinite check; the writer then throws its own
    // ArgumentException, whose message is not this one.
    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(float.NegativeInfinity, "-Infinity")]
    public void A_non_finite_number_is_refused(object value, string written)
    {
        var write = () => ReviewResponseRecord.ToPayloadJson(value);

        write.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith($"Workflow variable '$' is {written}, which is not a JSON number. A float or double must be finite.");
    }

    // Red: raise PlainJson's depth guard from 64 to 65; JsonDocument then refuses the 65 levels with a JsonException.
    [Fact]
    public void A_value_nested_deeper_than_64_levels_is_refused()
    {
        object? value = "leaf";
        for (var i = 0; i < 65; i++)
            value = new List<object?> { value };

        var write = () => ReviewResponseRecord.ToPayloadJson(value);

        write.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("nests deeper than 64 levels");
    }

    // Red: in PlainJson, read an IDictionary entry's key with Convert.ToString instead of refusing a non-string key, and
    // the dictionary is then written as an object keyed "1" instead of being refused.
    [Fact]
    public void A_dictionary_with_non_string_keys_is_refused_naming_the_dictionary()
    {
        var value = new Dictionary<int, string> { [1] = "a" };

        var write = () => ReviewResponseRecord.ToPayloadJson(value);

        write.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith($"Workflow variable '$' has type '{typeof(Dictionary<int, string>).FullName}', which is not JSON-shaped.");
    }

    public static TheoryData<string, object> Leaves() => new()
    {
        { "byte", (byte)7 },
        { "sbyte", (sbyte)-7 },
        { "short", (short)-300 },
        { "ushort", (ushort)300 },
        { "int", -70_000 },
        { "uint", 4_000_000_000u },
        { "ulong", ulong.MaxValue },
        { "float", 1.5f },
        { "whole double", 2.0 },
        { "decimal", 1.50m },
        { "bytes", new byte[] { 1, 2, 3 } },
        { "json node", JsonNode.Parse("""{"b":[1,"<"],"a":null}""")! },
        { "typed dictionary", new Dictionary<string, int>(StringComparer.Ordinal) { ["b"] = 2, ["a"] = 1 } },
        { "sorted map", new SortedList<string, object?>(StringComparer.Ordinal) { ["z"] = "last", ["a"] = 1L } },
    };

    // Red: in PlainJson, write every number through WriteNumberValue(double); the decimal loses its trailing zero and
    // ulong.MaxValue is written as 1.8446744073709552E+19. Red: write byte[] as a sequence; the bytes are written as
    // [1,2,3]. Red: drop the JsonNode case; the node is refused as not JSON-shaped.
    [Theory]
    [MemberData(nameof(Leaves))]
    public void A_leaf_or_container_the_goldens_skip_is_written_as_the_serializer_wrote_it(string shape, object value)
    {
        var payload = ReviewResponseRecord.ToPayloadJson(value);

        payload.Should().Be(ExpectedLeaves[shape], shape);
    }

    // Captured from today's reflection-based Serialize of the same Dictionary<string, object?> wrapper.
    private static readonly Dictionary<string, string> ExpectedLeaves = new(StringComparer.Ordinal)
    {
        ["byte"] = """{"response":7}""",
        ["sbyte"] = """{"response":-7}""",
        ["short"] = """{"response":-300}""",
        ["ushort"] = """{"response":300}""",
        ["int"] = """{"response":-70000}""",
        ["uint"] = """{"response":4000000000}""",
        ["ulong"] = """{"response":18446744073709551615}""",
        ["float"] = """{"response":1.5}""",
        ["whole double"] = """{"response":2}""",
        ["decimal"] = """{"response":1.50}""",
        ["bytes"] = """{"response":"AQID"}""",
        ["json node"] = """{"response":{"b":[1,"\u003C"],"a":null}}""",
        ["typed dictionary"] = """{"response":{"b":2,"a":1}}""",
        ["sorted map"] = """{"response":{"a":1,"z":"last"}}""",
    };
}
