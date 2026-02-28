using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

#pragma warning disable CA1873 // Logging arguments are cheap property accesses (Length, Count)

/// <summary>
///     Applies code changes from AI responses to repository files.
///     Parses file paths and code blocks from Claude's output in these formats:
///     1. ```lang:path/to/File.cs  (colon-separated after language tag)
///     2. **path/to/File.cs** followed by a code fence
///     3. `path/to/File.cs` followed by a code fence
///     4. ### Heading (path/to/File.cs) followed by a code fence (parenthesized path in heading)
/// </summary>
public sealed partial class GitChangeApplier(ILogger<GitChangeApplier> logger) : IGitChangeApplier
{
    public Task<Result<IReadOnlyList<CodeModification>>> ExtractChangesAsync(
        string aiResponse,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("Extracting code modifications from AI response ({Length} chars)", aiResponse.Length);

            var modifications = new List<CodeModification>();

            // Pattern 1: ```lang:path/to/File.ext (colon after language tag, path before newline)
            var colonPathMatches = ColonPathCodeBlockRegex().Matches(aiResponse);
#pragma warning disable S3267 // Manual loop preferred for clarity with multi-group regex matches
            foreach (var match in colonPathMatches.Cast<Match>())
#pragma warning restore S3267
            {
                var filePath = NormalizePath(match.Groups["path"].Value);
                var code = match.Groups["code"].Value.Trim();
                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(filePath))
                {
                    modifications.Add(new CodeModification
                    {
                        FilePath = filePath,
                        ModifiedCode = code,
                        ChangeDescription = "Extracted from code block with path annotation"
                    });
                }
            }

            // Pattern 2: **path/to/File.ext** or `path/to/File.ext` on line before ```
            // Only add if not already captured by pattern 1
            var headerPathMatches = HeaderPathCodeBlockRegex().Matches(aiResponse);
#pragma warning disable S3267 // Manual loop preferred for clarity with multi-group regex matches
            foreach (var match in headerPathMatches.Cast<Match>())
#pragma warning restore S3267
            {
                var filePath = NormalizePath(
                    match.Groups["boldpath"].Success ? match.Groups["boldpath"].Value : match.Groups["tickpath"].Value);
                var code = match.Groups["code"].Value.Trim();

                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(filePath))
                {
                    continue;
                }

                // Skip if we already have this file from pattern 1
                if (modifications.Exists(m => string.Equals(m.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                modifications.Add(new CodeModification
                {
                    FilePath = filePath,
                    ModifiedCode = code,
                    ChangeDescription = "Extracted from code block with header path"
                });
            }

            // Pattern 3: ### Heading (path/to/File.ext) followed by a code fence
            // Common Claude format: "### 1. Create the Solution File (HelloWorld.sln)\n\n```sln\n..."
            var parenPathMatches = ParenPathCodeBlockRegex().Matches(aiResponse);
#pragma warning disable S3267 // Manual loop preferred for clarity with multi-group regex matches
            foreach (var match in parenPathMatches.Cast<Match>())
#pragma warning restore S3267
            {
                var filePath = NormalizePath(match.Groups["parenpath"].Value);
                var code = match.Groups["code"].Value.Trim();

                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(filePath))
                {
                    continue;
                }

                // Skip if we already have this file from earlier patterns
                if (modifications.Exists(m => string.Equals(m.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                modifications.Add(new CodeModification
                {
                    FilePath = filePath,
                    ModifiedCode = code,
                    ChangeDescription = "Extracted from code block with parenthesized path in heading"
                });
            }

            logger.LogInformation("Extracted {Count} code modifications", modifications.Count);
            return Task.FromResult(
                Result.Success((IReadOnlyList<CodeModification>)modifications.AsReadOnly()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting modifications from AI response");
            return Task.FromResult(
                Result.Failure<IReadOnlyList<CodeModification>>($"Error extracting changes: {ex.Message}"));
        }
    }

    public async Task<Result> ApplyChangesAsync(
        string workTreePath,
        IReadOnlyList<CodeModification> changes,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Applying {ChangeCount} modifications to working tree at {Path}",
                    changes.Count,
                    workTreePath);
            }

            if (!Directory.Exists(workTreePath))
            {
                return Result.Failure($"Working tree path does not exist: {workTreePath}");
            }

            foreach (var change in changes)
            {
                if (string.IsNullOrEmpty(change.FilePath))
                {
                    continue;
                }

                var filePath = Path.Combine(workTreePath, change.FilePath);
                var directory = Path.GetDirectoryName(filePath);

                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // For now, create/update the file with the modified code
                await File.WriteAllTextAsync(filePath, change.ModifiedCode, ct).ConfigureAwait(false);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Applied modification to {FilePath}", change.FilePath);
                }
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error applying changes to working tree");
            return Result.Failure($"Error applying changes: {ex.Message}");
        }
    }

    public Task<Result<string>> GeneratePatchAsync(
        string workTreePath,
        string baseBranch,
        CancellationToken ct = default)
    {
        // Patch generation delegated to GitRepositoryManager.GetDiffsAsync for real implementations
        logger.LogInformation("Generating patch from {Path} against {BaseBranch}", workTreePath, baseBranch);
        return Task.FromResult(Result.Success(string.Empty));
    }

    public Task<Result> RevertChangesAsync(
        string workTreePath,
        string baseBranch,
        CancellationToken ct = default)
    {
        // Revert delegated to GitWorkflowService.ResetToLastGoodAsync for real implementations
        logger.LogInformation("Reverting changes in {Path} to {BaseBranch}", workTreePath, baseBranch);

        if (!Directory.Exists(workTreePath))
        {
            return Task.FromResult(Result.Failure($"Working tree path does not exist: {workTreePath}"));
        }

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    ///     Normalizes a file path from LLM output: converts backslashes, trims leading slashes.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var normalized = path.Trim()
            .Replace('\\', '/')
            .TrimStart('/');

        // Remove any trailing colon or punctuation
        normalized = normalized.TrimEnd(':', ',', ';');

        return normalized;
    }

    // Pattern: ```lang:path/to/File.ext\n...code...\n```
    [GeneratedRegex(
        @"```\w+:(?<path>[^\s\n]+)\s*\n(?<code>[\s\S]*?)```",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 5000)]
    private static partial Regex ColonPathCodeBlockRegex();

    // Pattern: **path/to/File.ext** or `path/to/File.ext` on the line before a code fence
    [GeneratedRegex(
        @"(?:\*\*(?<boldpath>[^\s*]+\.\w+)\*\*|`(?<tickpath>[^\s`]+\.\w+)`).*?\n\s*```\w*\s*\n(?<code>[\s\S]*?)```",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 5000)]
    private static partial Regex HeaderPathCodeBlockRegex();

    // Pattern: (path/to/File.ext) in a heading/line, followed within a few lines by a code fence
    // Matches: "### Create the File (src/HelloWorld/Program.cs)\n\n```csharp\n...```"
    [GeneratedRegex(
        @"\((?<parenpath>[^\s()]+\.\w+)\)[^\n]*\n\s*\n?\s*```\w*\s*\n(?<code>[\s\S]*?)```",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 5000)]
    private static partial Regex ParenPathCodeBlockRegex();
}
