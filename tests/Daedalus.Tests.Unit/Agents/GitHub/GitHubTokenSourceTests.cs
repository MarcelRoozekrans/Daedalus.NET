using Daedalus.Agents.GitHub;

namespace Daedalus.Tests.Unit.Agents.GitHub;

public class GitHubTokenSourceTests
{
    [Fact]
    public void GetToken_fails_when_the_environment_variable_is_absent()
    {
        var source = new GitHubTokenSource(_ => null);

        var result = source.GetToken();

        result.IsFailure.Should().BeTrue(
            "falling back to unauthenticated requests turns a credential problem into a 404 on every private repo");
    }

    [Fact]
    public void GetToken_fails_when_the_environment_variable_is_blank()
    {
        new GitHubTokenSource(_ => "   ").GetToken().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void GetToken_returns_the_token_when_present()
    {
        new GitHubTokenSource(_ => "ghp_example").GetToken().Value.Should().Be("ghp_example");
    }

    [Fact]
    public void The_failure_message_does_not_contain_the_variable_value()
    {
        var result = new GitHubTokenSource(_ => null).GetToken();

        result.Error.Should().NotContain("ghp_", "a token must never reach a message, a log or a model");
    }
}
