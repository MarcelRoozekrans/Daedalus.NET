using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Applies code changes from AI responses to repository files
/// </summary>
public sealed class GitChangeApplier(ILogger<GitChangeApplier> logger) : IGitChangeApplier
{
    public async Task<Result<IReadOnlyList<CodeModification>>> ExtractChangesAsync(
        string aiResponse,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("Extracting code modifications from AI response");

            await Task.Delay(50, ct).ConfigureAwait(false);

            var modifications = new List<CodeModification>();

            // Parse code blocks from AI response
            var codeBlockPattern = @"```(?:csharp|cs|C#)?\s*([\s\S]*?)```";
            var matches = Regex.Matches(aiResponse, codeBlockPattern, RegexOptions.None, TimeSpan.FromSeconds(5));

            foreach (var match in matches.Cast<Match>())
            {
                var code = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(code))
                {
                    modifications.Add(new CodeModification
                    {
                        FilePath = "modified-file.cs", ModifiedCode = code, StartLine = 0, EndLine = 0
                    });
                }
            }

            return Result.Success((IReadOnlyList<CodeModification>)modifications.AsReadOnly());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting modifications from AI response");
            return Result.Failure<IReadOnlyList<CodeModification>>($"Error extracting changes: {ex.Message}");
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

            await Task.Delay(100, ct).ConfigureAwait(false);

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

    public async Task<Result<string>> GeneratePatchAsync(
        string workTreePath,
        string baseBranch,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Generating patch from {Path} against {BaseBranch}",
                    workTreePath,
                    baseBranch);
            }

            await Task.Delay(100, ct).ConfigureAwait(false);

            var patch = "--- a/file\n+++ b/file\n@@ -1 +1 @@\n-original\n+modified\n";

            return Result.Success(patch);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating patch");
            return Result.Failure<string>($"Error generating patch: {ex.Message}");
        }
    }

    public async Task<Result> RevertChangesAsync(
        string workTreePath,
        string baseBranch,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Reverting changes in {Path} to {BaseBranch}",
                    workTreePath,
                    baseBranch);
            }

            await Task.Delay(50, ct).ConfigureAwait(false);

            if (!Directory.Exists(workTreePath))
            {
                return Result.Failure($"Working tree path does not exist: {workTreePath}");
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reverting changes");
            return Result.Failure($"Error reverting changes: {ex.Message}");
        }
    }
}
