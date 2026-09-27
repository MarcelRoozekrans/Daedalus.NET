using Daedalus.Agents.Git;
using Daedalus.Infrastructure.Services.GitHub;

namespace Daedalus.Tests.Unit.Agents.Git;

public sealed class GitHubGitCredentialSourceTests
{
    private const string Token = "ghs_test-token";

    private static GitHubGitCredentialSource WithToken(string? token) =>
        new(new GitHubTokenSource(name => string.Equals(name, GitHubTokenSource.VariableName, StringComparison.Ordinal) ? token : null));

    [Theory]
    [InlineData("https://github.com/MarcelRoozekrans/daedalus-sandbox.git")]
    [InlineData("HTTPS://GitHub.com/MarcelRoozekrans/daedalus-sandbox.git")]
    public void A_GitHub_https_remote_gets_the_token_as_an_access_token(string remote)
    {
        var credentials = WithToken(Token).GetCredentials(remote);

        credentials.Should().NotBeNull();
        credentials!.Username.Should().Be("x-access-token");
        credentials.Password.Should().Be(Token);
    }

    [Theory]
    [InlineData("https://github.com.evil.example/MarcelRoozekrans/daedalus-sandbox.git")]
    [InlineData("https://dev.azure.com/org/project/_git/repo")]
    [InlineData("http://github.com/MarcelRoozekrans/daedalus-sandbox.git")]
    public void Any_other_remote_gets_no_credentials(string remote)
    {
        WithToken(Token).GetCredentials(remote).Should().BeNull("the GitHub token must never reach another host");
    }

    [Fact]
    public void Without_a_configured_token_a_GitHub_remote_is_anonymous()
    {
        WithToken(null).GetCredentials("https://github.com/MarcelRoozekrans/daedalus-sandbox.git").Should().BeNull();
    }
}
