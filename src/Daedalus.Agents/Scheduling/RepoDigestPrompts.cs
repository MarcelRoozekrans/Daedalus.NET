namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The agent names and prompt text for the <c>RepoDigest</c> workflow: a scout sweeps the repository for what
///     changed since the last digest, and a writer turns those findings into a short, chat-shaped summary that
///     <c>DeliverDigest</c> hands to the channel adapter.
/// </summary>
public static class RepoDigestPrompts
{
    /// <summary>The catalog name of the agent that gathers findings.</summary>
    public const string ScoutAgent = "scout";

    /// <summary>The catalog name of the agent that turns findings into a summary.</summary>
    public const string WriterAgent = "writer";

    /// <summary>The task handed to <see cref="ScoutAgent"/> for one scheduled run, over one repository and one window.</summary>
    /// <remarks>
    ///     This was a constant reading "this repository", with nothing resolving which one. The tools take an
    ///     explicit <c>owner/name</c>, so the prompt has to name it.
    /// </remarks>
    /// <param name="repo">The repository to sweep, as <c>owner/name</c>.</param>
    /// <param name="sinceUtc">The window's start (UTC) — the previous completed digest's occurrence, or the configured default lookback when there is none.</param>
    public static string ScoutTask(string repo, DateTime sinceUtc) =>
        $"""
         Sweep the repository {repo} for everything that changed since {sinceUtc:yyyy-MM-dd HH:mm} UTC: new commits on
         the default branch, merged and still-open pull requests, issues opened or closed, and any CI runs that failed.
         Use the daedalus__repo_activity tool with repo "{repo}" and since "{sinceUtc:O}" — do not guess at activity you
         have not retrieved.
         For each item, note what changed, who it affects, and whether it needs a human's attention before the next
         digest. Do not write prose yet — list findings as short, factual bullet points a second agent will turn into a
         summary. If a category is empty, say so explicitly rather than omitting it. If the tool reports that a category
         could not be read, say that instead — never report an unreadable category as quiet.
         """;

    /// <summary>The task handed to <see cref="WriterAgent"/>, over the scout's findings.</summary>
    public static string WriterTask(string findings) =>
        $"""
         Turn the findings below into a short digest suitable for a chat message: a few sentences, plain
         language, no headings or markdown tables. Lead with whatever most needs a human's attention; if
         nothing does, say the repository is quiet. Keep it to a paragraph or two — this is a daily nudge,
         not a report.

         Findings:
         {findings}
         """;

    /// <summary>Every agent name this workflow depends on, for the startup validator to check in one pass.</summary>
    public static IReadOnlyList<string> AgentNames { get; } = [ScoutAgent, WriterAgent];
}
