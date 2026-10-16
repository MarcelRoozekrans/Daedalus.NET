using System.Net;
using System.Text;
using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

/// <summary>One request the <see cref="RecordingHandler"/> saw, with its body read at send time.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string? Body);

/// <summary>
///     An <see cref="HttpMessageHandler"/> that answers each request by its path and query, and records every
///     request with its body buffered before the call returns. An unrouted request is a 404.
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<Task<HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>The path and query of every recorded request, in order.</summary>
    public IReadOnlyList<string> Paths => [.. Requests.Select(r => r.PathAndQuery)];

    /// <summary>Routes <paramref name="pathAndQuery"/> to <paramref name="respond"/>, replacing any earlier route.</summary>
    public RecordingHandler On(string pathAndQuery, Func<HttpResponseMessage> respond) =>
        On(pathAndQuery, () => Task.FromResult(respond()));

    /// <summary>
    ///     Routes <paramref name="pathAndQuery"/> to <paramref name="respond"/>, which may do something, such as
    ///     dispatch an action, while the request is in flight; replaces any earlier route.
    /// </summary>
    public RecordingHandler On(string pathAndQuery, Func<Task<HttpResponseMessage>> respond)
    {
        _routes[pathAndQuery] = respond;
        return this;
    }

    /// <summary>Routes <paramref name="pathAndQuery"/> to a JSON response.</summary>
    public RecordingHandler OnJson(string pathAndQuery, string json, HttpStatusCode code = HttpStatusCode.OK) =>
        On(pathAndQuery, () => Json(code, json));

    public ApiClient Client() => new(Http());

    /// <summary>The project client, whose writes go through this handler as its reads do.</summary>
    public ProjectApiClient ProjectClient() => new(Http());

    /// <summary>The Git Repositories client, whose reads and writes go through this handler.</summary>
    public RepositoriesApiClient RepositoriesClient() => new(Http());

    private HttpClient Http() => new(this, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };

    public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.PathAndQuery;
        Requests.Add(new RecordedRequest(request.Method, path, body));
        return _routes.TryGetValue(path, out var respond) ? await respond() : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
