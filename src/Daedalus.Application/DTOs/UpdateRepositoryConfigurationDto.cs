using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

/// <summary>
///     DTO for updating a repository configuration
/// </summary>
[Validate]
public record UpdateRepositoryConfigurationDto
{
    [Must(nameof(IsPresent), Message = "Repository name is required.")]
    [MaxLength(256, Message = "Repository name cannot exceed 256 characters.")]
    public string Name { get; set; } = string.Empty;

    [SuppressMessage("Design", "CA1056:Uri properties should not be strings")]
    [Must(nameof(IsPresent), Message = "Repository URL is required.")]
    [MaxLength(1024, Message = "Repository URL cannot exceed 1024 characters.")]
    public string Url { get; set; } = string.Empty;

    [Must(nameof(IsPresent), Message = "Default branch is required.")]
    [MaxLength(256, Message = "Default branch cannot exceed 256 characters.")]
    public string DefaultBranch { get; set; } = "main";

    [MaxLength(100, Message = "Authentication method cannot exceed 100 characters.")]
    public string? AuthenticationMethod { get; set; }

    [MaxLength(512, Message = "Credential identifier cannot exceed 512 characters.")]
    public string? CredentialIdentifier { get; set; }

    public bool IsActive { get; set; }

    [MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    public string? Description { get; set; }

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
