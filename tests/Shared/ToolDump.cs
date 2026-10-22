using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Daedalus.Tests.Shared;

/// <summary>
///     One line per tool, <c>name | description | JsonSchema | ReturnJsonSchema</c>, schemas as compact JSON, in the order
///     the tools were given. The oracle format of phase 3.2: the reflective registration is dumped once into a literal, and
///     every later registration of the same tools must dump to the same text. Linked into each test project that pins tools.
/// </summary>
internal static class ToolDump
{
    /// <summary>The dump of <paramref name="tools"/>, lines joined with <c>\n</c>.</summary>
    public static string Dump(IReadOnlyList<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return string.Join('\n', tools.Select(Line));
    }

    /// <summary>One tool's line. A newline inside a description is written as <c>\n</c>, so a tool stays one line.</summary>
    public static string Line(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var description = (tool.Description ?? "").Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        if (tool is not AIFunction function)
        {
            return $"{tool.Name} | {description} | - | -";
        }

        var returns = function.ReturnJsonSchema is { } schema ? Compact(schema) : "null";
        return $"{function.Name} | {description} | {Compact(function.JsonSchema)} | {returns}";
    }

    /// <summary>
    ///     What a tool's invocation returned, as the bytes a provider would be handed: a <see cref="JsonElement"/> as its raw
    ///     text, escaping included, and anything else prefixed with its type so a change of type shows.
    /// </summary>
    public static string Result(object? result) => result switch
    {
        null => "null",
        JsonElement json => json.GetRawText(),
        string text => "string:" + text,
        _ => result.GetType().FullName + ":" + result,
    };

    private static string Compact(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
