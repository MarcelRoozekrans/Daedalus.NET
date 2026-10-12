using Daedalus.Api.Controllers;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>The list's opaque cursor round-trips exactly and refuses anything it did not write.</summary>
public sealed class WorkflowRunCursorCodecTests
{
    /// <summary>
    ///     Encode then decode gives the same instant to the tick and the same id, in a URL-safe string. The id's bytes are
    ///     all 0xFB, which standard base64 writes as '+' and '/', so the URL-safe check cannot pass by luck.
    ///     Red: encode CreatedAt.UtcDateTime.Ticks truncated to seconds. Red: use standard base64 with '+' and '/'.
    /// </summary>
    [Fact]
    public void A_cursor_round_trips()
    {
        var id = new Guid(0xfbfbfbfb, 0xfbfb, 0xfbfb, 0xfb, 0xfb, 0xfb, 0xfb, 0xfb, 0xfb, 0xfb, 0xfb);
        var cursor = new WorkflowRunCursor(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero).AddTicks(1234567), id);

        var text = WorkflowRunCursorCodec.Encode(cursor);

        text.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        WorkflowRunCursorCodec.TryDecode(text, out var back).Should().BeTrue();
        back.Should().Be(cursor);
    }

    /// <summary>
    ///     Text that is not 24 bytes of base64url is refused, and so are 24 bytes whose ticks no instant has: all 0xFF
    ///     reads as -1.
    ///     Red: accept any length. Red: let a FormatException escape.
    ///     Red: drop the tick range check, so the DateTimeOffset constructor throws.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    [InlineData("________________________________")]
    public void Anything_else_is_refused(string text) =>
        WorkflowRunCursorCodec.TryDecode(text, out _).Should().BeFalse();
}
