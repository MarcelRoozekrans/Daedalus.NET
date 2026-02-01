using CSharpFunctionalExtensions;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.Services.CodeAnalysis;

/// <summary>
///     Generates analysis prompts for Ralph Loop
/// </summary>
public interface IAnalysisPromptBuilder
{
    Task<Result<string>> BuildPromptAsync(
        CodeAnalysisRequest request,
        AnalysisContext context,
        CancellationToken ct = default);

    Task<Result<string>> BuildFeedbackPromptAsync(
        CodeAnalysisRequest request,
        AnalysisContext context,
        string validationErrors,
        CancellationToken ct = default);
}
