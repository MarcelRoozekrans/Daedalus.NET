using System.Text.Json;
using System.Text.Json.Nodes;
using Daedalus.Agents.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Review Focus 2, the half <see cref="ReviewResponseGoldenTests"/> does not pin: <see cref="ReviewResponseRecord.ToPayloadJson"/>
///     writes a reported value through Thalos' JSON-shaped variable contract, <c>WorkflowVariableJson.ToElement</c>, rather
///     than through the reflection-based serializer. So a value outside the contract is refused naming its type, on the JIT
///     as well as native, and the leaf types the goldens do not cover still write the bytes the serializer wrote. Written
///     against <c>ToPayloadJson</c>, not the contract itself, so they hold whichever implementation writes the value.
///     The contract is Thalos' code, so these tests guard the dependency's refusals and bytes across a Thalos bump: a Red
///     that names the contract names a Thalos release that would break it.
/// </summary>
public sealed class ReviewResponsePayloadContractTests
{
    private const string Allowed = "Allowed: string, number, bool, null, JsonElement, JsonNode, and lists or dictionaries of those.";

    // Red: in ToPayloadJson, build the element with JsonSerializer.SerializeToElement instead of through
    // WorkflowVariableJson.ToElement; the Guid is written as a string and nothing throws.
    [Fact]
    public void ToPayloadJson_refuses_a_non_JSON_value_naming_its_type()
    {
        var write = () => ReviewResponseRecord.ToPayloadJson(Guid.Empty);

        write.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith($"Workflow variable '$' has type 'System.Guid', which is not JSON-shaped. {Allowed}");
    }

    // Red: a Thalos release whose WorkflowVariableJson names every refusal at the root; the message names '$' instead
    // of '[1].meta'.
    [Fact]
    public void A_nested_refusal_names_the_path_to_the_value()
    {
        var value = new List<object?> { "a", new Dictionary<string, object?>(StringComparer.Ordinal) { ["meta"] = Guid.Empty } };

        var write = () => ReviewResponseRecord.ToPayloadJson(value);

        write.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith($"Workflow variable '[1].meta' has type 'System.Guid', which is not JSON-shaped. {Allowed}");
    }

    // Red: a Thalos release whose WorkflowVariableJson drops its finite check on float and double; the writer then
    // throws its own ArgumentException, whose message is not this one.
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

    // Red: a Thalos release whose WorkflowVariableJson allows 65 levels; JsonDocument then refuses them with a
    // JsonException.
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

    // Red: a Thalos release whose WorkflowVariableJson converts a non-string IDictionary key to a string instead of
    // refusing it; the dictionary is then written as an object keyed "1".
    [Fact]
    public void A_dictionary_with_non_string_keys_is_refused_naming_the_dictionary()
    {
        var value = new Dictionary<int, string> { [1] = "a" };

        var write = () => ReviewResponseRecord.ToPayloadJson(value);

        write.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith($"Workflow variable '$' has type '{typeof(Dictionary<int, string>).FullName}', which is not JSON-shaped.");
    }

    // Each number and leaf type, and a typed dictionary, is one the contract accepts. Red, for any case: a Thalos
    // release whose WorkflowVariableJson no longer accepts that type; the case is refused as not JSON-shaped. Seen
    // failing for sbyte, ushort, uint, float, double and the IDictionary arm, against the copy Daedalus kept before
    // Thalos 0.19.0.
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
    };

    // Red: a Thalos release whose WorkflowVariableJson writes every number as a double; the decimal loses its trailing
    // zero and ulong.MaxValue is written as 1.8446744073709552E+19. Red: one that writes a byte[] as a sequence; the
    // bytes are written as [1,2,3]. Red: one that drops JsonNode; the node is refused as not JSON-shaped.
    [Theory]
    [MemberData(nameof(Leaves))]
    public void A_leaf_or_container_the_goldens_skip_is_written_as_the_serializer_wrote_it(string shape, object value)
    {
        var payload = ReviewResponseRecord.ToPayloadJson(value);

        payload.Should().Be(ExpectedLeaves[shape], shape);
    }

    // A map that is only an IReadOnlyDictionary, as a custom bag type would be, is written as an object in its own
    // entry order. Red: a Thalos release whose WorkflowVariableJson writes an IReadOnlyDictionary only as a
    // sequence; the map then enumerates as key-value pairs and is refused, naming the map's type.
    [Fact]
    public void A_read_only_dictionary_is_written_as_an_object_in_entry_order()
    {
        var map = new ReadOnlyMap([new("z", "last"), new("a", 1L)]);

        var payload = ReviewResponseRecord.ToPayloadJson(map);

        payload.Should().Be("""{"response":{"z":"last","a":1}}""");
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
    };

    /// <summary>A map that implements <see cref="IReadOnlyDictionary{TKey,TValue}"/> and nothing more dictionary-like.</summary>
    private sealed class ReadOnlyMap(IReadOnlyList<KeyValuePair<string, object?>> entries) : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => entries.First(e => string.Equals(e.Key, key, StringComparison.Ordinal)).Value;

        public IEnumerable<string> Keys => entries.Select(e => e.Key);

        public IEnumerable<object?> Values => entries.Select(e => e.Value);

        public int Count => entries.Count;

        public bool ContainsKey(string key) => entries.Any(e => string.Equals(e.Key, key, StringComparison.Ordinal));

        public bool TryGetValue(string key, out object? value)
        {
            var at = entries.ToList().FindIndex(e => string.Equals(e.Key, key, StringComparison.Ordinal));
            value = at >= 0 ? entries[at].Value : null;
            return at >= 0;
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => entries.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
