using System.Globalization;
using System.Text;
using Daedalus.Infrastructure.Services.GitHub;

namespace Daedalus.Agents.Workflow;

/// <summary>The text <c>file-review-findings</c> writes to GitHub. Pure functions, so every line is tested without HTTP.</summary>
public static class DeferredFindingText
{
    /// <summary>The hidden marker every write carries, which a retry scans for before writing again (spec A2).</summary>
    public static string Marker(Guid runId, string findingId) =>
        $"<!-- daedalus-run:{runId.ToString("D", CultureInfo.InvariantCulture)} finding:{findingId} -->";

    /// <summary>
    ///     Model-written text made safe to put in a body. An HTML comment opener is escaped, so no generated body can
    ///     contain a <see cref="Marker"/> except the one the host appends last: a reviewer steered by the code it read
    ///     cannot plant a sibling finding's marker (spec A2 and D2).
    /// </summary>
    public static string Block(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("<!--", "&lt;!--", StringComparison.Ordinal);
    }

    /// <summary><see cref="Block"/> for a single line, such as a title: line breaks become spaces.</summary>
    public static string Line(string text) =>
        Block(text).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>A file path as link text inside a code span: nothing in it can close the span or the link, or break the line.</summary>
    public static string PathText(string file) =>
        Line(file).Replace('`', '\'').Replace('[', '(').Replace(']', ')');

    /// <summary>Why a finding was deferred, as a sentence fragment.</summary>
    public static string Reason(string reason) => reason switch
    {
        "different-area" => "it is in code this change does not touch",
        "needs-decision" => "it needs a design decision or the maintainer's call",
        "too-large" => "it is a sizeable piece of work of its own",
        "blocked" => "it waits on something outside this repository",
        _ => Line(reason),
    };

    /// <summary>A link to the finding's line at the pull request's head commit, so it still points at the right code after later edits.</summary>
    public static Uri Permalink(RepoRef repo, string headSha, DeferredFinding finding)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(finding);
        var path = string.Join('/', finding.File.Replace('\\', '/').TrimStart('/').Split('/').Select(Uri.EscapeDataString));
        return new Uri($"https://github.com/{repo.Owner}/{repo.Name}/blob/{headSha}/{path}#L{finding.Line.ToString(CultureInfo.InvariantCulture)}", UriKind.Absolute);
    }

    /// <summary>The body of a new issue for <paramref name="finding"/>; <paramref name="notUsed"/> says why the reviewer's existing issue was not used.</summary>
    public static string IssueBody(RepoRef repo, string headSha, Uri prUrl, Guid runId, IdentifiedDeferredFinding finding, string? notUsed)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(prUrl);
        var text = new StringBuilder()
            .AppendLine(Block(finding.Finding.Scenario))
            .AppendLine()
            .Append("- **Where:** [`").Append(PathText(finding.Finding.File)).Append(':').Append(finding.Finding.Line).Append("`](").Append(Permalink(repo, headSha, finding.Finding).AbsoluteUri).AppendLine(")")
            .Append("- **Why it was not fixed there:** ").AppendLine(Reason(finding.Finding.Reason))
            .Append("- **Found by:** the `").Append(PathText(finding.Lens)).Append("` review lens of manufacture run `").Append(runId).AppendLine("`")
            .Append("- **Pull request:** ").AppendLine(prUrl.AbsoluteUri);
        if (notUsed is not null)
            text.AppendLine().Append("> The reviewer pointed at an existing issue, but it was not used: ").Append(notUsed).AppendLine(".");

        return text.AppendLine().Append(Marker(runId, finding.Id)).ToString();
    }

    /// <summary>The comment added to an open issue the reviewer named as already tracking <paramref name="finding"/>.</summary>
    public static string IssueComment(RepoRef repo, string headSha, Uri prUrl, Guid runId, IdentifiedDeferredFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(prUrl);
        return new StringBuilder()
            .Append("Manufacture run `").Append(runId).Append("` met this again in its `").Append(PathText(finding.Lens))
            .Append("` review lens and left it out of scope: **").Append(Line(finding.Finding.Title)).AppendLine("**.")
            .AppendLine()
            .AppendLine(Block(finding.Finding.Scenario))
            .AppendLine()
            .Append("- **Where:** [`").Append(PathText(finding.Finding.File)).Append(':').Append(finding.Finding.Line).Append("`](").Append(Permalink(repo, headSha, finding.Finding).AbsoluteUri).AppendLine(")")
            .Append("- **Pull request:** ").AppendLine(prUrl.AbsoluteUri)
            .AppendLine()
            .Append(Marker(runId, finding.Id))
            .ToString();
    }

    /// <summary>The one pull-request comment that lists where every deferred finding went.</summary>
    public static string Summary(Guid runId, IReadOnlyList<IdentifiedDeferredFinding> deferred, IReadOnlyDictionary<string, FiledFinding> filed, DroppedFindings dropped)
    {
        ArgumentNullException.ThrowIfNull(deferred);
        ArgumentNullException.ThrowIfNull(filed);
        ArgumentNullException.ThrowIfNull(dropped);
        var text = new StringBuilder().AppendLine("**Review findings this run left out of scope**").AppendLine();
        foreach (var finding in deferred)
        {
            if (dropped.Ids.Contains(finding.Id))
                text.Append("- Dropped at the gate by ").Append(Line(dropped.By ?? "the approver")).Append(": `").Append(Line(finding.Id)).Append("` ").AppendLine(Line(finding.Finding.Title));
            else if (filed.TryGetValue(finding.Id, out var f))
                text.Append(string.Equals(f.Mode, FindingRecords.CommentedMode, StringComparison.Ordinal) ? "- Added to #" : "- Filed #").Append(f.Issue).Append(": ").AppendLine(Line(finding.Finding.Title));
        }

        return text.AppendLine().Append(Marker(runId, FindingRecords.SummaryId)).ToString();
    }
}
