using System.Net;

namespace Daedalus.Tests.Unit.Agents.GitHub;

/// <summary>
///     Records every outgoing request and replies from a queue keyed by path fragment, so a test can assert on what
///     was actually sent rather than only on what it chose to return.
/// </summary>
/// <remarks>
///     Each body is read when the request is sent. A ZeroAlloc.Rest client disposes the request, and its content, as soon
///     as the call returns, so a body read after the call would throw <see cref="ObjectDisposedException"/>.
/// </remarks>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string?> _bodies = [];
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    /// <summary>The body of each request in <see cref="Requests"/>, or <see langword="null"/> for one without content.</summary>
    public IReadOnlyList<string?> Bodies => _bodies;

    public StubHandler Route(string containing, HttpStatusCode status, string json)
    {
        _routes[containing] = () => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        return this;
    }

    public StubHandler Route(string containing, Func<HttpResponseMessage> respond)
    {
        _routes[containing] = respond;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);
        _bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        foreach (var (fragment, respond) in _routes)
        {
            if (request.RequestUri!.PathAndQuery.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return respond();
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}""", System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
