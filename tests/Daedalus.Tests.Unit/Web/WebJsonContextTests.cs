using System.Text.Json;
using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     What <see cref="WebJsonContext"/> writes that <see cref="WebClientGoldenTests"/> cannot reach through a client: the
///     body of the writes that send none.
/// </summary>
public sealed class WebJsonContextTests
{
    /// <summary>
    ///     Characterization test: it has no earlier failing form, because <c>EmptyBody</c> is new, and it passes on first
    ///     run. It pins the bytes the anonymous <c>new { }</c> posted before. Red: give <c>EmptyBody</c> one property,
    ///     say <c>public int Flag { get; init; }</c>; it writes <c>{"flag":0}</c> and this fails, as do the three
    ///     brainstorm and schedule rows of the golden that post it.
    /// </summary>
    [Fact]
    public void An_empty_body_still_posts_braces()
    {
        var json = JsonSerializer.Serialize(EmptyBody.Instance, WebJsonContext.Default.EmptyBody);

        json.Should().Be("{}");
    }
}
