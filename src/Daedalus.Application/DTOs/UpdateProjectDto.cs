using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

/// <summary>DTO for updating an existing project.</summary>
[Validate]
public record UpdateProjectDto(
    [property: Must(nameof(UpdateProjectDto.IsPresent), Message = "Project name is required.")]
    [property: MaxLength(256, Message = "Project name cannot exceed 256 characters.")]
    string ProjectName,
    [property: Must(nameof(UpdateProjectDto.IsPresent), Message = "Description is required.")]
    [property: MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    string Description,
    [property: Must(nameof(UpdateProjectDto.IsPresent), Message = "Version is required.")]
    [property: MaxLength(50, Message = "Version cannot exceed 50 characters.")]
    string Version)
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
