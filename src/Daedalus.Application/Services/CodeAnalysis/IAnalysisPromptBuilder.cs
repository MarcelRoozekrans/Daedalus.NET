using Daedalus.Domain.CodeAnalysis;
using ZeroAlloc.Results;

namespace Daedalus.Application.Services.CodeAnalysis;

/// <summary>
///     Generates the prompts for iterative code analysis
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
