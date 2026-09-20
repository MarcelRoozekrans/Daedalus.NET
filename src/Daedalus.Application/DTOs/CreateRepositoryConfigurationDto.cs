using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

/// <summary>
///     DTO for creating a new repository configuration
/// </summary>
[Validate]
public record CreateRepositoryConfigurationDto
{
    private static readonly string[] ValidPlatforms = ["GitHub", "GitLab", "AzureDevOps", "Bitbucket", "Gitea"];

    [NotEmpty(Message = "Repository name is required.")]
    [MaxLength(256, Message = "Repository name cannot exceed 256 characters.")]
    public string Name { get; set; } = string.Empty;

    [SuppressMessage("Design", "CA1056:Uri properties should not be strings")]
    [NotEmpty(Message = "Repository URL is required.")]
    [MaxLength(1024, Message = "Repository URL cannot exceed 1024 characters.")]
    public string Url { get; set; } = string.Empty;

    [NotEmpty(Message = "Platform is required.")]
    [Must(nameof(IsValidPlatform), Message = "Platform must be one of: GitHub, GitLab, AzureDevOps, Bitbucket, Gitea.")]
    public string Platform { get; set; } = "GitHub";

    [NotEmpty(Message = "Default branch is required.")]
    [MaxLength(256, Message = "Default branch cannot exceed 256 characters.")]
    public string DefaultBranch { get; set; } = "main";

    [MaxLength(100, Message = "Authentication method cannot exceed 100 characters.")]
    public string? AuthenticationMethod { get; set; }

    [MaxLength(512, Message = "Credential identifier cannot exceed 512 characters.")]
    public string? CredentialIdentifier { get; set; }

    [MaxLength(2000, Message = "Description cannot exceed 2000 characters.")]
    public string? Description { get; set; }

    [SuppressMessage("Performance", "CA1822", Justification = "ZeroAlloc.Validation's [Must] attribute requires an instance method.")]
    [SuppressMessage("Major Code Smell", "S2325", Justification = "ZeroAlloc.Validation's [Must] attribute requires an instance method.")]
    internal bool IsValidPlatform(string value) => ValidPlatforms.Contains(value, StringComparer.OrdinalIgnoreCase);
}
