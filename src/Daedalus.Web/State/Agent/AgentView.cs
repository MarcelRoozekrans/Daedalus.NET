namespace Daedalus.Web.State.Agent;

/// <summary>
///     What the Agent page shows of <see cref="AgentState"/>, and when it re-renders. Pure, so the render throttle is
///     unit-tested: the page has no component renderer, and a browser cannot count renders.
/// </summary>
public static class AgentView
{
    /// <summary>How many memories the panel reads a page.</summary>
    public const int MemoriesPageSize = 20;

    /// <summary>
    ///     How often a streaming reply's text may re-render. Text arrives a token at a time; rendering each would
    ///     re-render the whole transcript per token.
    /// </summary>
    public static TimeSpan RenderInterval { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    ///     Whether the page renders <paramref name="current"/>, having last rendered <paramref name="rendered"/>
    ///     <paramref name="sinceRender"/> ago. While the reply's text alone has grown since the last render, every
    ///     render waits for <see cref="RenderInterval"/>, including one the page's own input asks for, such as a click
    ///     on Memories: the held text and the input both show with the next render, at the next stream event after
    ///     the interval or at the end of the turn. Every other change renders at once: a tool card, the usage, the end
    ///     of the turn, and a render with nothing in the store changed since the last one. The decision compares the
    ///     reply's chunk lists by reference, never its text.
    /// </summary>
    public static bool ShouldRender(AgentState rendered, AgentState current, TimeSpan sinceRender) =>
        sinceRender >= RenderInterval || !OnlyTheReplyTextGrew(rendered, current);

    /// <summary>The transcript the page shows: the finished messages, then the reply streaming in.</summary>
    public static IReadOnlyList<AgentChatMessage> Messages(AgentState state) =>
        state.Reply is { } reply ? state.Transcript.Add(reply) : state.Transcript;

    /// <summary>Whether <paramref name="current"/> differs from <paramref name="rendered"/> in the reply's text alone.</summary>
    private static bool OnlyTheReplyTextGrew(AgentState rendered, AgentState current) =>
        rendered.Reply is { } before
        && current.Reply is { } after
        && !rendered.Equals(current)
        && rendered.Equals(current with { Reply = after with { Content = before.Content } });
}
