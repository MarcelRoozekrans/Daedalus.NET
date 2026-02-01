using CSharpFunctionalExtensions;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.Services.CodeAnalysis;

/// <summary>
///     Extracts code and context from git repositories
/// </summary>
#pragma warning disable CA1054 // Uri parameters should not be strings — URLs passed as strings to git operations
public interface IRepositoryCodeExtractor
{
    // Get single file
    Task<Result<RepositoryFile>> GetFileAsync(
        string repoUrl,
        string filePath,
        string? branch = null,
        string? commitSha = null,
        CancellationToken ct = default);

    // Get code snippet from line range
    Task<Result<string>> GetCodeSnippetAsync(
        string workTreePath,
        string filePath,
        int? startLine = null,
        int? endLine = null,
        CancellationToken ct = default);

    // Find related files
    Task<Result<IReadOnlyList<string>>> FindRelatedFilesAsync(
        string workTreePath,
        string filePath,
        CancellationToken ct = default);

    // Get git blame/history
    Task<Result<IReadOnlyList<GitCommitInfo>>> GetFileHistoryAsync(
        string workTreePath,
        string filePath,
        int? maxCommits = null,
        CancellationToken ct = default);

    // Build analysis context
    Task<Result<AnalysisContext>> BuildAnalysisContextAsync(
        CodeAnalysisRequest request,
        string workTreePath,
        CancellationToken ct = default);
}
