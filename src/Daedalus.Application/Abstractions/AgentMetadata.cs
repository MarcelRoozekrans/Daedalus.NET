namespace Daedalus.Application.Abstractions;

/// <summary>
///     Represents metadata for an Awesome Copilot agent or skill.
/// </summary>
public record AgentMetadata
{
    /// <summary>The unique identifier of the agent/skill.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Description of what the agent/skill does.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Tags/categories (e.g., "devops", "security", "database").</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>The organization/owner of this agent.</summary>
    public string Organization { get; set; } = string.Empty;

    /// <summary>Repository or source URL.</summary>
    public Uri? SourceUrl { get; set; }

    /// <summary>Relevance score (0-100) indicating fit for the task.</summary>
    public int RelevanceScore { get; set; }
}
