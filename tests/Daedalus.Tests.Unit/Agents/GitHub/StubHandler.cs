using System.Net;

namespace Daedalus.Tests.Unit.Agents.GitHub;

/// <summary>
///     Records every outgoing request and replies from a queue keyed by path fragment, so a test can assert on what
///     was actually sent rather than only on what it chose to return.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

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

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _requests.Add(request);

        foreach (var (fragment, respond) in _routes)
        {
            if (request.RequestUri!.PathAndQuery.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(respond());
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}""", System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
