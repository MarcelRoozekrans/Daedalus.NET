using System.Net;
using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

/// <summary>A failure message's status text, which must never end in an empty reason.</summary>
public sealed class HttpStatusTextTests
{
    /// <summary>
    ///     A response with a reason phrase reads as its code and that phrase.
    ///     Red: always use the status name; "400 Bad Request" reads "400 BadRequest".
    /// </summary>
    [Fact]
    public void A_reason_phrase_is_kept()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { ReasonPhrase = "Bad Request" };

        HttpStatusText.Of(response).Should().Be("400 Bad Request");
    }

    /// <summary>
    ///     An empty reason, as the browser's fetch reports over HTTP/2, falls back to the status name.
    ///     Red: drop the fallback and use ReasonPhrase as it is; the text reads "400 ".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void An_empty_reason_falls_back_to_the_status_name(string reason)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { ReasonPhrase = reason };

        HttpStatusText.Of(response).Should().Be("400 BadRequest");
    }
}
