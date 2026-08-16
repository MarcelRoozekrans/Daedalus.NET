using CSharpFunctionalExtensions;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.Services.CodeAnalysis;

/// <summary>
///     Detects repository platform from URL
/// </summary>
#pragma warning disable CA1054 // Uri parameters should not be strings — URLs are passed as strings through the pipeline
public interface IRepositoryPlatformDetector
{
    RepositoryPlatform DetectPlatform(string repositoryUrl);

    Task<Result<RepositoryInfo>> ParseRepositoryUrlAsync(
        string repositoryUrl,
        CancellationToken ct = default);
}
