namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object for analysis context
/// </summary>
#pragma warning disable CA1056 // URI properties should not be strings — used as data carrier, not parsed as Uri
public record AnalysisContext
{
    public string RepositoryName { get; init; } = string.Empty;
    public string RepositoryUrl { get; init; } = string.Empty;
    public string TargetFilePath { get; init; } = string.Empty;
    public string CodeSnippet { get; init; } = string.Empty;
    public IReadOnlyList<string> RelatedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RelatedFileContents { get; init; } = Array.Empty<string>();
    public IReadOnlyList<GitCommitInfo> RecentHistory { get; init; } = Array.Empty<GitCommitInfo>();
    public string GeneratedPrompt { get; init; } = string.Empty;
}
