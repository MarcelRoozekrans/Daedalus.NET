using System.Globalization;
using System.Text;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Writes a manufacture run's pull-request title, body and code commit message from <see cref="PullRequestFacts"/>.
///     Host code writes every heading and every sentence; the text a user or a model supplied appears only as data.
/// </summary>
/// <remarks>
///     <para>
///     <b>Nothing supplied becomes structure.</b> The work intent is rendered inside a quoted block, every line prefixed
///     with <c>&gt;</c>, so a line in it that reads <c>## Approval</c> is quoted text, not a heading. Lens names, checked
///     items, file paths and the approver's name are each collapsed onto one line, so none of them can start a line of
///     its own. The agent's summary comes last, under its own heading, and its first line is the label that says host
///     code did not verify it. The review section likewise opens with a label: its checked items are the review agents'
///     own report, and host code verified only each lens's approving verdict.
///     </para>
///     <para>
///     <b>Nothing an agent wrote renders.</b> A quoted block still renders links, images and <c>@</c> mentions, so the
///     agents' text is never quoted: each checked item is a code span and the summary is a fenced code block, each
///     fenced with more backticks than the longest run in the text, so the text can neither close its fence nor render
///     as anything but itself. A link the implement or a review agent wrote therefore reaches a reader as text, an image
///     is never fetched, and nobody is mentioned.
///     </para>
///     <para>
///     <b>Bounded.</b> The title's line is cut to <see cref="MaxTitleLineLength"/>. The summary, the intent, each list
///     entry and the number of files listed are capped, and the whole body never exceeds <see cref="MaxBodyLength"/>,
///     under GitHub's 65,536-character limit on a pull-request body, so an oversized report cannot fail the open.
///     </para>
/// </remarks>
internal static class PullRequestBody
{
    /// <summary>The longest first line of the work intent the title and the code commit message carry.</summary>
    public const int MaxTitleLineLength = 72;

    /// <summary>The longest work intent the body quotes. <c>ManufactureRunStarter</c> already refuses a longer one.</summary>
    public const int MaxWorkIntentLength = 4000;

    /// <summary>The longest agent summary the body quotes.</summary>
    public const int MaxSummaryLength = 4000;

    /// <summary>The longest single list entry: a lens name, a checked item, a file path or the approver.</summary>
    public const int MaxListEntryLength = 300;

    /// <summary>How many changed files the body lists before it says how many more there are.</summary>
    public const int MaxChangedFilesListed = 100;

    /// <summary>The longest body this renders, cut marker included.</summary>
    public const int MaxBodyLength = 60_000;

    /// <summary>The first line of the review section: the checked items are the review agents' own words.</summary>
    public const string ReviewLabel = "> Reported by the review agents; host code verified only that each lens approved.";

    /// <summary>The first line of the agent-written section.</summary>
    public const string AgentTextLabel = "> Written by the implement agent; not verified by host code.";

    private const string Ellipsis = "…";
    private const string CutNotice = "\n\n_This description was cut to fit the pull request's size limit._\n";

    /// <summary>The pull-request title: <c>manufacture: </c> and the intent's first line, cut to <see cref="MaxTitleLineLength"/>.</summary>
    public static string Title(string workIntent) => "manufacture: " + TitleLine(workIntent);

    /// <summary>The code commit's message: <c>feat: </c> and the intent's first line, a blank line, and the run.</summary>
    public static string CodeCommitMessage(string workIntent, Guid runId) =>
        $"feat: {TitleLine(workIntent)}\n\nManufactured by run {runId}.";

    /// <summary>Renders the pull-request body. See the type's remarks.</summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="facts"/> carries no review evidence. <c>OpenPullRequestAction</c> refuses such a run before it
    ///     commits anything, and the review section's label claims a verification only evidence can back.
    /// </exception>
    public static string Render(PullRequestFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Checked.Count == 0)
            throw new ArgumentException("A pull request body is rendered only for a run with review evidence.", nameof(facts));

        var body = new StringBuilder();
        body.Append("## Work intent\n\n");
        AppendQuoted(body, Cut(facts.WorkIntent, MaxWorkIntentLength));

        body.Append("\n## Changed files\n\n");
        foreach (var change in facts.Changes.Take(MaxChangedFilesListed))
        {
            var path = Cut(OneLine(change.Path), MaxListEntryLength).Replace('`', '\'');
            body.Append(CultureInfo.InvariantCulture, $"- `{path}`  +{change.LinesAdded} -{change.LinesDeleted}\n");
        }

        if (facts.Changes.Count > MaxChangedFilesListed)
            body.Append(CultureInfo.InvariantCulture, $"- and {facts.Changes.Count - MaxChangedFilesListed} more files\n");

        body.Append("\n## Review\n\n");
        // The label claims a verification, which the action checked for every lens listed below it.
        body.Append(ReviewLabel).Append("\n\n");

        foreach (var (lens, items) in facts.Checked)
        {
            body.Append("- ").Append(Entry(lens)).Append('\n');
            foreach (var item in items)
                body.Append("  - ").Append(CodeSpan(Entry(item))).Append('\n');
        }

        body.Append("\n## Approval\n\n");
        body.Append(facts is { ApprovedBy: { } by, ApprovedAt: { } at }
            ? string.Create(CultureInfo.InvariantCulture, $"Approved at the gate by {Entry(by)} at {at.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC.\n")
            : "No gate approval is recorded for this run.\n");

        body.Append("\n## Run\n\n");
        body.Append(CultureInfo.InvariantCulture, $"- Run id: `{facts.RunId}`\n");
        body.Append(CultureInfo.InvariantCulture, $"- Process: {Entry(facts.Process)} v{facts.ProcessVersion}\n");

        body.Append("\n## Agent-written summary\n\n");
        body.Append(AgentTextLabel).Append("\n\n");
        if (string.IsNullOrWhiteSpace(facts.AgentSummary))
            body.Append("No summary was reported.\n");
        else
            AppendFenced(body, Cut(facts.AgentSummary, MaxSummaryLength));

        return body.Length <= MaxBodyLength
            ? body.ToString()
            : Cut(body.ToString(), MaxBodyLength - CutNotice.Length - Ellipsis.Length) + CutNotice;
    }

    /// <summary>
    ///     The work intent's first non-blank line, on one line, with nested parentheses flattened, cut to
    ///     <see cref="MaxTitleLineLength"/>.
    /// </summary>
    private static string TitleLine(string workIntent)
    {
        var first = workIntent
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Length > 0) ?? "";
        var line = FlattenNestedParentheses(OneLine(first)).Trim();
        return line.Length <= MaxTitleLineLength ? line : CutAt(line, MaxTitleLineLength).TrimEnd();
    }

    /// <summary>
    ///     Keeps a top-level parenthesis and turns every parenthesis nested inside another into a square bracket.
    ///     release-please's commit parser drops a commit whose message nests parentheses, so neither the title nor the
    ///     commit message may carry them.
    /// </summary>
    private static string FlattenNestedParentheses(string text)
    {
        var result = new StringBuilder(text.Length);
        var depth = 0;
        foreach (var c in text)
        {
            switch (c)
            {
                case '(':
                    result.Append(depth == 0 ? '(' : '[');
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    result.Append(depth == 0 ? ')' : ']');
                    break;
                default:
                    result.Append(c);
                    break;
            }
        }

        return result.ToString();
    }

    private static string Entry(string text) => Cut(OneLine(text), MaxListEntryLength);

    /// <summary>Replaces every control character, line breaks included, with a space.</summary>
    private static string OneLine(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
        });

    private static void AppendQuoted(StringBuilder body, string text)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.TrimEnd();
            body.Append(trimmed.Length == 0 ? ">" : "> " + trimmed).Append('\n');
        }
    }

    /// <summary>
    ///     <paramref name="text"/>, which is on one line, as a code span fenced with one more backtick than its longest
    ///     run, padded with a space where CommonMark would otherwise read a backtick or a space at its edge as part of
    ///     the fence.
    /// </summary>
    private static string CodeSpan(string text)
    {
        var fence = new string('`', LongestBacktickRun(text) + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') || (text.StartsWith(' ') && text.EndsWith(' ')) ? " " : "";
        return fence + pad + text + pad + fence;
    }

    /// <summary>
    ///     <paramref name="text"/> as a fenced code block, fenced with at least three backticks and one more than its
    ///     longest run, so no line of it can close the block.
    /// </summary>
    private static void AppendFenced(StringBuilder body, string text)
    {
        var fence = new string('`', Math.Max(3, LongestBacktickRun(text) + 1));
        body.Append(fence).Append("text\n");
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
            body.Append(line.TrimEnd()).Append('\n');
        body.Append(fence).Append('\n');
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : CutAt(text, max) + Ellipsis;

    /// <summary>The first <paramref name="max"/> characters, one fewer when that would split a surrogate pair.</summary>
    private static string CutAt(string text, int max) =>
        char.IsHighSurrogate(text[max - 1]) ? text[..(max - 1)] : text[..max];
}
