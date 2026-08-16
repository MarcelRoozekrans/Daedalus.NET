using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Application.DTOs;

/// <summary>
///     DTO for updating a repository configuration
/// </summary>
public record UpdateRepositoryConfigurationDto
{
    public string Name { get; set; } = string.Empty;

    [SuppressMessage("Design", "CA1056:Uri properties should not be strings")]
    public string Url { get; set; } = string.Empty;

    public string DefaultBranch { get; set; } = "main";
    public string? AuthenticationMethod { get; set; }
    public string? CredentialIdentifier { get; set; }
    public bool IsActive { get; set; }
    public string? Description { get; set; }
}
