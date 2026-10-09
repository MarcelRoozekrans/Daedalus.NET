namespace Daedalus.Agents;

/// <summary>
///     Makes model-written text safe to post as the GitHub account Daedalus writes with. The <c>file-review-findings</c>
///     host action trusts a final-line HTML comment marker in a body that account authored, so nothing a model writes
///     through that account may open an HTML comment: neither the bodies the host itself builds nor the bodies and titles
///     the <c>repoaction</c> tools post.
/// </summary>
public static class HtmlCommentText
{
    private const string Opener = "<!--";
    private const string Escaped = "&lt;!--";

    /// <summary><paramref name="text"/> with every HTML comment opener escaped, so it renders as written and hides nothing.</summary>
    public static string Neutralize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace(Opener, Escaped, StringComparison.Ordinal);
    }
}
