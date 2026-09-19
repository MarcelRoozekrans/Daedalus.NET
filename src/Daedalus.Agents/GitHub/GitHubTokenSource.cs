using CSharpFunctionalExtensions;

namespace Daedalus.Agents.GitHub;

public interface IGitHubTokenSource
{
    Result<string> GetToken();
}

/// <summary>
///     Resolves the GitHub token from <c>GITHUB_TOKEN</c>. Takes the lookup as a delegate so a test can drive it
///     without mutating process environment state, which leaks between parallel tests.
/// </summary>
/// <remarks>
///     There is deliberately no unauthenticated fallback. An absent token would otherwise present as a 404 on every
///     private repository, disguising a credential problem as a missing repository.
/// </remarks>
public sealed class GitHubTokenSource(Func<string, string?> readEnvironmentVariable) : IGitHubTokenSource
{
    public const string VariableName = "GITHUB_TOKEN";

    public GitHubTokenSource() : this(Environment.GetEnvironmentVariable) { }

    public Result<string> GetToken()
    {
        var token = readEnvironmentVariable(VariableName);

        // Trimmed here, once, at the source: `gh auth token` and docker env files both emit a trailing newline,
        // and an untrimmed token throws FormatException from the auth header assignment. That is caught on the
        // read path but escapes SendWriteAsync, which has no try — breaking IGitHubWriter's contract that it
        // always returns a Result instead of throwing.
        return string.IsNullOrWhiteSpace(token)
            ? Result.Failure<string>($"No GitHub token is configured. Set the {VariableName} environment variable.")
            : Result.Success(token.Trim());
    }
}
