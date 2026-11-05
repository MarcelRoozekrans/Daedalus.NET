using RepositoryConfiguration = Daedalus.Domain.Entities.RepositoryConfiguration;

namespace Daedalus.Infrastructure.Persistence.Orm.RepositoryConfigurations;

/// <summary>
///     One <c>"RepositoryConfigurations"</c> row, every column but the dead row version, as
///     <see cref="RepositoryConfigurationSql"/> reads it.
/// </summary>
internal sealed record RepositoryConfigurationRow(
    Guid Id,
    string Name,
    string Url,
    string Platform,
    string DefaultBranch,
    string? AuthenticationMethod,
    string? CredentialIdentifier,
    bool IsActive,
    string? Description,
    DateTime CreatedAt,
    DateTime? ModifiedAt,
    DateTime? LastUsedAt)
{
    /// <summary>
    ///     The configuration the row holds, as stored: <see cref="RepositoryConfiguration.Rehydrate"/> validates nothing.
    /// </summary>
    /// <returns>The domain configuration.</returns>
    public RepositoryConfiguration ToDomain() =>
        RepositoryConfiguration.Rehydrate(
            Id, Name, Url, Platform, DefaultBranch, AuthenticationMethod, CredentialIdentifier, IsActive, Description,
            CreatedAt, ModifiedAt, LastUsedAt);
}
