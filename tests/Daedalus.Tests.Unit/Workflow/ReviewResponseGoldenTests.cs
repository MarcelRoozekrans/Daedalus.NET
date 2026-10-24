using System.Text.Json;
using Daedalus.Agents.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Review Focus 2: the bytes <see cref="ReviewResponseRecord.ToPayloadJson"/> writes for every value shape a node can
///     report, captured from today's reflective <c>Serialize(Dictionary&lt;string, object?&gt;)</c> before phase 3.2 moves
///     it to a context. The shapes are the ones Thalos hands a node's variables back as: a string, a list, a whole
///     number as <see cref="long"/>, a fraction as <see cref="double"/>, a bool, null and a nested map, plus a
///     <see cref="JsonElement"/>. The value itself is written by Thalos' <c>WorkflowVariableJson</c>, so these goldens also
///     guard the dependency's bytes across a Thalos bump.
/// </summary>
public sealed class ReviewResponseGoldenTests
{
    private const string Awkward = "<a & 'é' 🚀>";

    public static TheoryData<string, object?> Shapes() => new()
    {
        { "string", Awkward },
        { "string list", new List<object?> { "a", "b" } },
        { "string array", p2 },
        { "long", 3L },
        { "double", 1.5 },
        { "bool", true },
        { "null", null },
        {
            "nested map", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = Awkward,
                ["count"] = 3L,
                ["ratio"] = 1.5,
                ["done"] = false,
                ["none"] = null,
                ["items"] = new List<object?> { "x", 2L, new Dictionary<string, object?>(StringComparer.Ordinal) { ["deep"] = true } },
            }
        },
        { "json element", JsonDocument.Parse("""{"b":[1,2.5,"<"],"a":null}""").RootElement.Clone() },
    };

    /// <summary>
    ///     Red: serialise through <c>new DaedalusRecordsJsonContext(new JsonSerializerOptions { Encoder =
    ///     JavaScriptEncoder.UnsafeRelaxedJsonEscaping })</c> in <c>ToPayloadJson</c>; the string, nested-map and
    ///     JSON-element shapes turn their escapes raw. Red: a Thalos release whose <c>WorkflowVariableJson</c> writes a
    ///     <see cref="long"/> through <c>WriteStringValue</c>; the long and nested-map shapes write their numbers as
    ///     strings.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_reported_shape_is_written_as_captured(string shape, object? value)
    {
        var payload = ReviewResponseRecord.ToPayloadJson(value);

        payload.Should().Be(Expected[shape], shape);
    }

    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["string"] = """{"response":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"}""",
        ["string list"] = """{"response":["a","b"]}""",
        ["string array"] = """{"response":["a","b"]}""",
        ["long"] = """{"response":3}""",
        ["double"] = """{"response":1.5}""",
        ["bool"] = """{"response":true}""",
        ["null"] = """{"response":null}""",
        ["nested map"] = """{"response":{"text":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E","count":3,"ratio":1.5,"done":false,"none":null,"items":["x",2,{"deep":true}]}}""",
        ["json element"] = """{"response":{"b":[1,2.5,"\u003C"],"a":null}}""",
    };
    private static readonly string[] p2 = new[] { "a", "b" };
}
