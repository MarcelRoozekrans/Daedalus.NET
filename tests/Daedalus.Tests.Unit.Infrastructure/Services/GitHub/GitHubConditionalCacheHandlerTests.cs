using System.Net;
using System.Text;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>Phase 2.11, decision D4 and ruling P17: the in-memory ETag cache in front of GitHub reads.</summary>
public sealed class GitHubConditionalCacheHandlerTests
{
    private const string Url = "https://api.github.com/repos/owner/repo/issues/7";
    private const string IssueJson = """{"number":7,"title":"A bug","state":"open","body":"text","html_url":"https://github.com/owner/repo/issues/7","labels":[]}""";

    /// <summary>
    ///     Red: send <c>If-None-Match</c> with an empty tag when nothing is stored; the first-request assertion fails.
    ///     Red: skip adding <c>If-None-Match</c>; the second-request assertion throws on the missing header.
    ///     Red: return the 304 as it came; the status assertion fails.
    ///     Red: replay an empty body; the body assertion fails.
    ///     Red: drop the stored content type from the replay; the media-type assertion throws on a null content type.
    /// </summary>
    [Fact]
    public async Task A_second_read_sends_the_etag_and_a_304_replays_the_stored_answer()
    {
        var inner = RecordingHandler.Answering(request => request.Headers.Contains("If-None-Match") ? NotModified() : Ok(IssueJson, "\"abc\""));
        using var invoker = Invoker(new GitHubConditionalCache(), inner);

        using var first = await invoker.SendAsync(Get(Url), CancellationToken.None);
        using var second = await invoker.SendAsync(Get(Url), CancellationToken.None);

        inner.Sent[0].Headers.Should().NotContainKey("If-None-Match");
        inner.Sent[1].Headers["If-None-Match"].Should().Be("\"abc\"");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync()).Should().Be(IssueJson);
        second.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    /// <summary>Red: read the first body as a stream into the cache without buffering it; the client then reads it empty.</summary>
    [Fact]
    public async Task The_answer_that_is_stored_still_reaches_the_client_whole()
    {
        var inner = RecordingHandler.Answering(_ => Ok(IssueJson, "\"abc\""));
        using var invoker = Invoker(new GitHubConditionalCache(), inner);

        using var first = await invoker.SendAsync(Get(Url), CancellationToken.None);

        (await first.Content.ReadAsStringAsync()).Should().Be(IssueJson);
    }

    /// <summary>
    ///     Red: store a 200 without an ETag under an empty tag; the second request then carries <c>If-None-Match</c>.
    ///     Red: the same change, with the condition for sending <c>If-None-Match</c> narrowed to a non-empty tag; the header
    ///     assertion stays green and the count is 1.
    /// </summary>
    [Fact]
    public async Task A_200_without_an_etag_is_not_stored()
    {
        var cache = new GitHubConditionalCache();
        var inner = RecordingHandler.Answering(_ => Ok(IssueJson, etag: null));
        using var invoker = Invoker(cache, inner);

        using var first = await invoker.SendAsync(Get(Url), CancellationToken.None);
        using var second = await invoker.SendAsync(Get(Url), CancellationToken.None);

        inner.Sent[1].Headers.Should().NotContainKey("If-None-Match");
        cache.Count.Should().Be(0);
    }

    /// <summary>Red: store any answer that carries an ETag; the count is 1.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_error_is_never_stored(HttpStatusCode status)
    {
        var cache = new GitHubConditionalCache();
        var inner = RecordingHandler.Answering(_ => Refused(status));
        using var invoker = Invoker(cache, inner);

        using var answer = await invoker.SendAsync(Get(Url), CancellationToken.None);

        cache.Count.Should().Be(0);
    }

    /// <summary>
    ///     An error between two good reads neither replaces the stored answer nor is replayed in its place.
    ///     Red: store any answer that carries an ETag; the third request carries <c>"err"</c>, not <c>"abc"</c>.
    ///     Red: replace a failed answer's status with 200 on its way out; the status assertion fails.
    ///     Red: overwrite the stored entry when an error comes back; the third request carries the wrong tag.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task An_error_answer_neither_replaces_nor_is_served_in_place_of_the_stored_one(HttpStatusCode status)
    {
        var calls = 0;
        var inner = RecordingHandler.Answering(request => Interlocked.Increment(ref calls) switch
        {
            1 => Ok(IssueJson, "\"abc\""),
            2 => Refused(status, "\"err\""),
            _ => request.Headers.Contains("If-None-Match") ? NotModified() : Ok("{}", "\"other\""),
        });
        using var invoker = Invoker(new GitHubConditionalCache(), inner);

        using var first = await invoker.SendAsync(Get(Url), CancellationToken.None);
        using var error = await invoker.SendAsync(Get(Url), CancellationToken.None);
        using var third = await invoker.SendAsync(Get(Url), CancellationToken.None);

        error.StatusCode.Should().Be(status);
        inner.Sent[2].Headers["If-None-Match"].Should().Be("\"abc\"");
        (await third.Content.ReadAsStringAsync()).Should().Be(IssueJson);
    }

    /// <summary>
    ///     Red: make every method conditional; the POST then carries <c>If-None-Match</c>.
    ///     Red: drop the method check and key on <c>request.Method + " " + uri</c>; the POST is stored too, and the count
    ///     is 2.
    /// </summary>
    [Fact]
    public async Task A_write_is_never_conditional_and_never_stored()
    {
        var cache = new GitHubConditionalCache();
        var inner = RecordingHandler.Answering(_ => Ok(IssueJson, "\"abc\""));
        using var invoker = Invoker(cache, inner);

        using var read = await invoker.SendAsync(Get(Url), CancellationToken.None);
        using var write = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent("{}") }, CancellationToken.None);

        inner.Sent[1].Headers.Should().NotContainKey("If-None-Match");
        cache.Count.Should().Be(1);
    }

    /// <summary>
    ///     A scan page can be megabytes, and digest URLs change every day, so a large answer is not kept (P17).
    ///     Red: drop the size check from the handler; the count is 1.
    ///     Red: answer an over-cap response with an empty body instead of passing it through; the length assertion fails.
    /// </summary>
    [Fact]
    public async Task An_answer_over_the_size_cap_is_not_stored()
    {
        var cache = new GitHubConditionalCache();
        var large = "\"" + new string('x', GitHubConditionalCache.MaxEntryBytes) + "\"";
        var inner = RecordingHandler.Answering(_ => Ok(large, "\"big\""));
        using var invoker = Invoker(cache, inner);

        using var answer = await invoker.SendAsync(Get(Url), CancellationToken.None);

        cache.Count.Should().Be(0);
        (await answer.Content.ReadAsStringAsync()).Length.Should().Be(large.Length);
    }

    /// <summary>
    ///     GitHub sends compressed, chunked JSON, so the length is unknown until the body is read; only the check on the
    ///     bytes read keeps a large scan page out of the cache.
    ///     Red: drop the check on the bytes read from the handler; the count is 1.
    ///     Red: answer an over-cap response with an empty body instead of passing it through; the length assertion fails.
    /// </summary>
    [Fact]
    public async Task An_answer_over_the_size_cap_with_no_declared_length_is_not_stored()
    {
        var cache = new GitHubConditionalCache();
        var large = "\"" + new string('x', GitHubConditionalCache.MaxEntryBytes) + "\"";
        var inner = RecordingHandler.Answering(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(large) };
            response.Headers.TryAddWithoutValidation("ETag", "\"big\"");
            return response;
        });
        using var invoker = Invoker(cache, inner);

        using var answer = await invoker.SendAsync(Get(Url), CancellationToken.None);

        cache.Count.Should().Be(0);
        (await answer.Content.ReadAsStringAsync()).Length.Should().Be(large.Length);
    }

    /// <summary>
    ///     Red: drop the eviction loop from <c>Store</c>; the count is 201.
    ///     Red: evict the most recent entry instead of the oldest; key 1 survives.
    ///     Red: leave <c>Find</c> from moving an entry to the front; key 0 is evicted.
    /// </summary>
    [Fact]
    public void The_least_recently_used_entry_is_evicted_at_the_cap()
    {
        var cache = new GitHubConditionalCache();
        for (var i = 0; i < GitHubConditionalCache.Capacity; i++)
            cache.Store(Entry(i));

        cache.Find(Key(0)).Should().NotBeNull();
        cache.Store(Entry(GitHubConditionalCache.Capacity));

        cache.Count.Should().Be(GitHubConditionalCache.Capacity);
        cache.Find(Key(1)).Should().BeNull();
        cache.Find(Key(0)).Should().NotBeNull();
    }

    /// <summary>
    ///     Through the real registration and two facades, as two scopes get them.
    ///     Red: replay an empty <c>ByteArrayContent</c>; the second read is Unreadable, and the equivalence assertion throws
    ///     on <c>Value</c>.
    ///     Red: leave the cache handler off the read client in <c>AddGitHubApi</c>; no <c>If-None-Match</c> is sent, and the
    ///     header assertion throws on the missing header.
    /// </summary>
    [Fact]
    public async Task A_repeated_issue_read_is_answered_from_a_304_replay()
    {
        var primary = RecordingHandler.Answering(request => request.Headers.Contains("If-None-Match") ? NotModified() : Ok(IssueJson, "W/\"v1\""));
        await using var host = GitHubHost.Build(primary);
        var repo = RepoRef.Parse("owner/repo").Value;

        var first = await host.GetRequiredService<GitHubApi>().GetIssueAsync(repo, 7);
        var second = await host.GetRequiredService<GitHubApi>().GetIssueAsync(repo, 7);

        second.Value.Should().BeEquivalentTo(first.Value);
        primary.Sent[1].Headers["If-None-Match"].Should().Be("W/\"v1\"");
    }

    /// <summary>Content whose length is not known up front, as a decompressed chunked body is.</summary>
    private sealed class UnsizedContent(string text) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static HttpMessageInvoker Invoker(GitHubConditionalCache cache, HttpMessageHandler inner) =>
        new(new GitHubConditionalCacheHandler(cache) { InnerHandler = inner });

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    private static HttpResponseMessage Ok(string json, string? etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null)
            response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    private static HttpResponseMessage Refused(HttpStatusCode status, string etag = "\"abc\"")
    {
        var refused = new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        refused.Headers.TryAddWithoutValidation("ETag", etag);
        return refused;
    }

    private static HttpResponseMessage NotModified() => new(HttpStatusCode.NotModified);

    private static string Key(int i) => $"GET https://api.github.com/repos/owner/repo/issues/{i}";

    private static GitHubConditionalCache.Entry Entry(int i) => new(Key(i), $"\"{i}\"", [], "application/json");
}
