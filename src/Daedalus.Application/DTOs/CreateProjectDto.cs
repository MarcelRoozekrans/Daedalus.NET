using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

/// <summary>DTO for creating a new project.</summary>
[SuppressMessage("Design", "CA1054", Justification = "DTO uses string for JSON serialization")]
[SuppressMessage("Design", "CA1056", Justification = "DTO uses string for JSON serialization")]
[Validate]
public record CreateProjectDto(
    [property: Must(nameof(CreateProjectDto.IsPresent), Message = "Project name is required.")]
    [property: MaxLength(256, Message = "Project name cannot exceed 256 characters.")]
    string ProjectName,
    [property: Must(nameof(CreateProjectDto.IsPresent), Message = "Description is required.")]
    [property: MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    string Description,
    [property: Must(nameof(CreateProjectDto.IsPresent), Message = "Version is required.")]
    [property: MaxLength(50, Message = "Version cannot exceed 50 characters.")]
    string Version,
    string? RepositoryUrl = null,
    string? DefaultBranch = null)
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
