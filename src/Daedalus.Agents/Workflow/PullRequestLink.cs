using System.Globalization;
using Daedalus.Infrastructure.Services.GitHub;
using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>Reads a run's <c>pr_url</c> variable. <c>OpenPullRequestAction</c> is its only writer.</summary>
public static class PullRequestLink
{
    /// <summary>
    ///     The link when the value is an absolute http or https URL, or else the text it holds. Both are null when the run
    ///     has no <c>pr_url</c> yet.
    /// </summary>
    public static (Uri? Link, string? Unreadable) Read(WorkflowRun run)
    {
        if (!run.Variables.TryGetValue(ReviewHandoff.PrUrlKey, out var value) || value?.ToString() is not { } text)
        {
            return (null, null);
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var link)
            && (string.Equals(link.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || string.Equals(link.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
            ? (link, null)
            : (null, text);
    }

    /// <summary>
    ///     The repository and number of a github.com pull request link, and nothing else. <c>file-review-findings</c>
    ///     files against that repository, and the run view links a deferred finding's existing issue into it.
    /// </summary>
    public static bool TryParse(Uri? link, out RepoRef repo, out int number)
    {
        repo = null!;
        number = 0;
        if (link is not { IsAbsoluteUri: true })
            return false;

        var owner = RepoRef.FromGitHubUrl(link.AbsoluteUri);
        if (owner.IsFailure)
            return false;

        var segments = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4 || !string.Equals(segments[2], "pull", StringComparison.Ordinal)
            || !int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0)
            return false;

        repo = owner.Value;
        return true;
    }
}
