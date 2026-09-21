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

    public static GitHubApi Build(StubHandler handler, string? token = "ghp_example")
    {
        var http = new HttpClient(handler);
        return new GitHubApi(
            http,
            Options.Create(new GitHubOptions()),
            new GitHubTokenSource(_ => token),
            new FakeTimeProvider(Now),
            NullLogger<GitHubApi>.Instance);
    }
}
