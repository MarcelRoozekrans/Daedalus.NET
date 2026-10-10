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
}
