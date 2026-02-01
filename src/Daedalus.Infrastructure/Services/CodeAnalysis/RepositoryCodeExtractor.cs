using System.Globalization;
using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Extracts code context from repositories for analysis prompts
/// </summary>
public sealed class RepositoryCodeExtractor(ILogger<RepositoryCodeExtractor> logger) : IRepositoryCodeExtractor
{
    public async Task<Result<RepositoryFile>> GetFileAsync(
        string repoUrl,
        string filePath,
        string? branch = null,
        string? commitSha = null,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Reading file {FilePath} from {Repository} (branch: {Branch}, commit: {Commit})",
                    filePath,
                    repoUrl,
                    branch ?? "HEAD",
                    commitSha ?? "current");
            }

            await Task.Delay(100, ct).ConfigureAwait(false);

            var file = new RepositoryFile
            {
                FilePath = filePath,
                Content = "// Placeholder code content",
                LastModified = DateTime.UtcNow,
                LastCommitSha = commitSha ?? "HEAD",
                LastAuthor = "Unknown"
            };

            return Result.Success(file);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading file {FilePath}", filePath);
            return Result.Failure<RepositoryFile>($"Error reading file: {ex.Message}");
        }
    }

    public async Task<Result<string>> GetCodeSnippetAsync(
        string workTreePath,
        string filePath,
        int? startLine = null,
        int? endLine = null,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Extracting snippet from {FilePath} (lines {StartLine}-{EndLine})",
                    filePath,
                    startLine?.ToString(CultureInfo.InvariantCulture) ?? "1",
                    endLine?.ToString(CultureInfo.InvariantCulture) ?? "all");
            }

            await Task.Delay(50, ct).ConfigureAwait(false);

            var fullPath = Path.Combine(workTreePath, filePath);
            if (!File.Exists(fullPath))
            {
                return Result.Failure<string>($"File not found: {filePath}");
            }

            var lines = await File.ReadAllLinesAsync(fullPath, ct).ConfigureAwait(false);
            var start = (startLine ?? 1) - 1;
            var end = endLine ?? lines.Length;

            if (start < 0 || start >= lines.Length)
            {
                return Result.Failure<string>("Invalid start line");
            }

            var snippetLines = lines.Skip(start).Take(end - start).ToList();
            var snippet = string.Join('\n', snippetLines);

            return Result.Success(snippet);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting snippet from {FilePath}", filePath);
            return Result.Failure<string>($"Error extracting snippet: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<string>>> FindRelatedFilesAsync(
        string workTreePath,
        string filePath,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Finding related files for {FilePath}", filePath);
            }

            await Task.Delay(50, ct).ConfigureAwait(false);

            var relatedFiles = new List<string>();

            if (!Directory.Exists(workTreePath))
            {
                return Result.Success((IReadOnlyList<string>)relatedFiles);
            }

            var fileExtension = Path.GetExtension(filePath);
            var directory = Path.GetDirectoryName(filePath) ?? "";

            var files = Directory.EnumerateFiles(
                    Path.Combine(workTreePath, directory),
                    $"*{fileExtension}",
                    SearchOption.AllDirectories)
                .Where(f => !f.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .Select(f => f.Replace(workTreePath, "", StringComparison.Ordinal).TrimStart(Path.DirectorySeparatorChar))
                .ToList();

            relatedFiles.AddRange(files);

            return Result.Success((IReadOnlyList<string>)relatedFiles.AsReadOnly());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error finding related files");
            return Result.Failure<IReadOnlyList<string>>($"Error finding files: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<GitCommitInfo>>> GetFileHistoryAsync(
        string workTreePath,
        string filePath,
        int? maxCommits = null,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Getting history for {FilePath} (max commits: {MaxCommits})",
                    filePath,
                    maxCommits?.ToString(CultureInfo.InvariantCulture) ?? "10");
            }

            await Task.Delay(50, ct).ConfigureAwait(false);

            var commits = new List<GitCommitInfo>();

            return Result.Success((IReadOnlyList<GitCommitInfo>)commits.AsReadOnly());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting file history");
            return Result.Failure<IReadOnlyList<GitCommitInfo>>($"Error getting history: {ex.Message}");
        }
    }

    public async Task<Result<AnalysisContext>> BuildAnalysisContextAsync(
        CodeAnalysisRequest request,
        string workTreePath,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Building analysis context for request {RequestId} targeting {FilePath}",
                    request.Id,
                    request.Location.FilePath);
            }

            // Get the main file
            var fileResult = await GetFileAsync(request.Repository.Url, request.Location.FilePath, ct: ct)
                .ConfigureAwait(false);

            if (fileResult.IsFailure)
            {
                return Result.Failure<AnalysisContext>(fileResult.Error);
            }

            var mainFile = fileResult.Value;

            // Get code snippet
            var snippetResult = await GetCodeSnippetAsync(workTreePath, request.Location.FilePath, ct: ct)
                .ConfigureAwait(false);

            var codeSnippet = snippetResult.IsSuccess ? snippetResult.Value : mainFile.Content;

            // Find related files
            var relatedResult = await FindRelatedFilesAsync(workTreePath, request.Location.FilePath, ct)
                .ConfigureAwait(false);

            var relatedFiles = relatedResult.IsSuccess ? relatedResult.Value : new List<string>();

            // Get file history
            var historyResult = await GetFileHistoryAsync(workTreePath, request.Location.FilePath, 5, ct)
                .ConfigureAwait(false);

            var history = historyResult.IsSuccess ? historyResult.Value : new List<GitCommitInfo>();

            var context = new AnalysisContext
            {
                RepositoryUrl = request.Repository.Url,
                TargetFilePath = request.Location.FilePath,
                CodeSnippet = codeSnippet,
                RelatedFiles = relatedFiles,
                RecentHistory = history
            };

            return Result.Success(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error building analysis context");
            return Result.Failure<AnalysisContext>($"Error building context: {ex.Message}");
        }
    }
}
