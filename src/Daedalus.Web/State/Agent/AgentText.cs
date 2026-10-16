namespace Daedalus.Web.State.Agent;

/// <summary>
///     A bubble's text as the chunks it streamed in: an immutable list linked back to front, so appending a token is
///     O(1) and copies nothing, where concatenating would copy the whole reply on every token. The string is built only
///     when the page renders it, in one pass of <see cref="Length"/>, and kept for later renders.
///     <para>
///         Equality is by reference: a new chunk makes a new instance, and the render throttle compares instances, never
///         strings.
///     </para>
/// </summary>
public sealed class AgentText
{
    private readonly AgentText? _previous;
    private readonly string _chunk;
    private string? _text;

    private AgentText(AgentText? previous, string chunk, int length)
    {
        _previous = previous;
        _chunk = chunk;
        Length = length;
    }

    /// <summary>No text.</summary>
    public static AgentText Empty { get; } = new(null, string.Empty, 0);

    /// <summary>The text's length in characters.</summary>
    public int Length { get; }

    /// <summary>Whole text <paramref name="text"/>, such as a stored message.</summary>
    public static AgentText From(string? text) => Empty.Append(text);

    /// <summary>This text followed by <paramref name="chunk"/>; this instance is unchanged.</summary>
    public AgentText Append(string? chunk) =>
        string.IsNullOrEmpty(chunk) ? this : new AgentText(this, chunk, Length + chunk.Length);

    /// <summary>The text: built once, from the last chunk back to the first, then kept.</summary>
    public override string ToString() => _text ??= Build();

    private string Build() =>
        string.Create(Length, this, static (span, last) =>
        {
            var end = span.Length;
            for (var node = last; node is not null; node = node._previous)
            {
                end -= node._chunk.Length;
                node._chunk.AsSpan().CopyTo(span[end..]);
            }
        });
}
