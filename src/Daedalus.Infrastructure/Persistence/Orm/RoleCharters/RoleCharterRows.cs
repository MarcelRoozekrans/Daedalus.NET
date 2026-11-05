using Daedalus.Domain.Entities;

namespace Daedalus.Infrastructure.Persistence.Orm.RoleCharters;

/// <summary>One <c>"RoleCharterVersions"</c> row, every column, as <see cref="RoleCharterSql"/> reads it.</summary>
internal sealed record RoleCharterVersionRow(
    string Role,
    string ContentHash,
    string Description,
    string Instructions,
    string? Model,
    string SourcePath,
    DateTime UpdatedAt,
    IReadOnlyList<string> Skills)
{
    /// <summary>The version the row holds, as stored: <see cref="RoleCharterVersion.Rehydrate"/> validates nothing.</summary>
    /// <returns>The domain version.</returns>
    public RoleCharterVersion ToDomain() =>
        RoleCharterVersion.Rehydrate(Role, ContentHash, Description, Instructions, Model, Skills, SourcePath, UpdatedAt);
}

/// <summary>One <c>"RoleCharters"</c> row, every column, as <see cref="RoleCharterSql"/> reads it.</summary>
internal sealed record RoleCharterHeadRow(string Role, string CurrentHash, bool IsActive, DateTime UpdatedAt)
{
    /// <summary>The head the row holds, as stored.</summary>
    /// <returns>The domain head.</returns>
    public RoleCharterHead ToDomain() => RoleCharterHead.Rehydrate(Role, CurrentHash, IsActive, UpdatedAt);
}
