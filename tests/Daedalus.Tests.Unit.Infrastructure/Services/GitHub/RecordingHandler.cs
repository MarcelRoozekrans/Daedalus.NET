using System.Collections.Concurrent;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>One request as it was sent: its headers and body are copied before the client disposes it.</summary>
internal sealed record SentRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>
///     The innermost handler of a test pipeline. It records every attempt, thread-safely, and answers with
///     <paramref name="respond"/>, which may wait on the attempt's token to stand in for a slow GitHub.
/// </summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly ConcurrentQueue<SentRequest> _sent = new();

    public IReadOnlyList<SentRequest> Sent => [.. _sent];

    public int Count => _sent.Count;

    public static RecordingHandler Answering(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new((request, _) => Task.FromResult(respond(request)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
        _sent.Enqueue(new SentRequest(request.Method, request.RequestUri!, headers, body));
        return await respond(request, cancellationToken).ConfigureAwait(false);
    }
}
