using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.DTOs;

#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable CA1056 // Uri properties should not be strings

/// <summary>
///     DTO for submitting a code analysis request
/// </summary>
public record SubmitAnalysisRequest(
    string RepositoryUrl,
    string FilePath,
    AnalysisType Type,
    string Title,
    string Description,
    IReadOnlyList<string> Requirements,
    string? TargetBranch = null,
    string? TargetCommit = null,
    int MaxIterations = 15);
