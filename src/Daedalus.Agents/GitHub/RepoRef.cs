using CSharpFunctionalExtensions;

namespace Daedalus.Agents.GitHub;

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
            return Result.Failure<RepoRef>("Repository must be given as owner/name.");

        var parts = value.Split('/');
        if (parts.Length != 2)
            return Result.Failure<RepoRef>($"Repository must be given as owner/name, not '{value}'.");

        var owner = parts[0];
        var name = parts[1];

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            return Result.Failure<RepoRef>($"Repository must be given as owner/name, not '{value}'.");

        if (owner.IndexOfAny(Forbidden) >= 0 || name.IndexOfAny(Forbidden) >= 0
            || owner.Contains("..", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
        {
            return Result.Failure<RepoRef>($"Repository name contains characters that are not allowed: '{value}'.");
        }

        return Result.Success(new RepoRef(owner, name));
    }

    public override string ToString() => $"{Owner}/{Name}";
}
