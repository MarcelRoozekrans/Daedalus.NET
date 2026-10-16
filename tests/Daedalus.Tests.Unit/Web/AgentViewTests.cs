using Daedalus.Application.DTOs.Agents;
using Daedalus.Web.State.Agent;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The Agent page's render throttle: a long streamed answer re-renders at most every 50 ms for its text, while
///     every other change renders at once, as the page did before it moved onto the store.
/// </summary>
public sealed class AgentViewTests
{
    private static readonly AgentState Streaming = AgentReducers.On(
        AgentReducers.On(AgentState.Empty, new AgentSessionOpened("a", 1)), new AgentTurnStarted("a", 2, "hi"));

    private static AgentState Delta(AgentState state, string text) =>
        AgentReducers.On(state, new AgentTurnEvent("a", 2, new AgentEventDto("text-delta", Text: text)));

    /// <summary>
    ///     Text that grew within 50 ms of the last render waits; at 50 ms it renders.
    ///     Red: return true always from AgentView.ShouldRender; every token re-renders.
    ///     Red: compare with &gt; instead of &gt;= against RenderInterval in AgentView.ShouldRender; 50 ms waits.
    /// </summary>
    [Fact]
    public void Text_that_grew_within_the_interval_waits()
    {
        var rendered = Delta(Streaming, "Hel");
        var grown = Delta(rendered, "lo");

        AgentView.ShouldRender(rendered, grown, TimeSpan.FromMilliseconds(49)).Should().BeFalse();
        AgentView.ShouldRender(rendered, grown, AgentView.RenderInterval).Should().BeTrue();
    }

    /// <summary>
    ///     A long answer streamed token by token every millisecond renders about once per 50 ms, not once per token.
    ///     Red: return true always from AgentView.ShouldRender; 1000 renders.
    /// </summary>
    [Fact]
    public void A_long_answer_renders_once_per_interval_not_per_token()
    {
        var rendered = Streaming;
        var current = Streaming;
        var sinceRender = TimeSpan.Zero;
        var renders = 0;
        for (var token = 0; token < 1000; token++)
        {
            current = Delta(current, "x");
            sinceRender += TimeSpan.FromMilliseconds(1);
            if (AgentView.ShouldRender(rendered, current, sinceRender))
            {
                renders++;
                rendered = current;
                sinceRender = TimeSpan.Zero;
            }
        }

        renders.Should().Be(20);
    }

    /// <summary>
    ///     Anything but grown text renders at once: a tool call, the end of the turn, and a state with nothing changed,
    ///     which is the page's own input.
    ///     Red: drop the last clause, the equality with the old content put back, in
    ///     AgentView.OnlyTheReplyTextGrew; a tool call within 50 ms waits.
    ///     Red: return !rendered.Equals(current) from AgentView.OnlyTheReplyTextGrew, so any change counts as text; the
    ///     tool call and the end of the turn wait.
    ///     Red: drop the !rendered.Equals(current) clause in AgentView.OnlyTheReplyTextGrew; a render with nothing in the
    ///     store changed, such as a click while streaming, waits.
    /// </summary>
    [Fact]
    public void Every_other_change_renders_at_once()
    {
        var rendered = Delta(Streaming, "Hel");
        var tool = AgentReducers.On(rendered, new AgentTurnEvent("a", 2,
            new AgentEventDto("tool-call", ToolCall: new AgentToolCallDto("c", "tool", null, null))));
        var ended = AgentReducers.On(rendered, new AgentTurnEnded("a", 2, null));
        var soon = TimeSpan.FromMilliseconds(1);

        AgentView.ShouldRender(rendered, tool, soon).Should().BeTrue();
        AgentView.ShouldRender(rendered, ended, soon).Should().BeTrue();
        AgentView.ShouldRender(rendered, rendered, soon).Should().BeTrue();
    }

    /// <summary>
    ///     The transcript the page shows is the finished messages, then the reply streaming in.
    ///     Red: return Transcript alone from AgentView.Messages; the streaming reply is not shown.
    /// </summary>
    [Fact]
    public void The_messages_end_with_the_streaming_reply()
    {
        AgentView.Messages(Delta(Streaming, "Hel")).Select(m => m.Text).Should().Equal("hi", "Hel");
    }
}
