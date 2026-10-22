using System.Text.Json;
using Daedalus.Agents.Api;
using Microsoft.Extensions.AI;

namespace Daedalus.Tests.Unit.Application.Agents;

/// <summary>
///     The argument JSON <see cref="AgentDtoMapper.ToDtos"/> shows for a tool call, captured from today's reflective
///     <c>Serialize(IDictionary&lt;string, object?&gt;)</c> over M.E.AI's options before phase 3.2 reads the same options'
///     generated metadata. Arguments arrive as <see cref="JsonElement"/>s from a provider and as plain values from the
///     host, so both are pinned.
/// </summary>
public sealed class AgentDtoMapperGoldenTests
{
    private const string Awkward = "<a & 'é' 🚀>";

    /// <summary>
    ///     Red: replace <c>AIJsonUtilities.DefaultOptions</c> with <c>JsonSerializerOptions.Default</c> in
    ///     <c>AgentDtoMapper.CompactJson</c>; the default encoder escapes the characters M.E.AI's relaxed one keeps raw.
    /// </summary>
    [Fact]
    public void Plain_argument_values_are_written_as_captured()
    {
        var json = ArgumentsOf(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text"] = Awkward,
            ["count"] = 3L,
            ["ratio"] = 1.5,
            ["flag"] = true,
            ["none"] = null,
            ["list"] = new[] { "a", Awkward },
        });

        json.Should().Be("""{"text":"<a & 'é' \uD83D\uDE80>","count":3,"ratio":1.5,"flag":true,"none":null,"list":["a","<a & 'é' \uD83D\uDE80>"]}""");
    }

    /// <summary>Red: set <c>WriteIndented = true</c> on <c>AgentDtoMapper.CompactJson</c>; the output gains line breaks.</summary>
    [Fact]
    public void Json_element_argument_values_are_written_as_captured()
    {
        using var document = JsonDocument.Parse("""{"text":"<a & 'é' 🚀>","count":3,"nested":{"b":[1,2.5,null],"a":"é"}}""");
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        var json = ArgumentsOf(arguments);

        json.Should().Be("""{"text":"<a & 'é' \uD83D\uDE80>","count":3,"nested":{"b":[1,2.5,null],"a":"é"}}""");
    }

    private static string? ArgumentsOf(Dictionary<string, object?> arguments)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "workspace__write_file", arguments)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "ok")]),
        };

        var call = AgentDtoMapper.ToDtos(messages).Should().ContainSingle().Which.ToolCalls.Should().ContainSingle().Which;
        call.ResultPreview.Should().Be("ok");
        return call.ArgumentsJson;
    }
}
