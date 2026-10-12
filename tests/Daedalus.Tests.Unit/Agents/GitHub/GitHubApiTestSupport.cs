using System.Text.Json.Nodes;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Daedalus.Tests.Unit.Agents.GitHub;

/// <summary>
///     Shared construction for <see cref="GitHubApi"/> tests. Both the read and write test classes build the same
///     way — over a <see cref="StubHandler"/>, with a fake clock and a token — so this lives in one place rather
///     than as two copies that can drift apart.
/// </summary>
internal static class GitHubApiTestSupport
{
    public static readonly DateTime Now = new(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc);

    public static GitHubApi Build(StubHandler handler, string? token = "ghp_example") =>
        new(
            Clients(handler),
            Options.Create(new GitHubOptions()),
            new GitHubTokenSource(_ => token),
            new FakeTimeProvider(new DateTimeOffset(Now)),
            NullLogger<GitHubApi>.Instance);

    /// <summary>Both named clients over one handler, so a test reads every request whichever client sent it.</summary>
    public static IHttpClientFactory Clients(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    /// <summary>A request's query as name to unescaped value, so a test compares parameters rather than their order.</summary>
    public static IReadOnlyDictionary<string, string> QueryOf(HttpRequestMessage request) =>
        request.RequestUri!.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "",
                StringComparer.Ordinal);

    /// <summary>The query as <c>name=value</c> pairs sorted by name and unescaped, or empty when there is none.</summary>
    public static string CanonicalQuery(HttpRequestMessage request) =>
        string.Join('&', QueryOf(request).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

    /// <summary>Compares JSON by value, so a serializer's property order and escaping do not matter.</summary>
    public static void ShouldBeJson(string? actual, string expected) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual ?? "null"), JsonNode.Parse(expected))
            .Should().BeTrue($"the body sent was {actual ?? "<none>"}");
}
