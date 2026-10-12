using System.Net;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>
///     The process-wide store behind <see cref="GitHubConditionalCacheHandler"/>: at most <see cref="Capacity"/> answers
///     that carried an ETag, the least recently used evicted first. In memory only, so a restart costs one full request per
///     URL (spec D4).
/// </summary>
internal sealed class GitHubConditionalCache
{
    /// <summary>The most answers kept.</summary>
    public const int Capacity = 200;

    /// <summary>The largest body kept, 256 KiB. A larger answer passes through unstored, so the cache stays bounded in bytes.</summary>
    public const int MaxEntryBytes = 256 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The entry for <paramref name="key"/>, now the most recently used, or <see langword="null"/>.</summary>
    public Entry? Find(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
                return null;

            _recency.Remove(node);
            _recency.AddFirst(node);
            return node.Value;
        }
    }

    /// <summary>Stores <paramref name="entry"/> as the most recently used, replacing one with its key, and evicts past the cap.</summary>
    public void Store(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (_entries.Remove(entry.Key, out var replaced))
                _recency.Remove(replaced);

            _entries[entry.Key] = _recency.AddFirst(entry);

            while (_entries.Count > Capacity)
            {
                var oldest = _recency.Last!;
                _recency.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
        }
    }

    /// <summary>One stored answer: its validator, its body and its content type.</summary>
    internal sealed record Entry(string Key, string ETag, byte[] Body, string? ContentType);
}

/// <summary>
///     Answers a repeated GitHub read from <see cref="GitHubConditionalCache"/> when GitHub says nothing changed. A GET whose
///     URL has a stored answer is sent with <c>If-None-Match</c>; a <c>304</c> comes back as a <c>200</c> with the stored body;
///     a <c>200</c> with an <c>ETag</c> is stored. Everything else passes through and is not stored: errors, a <c>200</c>
///     without an <c>ETag</c>, and every write. A <c>304</c> to an authenticated conditional request costs no primary rate
///     limit, and GitHub sends one only when the resource is unchanged.
/// </summary>
/// <remarks>
///     The key is the method and the full URL, with no identity: the process holds a single GitHub token, which
///     <see cref="GitHubTokenSource"/> reads from one environment variable, so every caller may see every stored answer.
///     A second token would need the identity in the key. The entry found before sending is the one replayed, so an
///     eviction while the request is in flight cannot let a <c>304</c> through.
/// </remarks>
internal sealed class GitHubConditionalCacheHandler(GitHubConditionalCache cache) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var key = "GET " + request.RequestUri.AbsoluteUri;
        var stored = cache.Find(key);
        if (stored is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", stored.ETag);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && stored is not null)
            return Replay(response, stored);

        if (response.StatusCode == HttpStatusCode.OK
            && response.Headers.ETag is { } etag
            && response.Content.Headers.ContentLength is null or <= GitHubConditionalCache.MaxEntryBytes)
        {
            // ReadAsByteArrayAsync buffers the content, so the client still reads the whole body after this. A body without
            // a declared length is checked once read. Buffering a large body that is then not stored is accepted: callers
            // deserialize the whole body anyway.
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length <= GitHubConditionalCache.MaxEntryBytes)
                cache.Store(new GitHubConditionalCache.Entry(key, etag.ToString(), body, response.Content.Headers.ContentType?.ToString()));
        }

        return response;
    }

    private static HttpResponseMessage Replay(HttpResponseMessage notModified, GitHubConditionalCache.Entry stored)
    {
        var replay = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = notModified.RequestMessage,
            Version = notModified.Version,
            Content = new ByteArrayContent(stored.Body),
        };

        foreach (var header in notModified.Headers)
            replay.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (stored.ContentType is not null)
            replay.Content.Headers.TryAddWithoutValidation("Content-Type", stored.ContentType);

        notModified.Dispose();
        return replay;
    }
}
