using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.DTOs;

#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable CA1056 // Uri properties should not be strings

/// <summary>
///     DTO for code analysis request response
/// </summary>
public record CodeAnalysisRequestDto(
    Guid Id,
    string Title,
    string Description,
    string RepositoryUrl,
    string FilePath,
    AnalysisType Type,
    AnalysisStatus Status,
    int CurrentIteration,
    int MaxIterations,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);
