using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Builds analysis prompts with code context for the Ralph Loop
/// </summary>
public sealed class AnalysisPromptBuilder(ILogger<AnalysisPromptBuilder> logger) : IAnalysisPromptBuilder
{
    public async Task<Result<string>> BuildPromptAsync(
        CodeAnalysisRequest request,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Building analysis prompt for request {RequestId} ({AnalysisType})",
                    request.Id,
                    request.Type);
            }

            await Task.Delay(100, ct).ConfigureAwait(false);

            var relatedFilesContext = FormatRelatedFiles(context.RelatedFiles);
            var historyContext = FormatHistory(context.RecentHistory);

            var requirementsArray = request.Requirements.Split('\n').Where(r => !string.IsNullOrWhiteSpace(r)).ToList();

            var prompt = $"""
                          # Code Analysis Request: {request.Title}

                          **Description:** {request.Description}

                          **Analysis Type:** {request.Type}

                          **Target File:** {context.TargetFilePath}

                          **Requirements:**
                          {string.Join('\n', requirementsArray.Select((r, i) => $"{i + 1}. {r}"))}

                          ## Current Code

                          ```csharp
                          {context.CodeSnippet}
                          ```

                          {relatedFilesContext}

                          {historyContext}

                          ## Task

                          Please analyze this code and provide improvements that address all requirements.

                          Format your response with clear code blocks marked with the appropriate language specifier.

                          Include a summary of changes at the end using <promise>SUMMARY_OF_CHANGES</promise> tags.
                          """;

            return Result.Success(prompt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error building analysis prompt");
            return Result.Failure<string>($"Error building prompt: {ex.Message}");
        }
    }

    public async Task<Result<string>> BuildFeedbackPromptAsync(
        CodeAnalysisRequest request,
        AnalysisContext context,
        string validationErrors,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Building feedback prompt for request {RequestId} iteration",
                    request.Id);
            }

            await Task.Delay(100, ct).ConfigureAwait(false);

            var requirementsArray = request.Requirements.Split('\n').Where(r => !string.IsNullOrWhiteSpace(r)).ToList();

            var prompt = $"""
                          # Code Analysis Refinement Request

                          **Request ID:** {request.Id}

                          **Original Task:** {request.Title}

                          **Target File:** {context.TargetFilePath}

                          ## Previous Attempt Feedback

                          The following issues were found with the previous attempt:

                          ```
                          {validationErrors}
                          ```

                          ## Requirements (Unchanged)

                          {string.Join('\n', requirementsArray.Select((r, i) => $"{i + 1}. {r}"))}

                          ## Current Code

                          ```csharp
                          {context.CodeSnippet}
                          ```

                          ## Task

                          Please address the validation issues and provide an improved solution that:
                          1. Fixes all the issues listed above
                          2. Maintains code quality and best practices
                          3. Meets all original requirements

                          Format your response with clear code blocks marked with the appropriate language specifier.

                          Include a detailed summary of how you addressed each issue using <promise>REFINEMENT_SUMMARY</promise> tags.
                          """;

            return Result.Success(prompt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error building feedback prompt");
            return Result.Failure<string>($"Error building feedback prompt: {ex.Message}");
        }
    }

    private static string FormatRelatedFiles(IReadOnlyList<string> relatedFiles)
    {
        if (relatedFiles.Count == 0)
        {
            return "";
        }

        return $"""

                ## Related Files in Repository

                The following files may be relevant to this analysis:

                {string.Join('\n', relatedFiles.Take(10).Select(f => $"- {f}"))}
                """;
    }

    private static string FormatHistory(IReadOnlyList<GitCommitInfo> history)
    {
        if (history.Count == 0)
        {
            return "";
        }

        return $"""

                ## Recent File History

                Recent commits affecting this file:

                {string.Join('\n', history.Take(5).Select(c => $"- {c.Message} ({c.Date:yyyy-MM-dd})"))}
                """;
    }
}
