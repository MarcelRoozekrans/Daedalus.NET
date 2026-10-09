using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>
///     A validated <c>owner/name</c> repository reference. Agents pass this string straight from a model, so it is
///     parsed rather than interpolated: a value containing a slash, a query or a traversal segment would otherwise
///     build a URL pointing somewhere other than the caller intended.
/// </summary>
public sealed class RepoRef
{
    private static readonly char[] Forbidden = ['?', '#', '\\', ' '];

    private RepoRef(string owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    public string Owner { get; }
    public string Name { get; }

    public static Result<RepoRef> Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result<RepoRef>.Failure("Repository must be given as owner/name.");

        var parts = value.Split('/');
        if (parts.Length != 2)
            return Result<RepoRef>.Failure($"Repository must be given as owner/name, not '{value}'.");

        var owner = parts[0];
        var name = parts[1];

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            return Result<RepoRef>.Failure($"Repository must be given as owner/name, not '{value}'.");

        if (owner.IndexOfAny(Forbidden) >= 0 || name.IndexOfAny(Forbidden) >= 0
            || owner.Contains("..", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
        {
            return Result<RepoRef>.Failure($"Repository name contains characters that are not allowed: '{value}'.");
        }

        return Result<RepoRef>.Success(new RepoRef(owner, name));
    }

    /// <summary>
    ///     The owner and name out of a github.com URL: an HTTPS remote with or without <c>.git</c>, an SSH remote
    ///     (<c>git@github.com:owner/name.git</c>), or a web link into the repository such as a pull request's
    ///     <c>https://github.com/owner/name/pull/7</c>. Anything else is a failure, never a guess.
    /// </summary>
    [SuppressMessage("Design", "CA1054", Justification = "Takes a git remote, including the SSH form, which is not a System.Uri.")]
    public static Result<RepoRef> FromGitHubUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Result<RepoRef>.Failure("A GitHub URL is required.");

        const string SshPrefix = "git@github.com:";
        string path;
        if (url.StartsWith(SshPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = url[SshPrefix.Length..];
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                 && (string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                     || string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
                 && string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            path = parsed.AbsolutePath;
        }
        else
        {
            return Result<RepoRef>.Failure($"'{url}' is not a github.com repository URL.");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return Result<RepoRef>.Failure($"'{url}' does not name an owner and a repository.");

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return Parse($"{segments[0]}/{name}");
    }

    public override string ToString() => $"{Owner}/{Name}";
}
