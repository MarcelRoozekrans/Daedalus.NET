using Daedalus.Infrastructure.Services.GitHub;
using Thalos.Git;
using Thalos.Git.Workspaces;

namespace Daedalus.Agents.Git;

/// <summary>
///     Hands the workspace git the GitHub token for a <c>https://github.com/</c> remote, so a run can clone, fetch and
///     push a private repository. Thalos passes the credentials to git through <c>GIT_CONFIG_COUNT</c> environment
///     variables, never through git config, so the token is never written into the mirror or the worktree.
/// </summary>
/// <remarks>
///     Any other remote, or no configured token, gets <see langword="null"/>: anonymous git. The prefix includes the
///     trailing slash so a lookalike host such as <c>https://github.com.example/</c> never receives the token.
/// </remarks>
internal sealed class GitHubGitCredentialSource(IGitHubTokenSource tokens) : IGitCredentialSource
{
    private const string GitHubPrefix = "https://github.com/";

    private const string TokenUserName = "x-access-token";

    /// <inheritdoc />
    public GitCredentials? GetCredentials(string remoteUrl) =>
        remoteUrl.StartsWith(GitHubPrefix, StringComparison.OrdinalIgnoreCase) && tokens.GetToken() is { IsSuccess: true } token
            ? new GitCredentials(TokenUserName, token.Value)
            : null;
}
