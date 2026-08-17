using System.Runtime.CompilerServices;
using System.Text;

namespace Daedalus.Web.Services;

/// <summary>
///     Minimal Server-Sent Events parser: yields one <c>(Event, Data)</c> tuple per blank-line-terminated block. Multi-line
///     <c>data:</c> fields are joined with <c>\n</c>, comment lines (<c>:</c>) and unknown fields (<c>id:</c>, <c>retry:</c>)
///     are ignored, and a final block that is not terminated by a blank line is still yielded when the stream ends.
///     Hand-rolled because the browser <c>EventSource</c> can neither POST nor send bearer tokens.
/// </summary>
public static class SseReader
{
    /// <summary>Reads SSE blocks from <paramref name="stream"/> until it ends or <paramref name="ct"/> is cancelled.</summary>
    public static async IAsyncEnumerable<(string? Event, string Data)> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? evt = null;
        var data = new StringBuilder();
        var hasData = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                // Dispatch: per spec a block without any data field is dropped (but still resets the event name).
                if (hasData)
                {
                    yield return (evt, data.ToString());
                }

                evt = null;
                data.Clear();
                hasData = false;
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon == 0)
            {
                continue; // comment line
            }

            string field;
            string value;
            if (colon < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (value.StartsWith(' '))
                {
                    value = value[1..]; // the spec strips exactly one leading space
                }
            }

            switch (field)
            {
                case "event":
                    evt = value;
                    break;
                case "data":
                    if (hasData)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    hasData = true;
                    break;
                default:
                    break; // id, retry, anything else: ignored
            }
        }

        if (hasData)
        {
            yield return (evt, data.ToString());
        }
    }
}
