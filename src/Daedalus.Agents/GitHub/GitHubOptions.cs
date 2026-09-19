namespace Daedalus.Agents.GitHub;

/// <summary>Bindable options for the GitHub reader and writer, under <see cref="SectionName"/>.</summary>
public sealed class GitHubOptions
{
    public const string SectionName = "ExternalServices:Platforms:GitHub";

#pragma warning disable CA1056
    public string ApiUrl { get; set; } = "https://api.github.com";
#pragma warning restore CA1056

    /// <summary>GitHub rejects requests without one.</summary>
    public string UserAgent { get; set; } = "Daedalus";

    /// <summary>Per-category ceiling. A category that hits it reports that it was truncated.</summary>
    public int MaxItemsPerCategory { get; set; } = 50;

    /// <summary>Window used only when a run has no previous occurrence to measure from.</summary>
    public TimeSpan DefaultLookback { get; set; } = TimeSpan.FromHours(24);
}
