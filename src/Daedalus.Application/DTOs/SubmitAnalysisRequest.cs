using System.Diagnostics.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable CA1056 // Uri properties should not be strings

/// <summary>
///     DTO for submitting a code analysis request
/// </summary>
[Validate]
public record SubmitAnalysisRequest(
    [property: Must(nameof(SubmitAnalysisRequest.IsPresent), Message = "Repository URL is required.")]
    [property: MaxLength(1024, Message = "Repository URL cannot exceed 1024 characters.")]
    string RepositoryUrl,
    [property: Must(nameof(SubmitAnalysisRequest.IsPresent), Message = "File path is required.")]
    [property: MaxLength(1024, Message = "File path cannot exceed 1024 characters.")]
    string FilePath,
    [property: IsInEnum(Message = "Invalid analysis type.")]
    AnalysisType Type,
    [property: Must(nameof(SubmitAnalysisRequest.IsPresent), Message = "Title is required.")]
    [property: MaxLength(256, Message = "Title cannot exceed 256 characters.")]
    string Title,
    [property: Must(nameof(SubmitAnalysisRequest.IsPresent), Message = "Description is required.")]
    [property: MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    string Description,
    IReadOnlyList<string> Requirements,
    string? TargetBranch = null,
    string? TargetCommit = null,
    [property: InclusiveBetween(1, 100, Message = "Max iterations must be between 1 and 100.")]
    int MaxIterations = 15)
{
    /// <summary>
    ///     Backs the <c>[Must]</c> rules above. FluentValidation's <c>NotEmpty()</c> rejects
    ///     whitespace-only strings; ZeroAlloc.Validation's <c>[NotEmpty]</c> lowers to
    ///     <c>string.IsNullOrEmpty</c>, which does not, so a plain <c>[NotEmpty]</c> here would
    ///     silently accept "   ".
    /// </summary>
    [SuppressMessage("Performance", "CA1822", Justification = "ZeroAlloc.Validation's [Must] attribute requires an instance method.")]
    [SuppressMessage("Major Code Smell", "S2325", Justification = "ZeroAlloc.Validation's [Must] attribute requires an instance method.")]
    internal bool IsPresent(string value) => !string.IsNullOrWhiteSpace(value);
}
