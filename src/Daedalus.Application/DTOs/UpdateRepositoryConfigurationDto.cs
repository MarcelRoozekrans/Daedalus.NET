using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

/// <summary>
///     DTO for updating a repository configuration
/// </summary>
[Validate]
public record UpdateRepositoryConfigurationDto
{
    [NotEmpty(Message = "Repository name is required.")]
    [MaxLength(256, Message = "Repository name cannot exceed 256 characters.")]
    public string Name { get; set; } = string.Empty;

    [SuppressMessage("Design", "CA1056:Uri properties should not be strings")]
    [NotEmpty(Message = "Repository URL is required.")]
    [MaxLength(1024, Message = "Repository URL cannot exceed 1024 characters.")]
    public string Url { get; set; } = string.Empty;

    [NotEmpty(Message = "Default branch is required.")]
    [MaxLength(256, Message = "Default branch cannot exceed 256 characters.")]
    public string DefaultBranch { get; set; } = "main";

    [MaxLength(100, Message = "Authentication method cannot exceed 100 characters.")]
    public string? AuthenticationMethod { get; set; }

    [MaxLength(512, Message = "Credential identifier cannot exceed 512 characters.")]
    public string? CredentialIdentifier { get; set; }

    public bool IsActive { get; set; }

    [MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    public string? Description { get; set; }
}
