using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Loads workspace context files from the file system for injection into Ralph loop prompts.
///     Reads specs, plan, and agent instruction files deterministically every iteration,
///     following the RLP pattern of "allocating the stack the same way every loop".
/// </summary>
public sealed partial class FileSystemWorkspaceContextProvider(
    ILogger<FileSystemWorkspaceContextProvider> logger) : IWorkspaceContextProvider
{
    /// <summary>Maximum file size to load (256 KB). Larger files are truncated.</summary>
    private const int _maxFileSizeBytes = 256 * 1024;

    /// <summary>Maximum total context size across all files (1 MB).</summary>
    private const int _maxTotalContextBytes = 1024 * 1024;

    /// <summary>Default search paths for copilot instructions, in priority order.</summary>
    private static readonly string[] _defaultCopilotInstructionsPaths =
    [
        ".github/copilot-instructions.md",
        ".github/copilot_instructions.md",
        "copilot-instructions.md",
        "COPILOT.md"
    ];

    public async Task<Result<WorkspaceContext>> LoadWorkspaceContextAsync(
        string workspacePath,
        string specsGlob = "specs/**/*.md",
        string planFileName = "fix_plan.md",
        string agentFileName = "AGENT.md",
        IReadOnlyList<string>? copilotInstructionsPaths = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result.Failure<WorkspaceContext>("Workspace path cannot be empty");
        }

        if (!Directory.Exists(workspacePath))
        {
            return Result.Failure<WorkspaceContext>($"Workspace directory not found: {workspacePath}");
        }

        var context = new WorkspaceContext { WorkspacePath = workspacePath, LoadedAt = DateTime.UtcNow };

        var totalBytesLoaded = 0;

        // Load specification files (extract directory and extension from glob pattern)
        var specsResult = await LoadSpecificationsAsync(workspacePath, specsGlob, ct).ConfigureAwait(false);
        if (specsResult.IsSuccess)
        {
            context.Specifications = specsResult.Value;
            totalBytesLoaded += specsResult.Value.Values.Sum(v => Encoding.UTF8.GetByteCount(v));
            LogSpecsLoaded(logger, specsResult.Value.Count, totalBytesLoaded);
        }
        else
        {
            LogSpecsLoadFailed(logger, specsResult.Error);
            context.Specifications = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // Load plan file
        if (totalBytesLoaded < _maxTotalContextBytes)
        {
            var planResult = await LoadFileAsync(workspacePath, planFileName, ct).ConfigureAwait(false);
            if (planResult.IsSuccess)
            {
                context.PlanContent = planResult.Value;
                context.PlanFilePath = planFileName;
                totalBytesLoaded += Encoding.UTF8.GetByteCount(planResult.Value);
                LogPlanLoaded(logger, planFileName, planResult.Value.Length);
            }
            else
            {
                LogPlanNotFound(logger, planFileName);
            }
        }

        // Load agent instructions file
        if (totalBytesLoaded < _maxTotalContextBytes)
        {
            var agentResult = await LoadFileAsync(workspacePath, agentFileName, ct).ConfigureAwait(false);
            if (agentResult.IsSuccess)
            {
                context.AgentInstructions = agentResult.Value;
                context.AgentInstructionsPath = agentFileName;
                totalBytesLoaded += Encoding.UTF8.GetByteCount(agentResult.Value);
                LogAgentInstructionsLoaded(logger, agentFileName, agentResult.Value.Length);
            }
            else
            {
                LogAgentInstructionsNotFound(logger, agentFileName);
            }
        }

        // Load copilot instructions from the target repo (coding standards / style guide)
        if (totalBytesLoaded < _maxTotalContextBytes)
        {
            var searchPaths = copilotInstructionsPaths ?? _defaultCopilotInstructionsPaths;
            var copilotResult = await LoadFirstMatchingFileAsync(workspacePath, searchPaths, ct).ConfigureAwait(false);
            if (copilotResult.IsSuccess)
            {
                var (matchedPath, content) = copilotResult.Value;
                context.CopilotInstructions = content;
                context.CopilotInstructionsPath = matchedPath;
                totalBytesLoaded += Encoding.UTF8.GetByteCount(content);
                LogCopilotInstructionsLoaded(logger, matchedPath, content.Length);
            }
            else
            {
                LogCopilotInstructionsNotFound(logger);
            }
        }

        // Load .editorconfig for formatting rules
        if (totalBytesLoaded < _maxTotalContextBytes)
        {
            var editorConfigResult = await LoadFileAsync(workspacePath, ".editorconfig", ct).ConfigureAwait(false);
            if (editorConfigResult.IsSuccess)
            {
                context.EditorConfig = editorConfigResult.Value;
                totalBytesLoaded += Encoding.UTF8.GetByteCount(editorConfigResult.Value);
                LogEditorConfigLoaded(logger, editorConfigResult.Value.Length);
            }
        }

        LogWorkspaceContextLoaded(logger, workspacePath, totalBytesLoaded, context.TotalCharactersLoaded);

        return Result.Success(context);
    }

    /// <summary>
    ///     Loads specification files from a directory pattern.
    ///     Parses the glob pattern to extract the base directory and file extension.
    /// </summary>
    private async Task<Result<IReadOnlyDictionary<string, string>>> LoadSpecificationsAsync(
        string workspacePath,
        string specsGlob,
        CancellationToken ct)
    {
        try
        {
            // Parse the glob pattern to extract directory and extension
            // e.g., "specs/**/*.md" → baseDir="specs", extension=".md"
            var (baseDir, extension) = ParseGlobPattern(specsGlob);
            var specsDir = Path.Combine(workspacePath, baseDir);

            if (!Directory.Exists(specsDir))
            {
                return Result.Success<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string>(StringComparer.Ordinal));
            }

            var specs = new Dictionary<string, string>(StringComparer.Ordinal);
            var totalBytes = 0;
            var searchPattern = string.IsNullOrEmpty(extension) ? "*" : $"*{extension}";

            var files = Directory.EnumerateFiles(specsDir, searchPattern, SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal); // Deterministic ordering

            foreach (var fullPath in files)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (totalBytes >= _maxTotalContextBytes)
                {
                    LogMaxContextSizeReached(logger, totalBytes);
                    break;
                }

                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Length > _maxFileSizeBytes)
                {
                    var relativePath = Path.GetRelativePath(workspacePath, fullPath);
                    LogFileSkippedTooLarge(logger, relativePath, fileInfo.Length, _maxFileSizeBytes);
                    continue;
                }

                var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
                var relPath = Path.GetRelativePath(workspacePath, fullPath).Replace('\\', '/');
                specs[relPath] = content;
                totalBytes += Encoding.UTF8.GetByteCount(content);
            }

            return Result.Success<IReadOnlyDictionary<string, string>>(specs);
        }
        catch (Exception ex)
        {
            LogSpecsLoadError(logger, ex, specsGlob);
            return Result.Failure<IReadOnlyDictionary<string, string>>($"Failed to load specs: {ex.Message}");
        }
    }

    /// <summary>
    ///     Parses a simple glob pattern into a base directory and file extension.
    /// </summary>
    private static (string baseDir, string extension) ParseGlobPattern(string glob)
    {
        // Handle patterns like "specs/**/*.md", "docs/*.txt", "src/**/*.cs"
        var parts = glob.Split('/');
        var baseDir = string.Empty;
        var extension = string.Empty;

        foreach (var part in parts)
        {
            if (part.Contains('*', StringComparison.Ordinal))
            {
                // Extract extension from the wildcard part (e.g., "*.md" → ".md")
                var dotIndex = part.LastIndexOf('.');
                if (dotIndex >= 0)
                {
                    extension = part[dotIndex..];
                }

                break;
            }

            baseDir = string.IsNullOrEmpty(baseDir) ? part : $"{baseDir}/{part}";
        }

        return (baseDir, extension);
    }

    /// <summary>
    ///     Loads the first matching file from a list of candidate paths.
    ///     Used for copilot instructions discovery where files may be at different locations.
    /// </summary>
    private static async Task<Result<(string matchedPath, string content)>> LoadFirstMatchingFileAsync(
        string workspacePath,
        IReadOnlyList<string> candidatePaths,
        CancellationToken ct)
    {
        foreach (var relativePath in candidatePaths)
        {
            var result = await LoadFileAsync(workspacePath, relativePath, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return Result.Success((relativePath, result.Value));
            }
        }

        return Result.Failure<(string, string)>("No matching file found in any of the candidate paths");
    }

    /// <summary>
    ///     Loads a single file from the workspace.
    /// </summary>
    private static async Task<Result<string>> LoadFileAsync(
        string workspacePath,
        string fileName,
        CancellationToken ct)
    {
        var fullPath = Path.Combine(workspacePath, fileName);

        if (!File.Exists(fullPath))
        {
            return Result.Failure<string>($"File not found: {fileName}");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > _maxFileSizeBytes)
        {
            return Result.Failure<string>($"File too large ({fileInfo.Length} bytes): {fileName}");
        }

        var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        return Result.Success(content);
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Loaded {SpecCount} specification files ({TotalBytes} bytes)")]
    private static partial void LogSpecsLoaded(ILogger logger, int specCount, int totalBytes);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Failed to load specifications: {Error}")]
    private static partial void LogSpecsLoadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Plan file loaded: {FileName} ({ContentLength} chars)")]
    private static partial void LogPlanLoaded(ILogger logger, string fileName, int contentLength);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "Plan file not found: {FileName} (this is normal for new projects)")]
    private static partial void LogPlanNotFound(ILogger logger, string fileName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Agent instructions loaded: {FileName} ({ContentLength} chars)")]
    private static partial void LogAgentInstructionsLoaded(ILogger logger, string fileName, int contentLength);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "Agent instructions file not found: {FileName} (this is normal for new projects)")]
    private static partial void LogAgentInstructionsNotFound(ILogger logger, string fileName);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message =
            "Workspace context loaded from {WorkspacePath}: {TotalBytes} bytes, {TotalChars} characters")]
    private static partial void LogWorkspaceContextLoaded(ILogger logger, string workspacePath, int totalBytes,
        int totalChars);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "Max context size reached ({TotalBytes} bytes), skipping remaining files")]
    private static partial void LogMaxContextSizeReached(ILogger logger, int totalBytes);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "File skipped (too large): {FilePath} ({FileSize} bytes, max {MaxSize} bytes)")]
    private static partial void LogFileSkippedTooLarge(ILogger logger, string filePath, long fileSize, int maxSize);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "Error loading specs with glob {SpecsGlob}")]
    private static partial void LogSpecsLoadError(ILogger logger, Exception exception, string specsGlob);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information,
        Message = "Copilot instructions loaded from target repo: {FilePath} ({ContentLength} chars)")]
    private static partial void LogCopilotInstructionsLoaded(ILogger logger, string filePath, int contentLength);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug,
        Message = "No copilot instructions file found in target repo (checked default paths)")]
    private static partial void LogCopilotInstructionsNotFound(ILogger logger);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information,
        Message = ".editorconfig loaded from target repo ({ContentLength} chars)")]
    private static partial void LogEditorConfigLoaded(ILogger logger, int contentLength);
}
