using Daedalus.Domain.Entities;

namespace Daedalus.Infrastructure.Persistence.Orm.Skills;

/// <summary>One <c>"Skills"</c> row, every column, as <see cref="SkillSql"/> reads it.</summary>
internal sealed record SkillRow(
    string Name,
    string Description,
    string Body,
    IReadOnlyList<string> Tags,
    string SourcePath,
    string ContentHash,
    bool IsActive,
    DateTime UpdatedAt)
{
    /// <summary>The skill the row holds, as stored: <see cref="Skill.Rehydrate"/> validates nothing.</summary>
    /// <returns>The domain skill.</returns>
    public Skill ToDomain() =>
        Skill.Rehydrate(Name, Description, Body, Tags, SourcePath, ContentHash, IsActive, UpdatedAt);
}

/// <summary>One <c>"SkillVersions"</c> row, every column, as <see cref="SkillSql"/> reads it.</summary>
internal sealed record SkillVersionRow(
    string Name,
    string ContentHash,
    string Description,
    string Body,
    IReadOnlyList<string> Tags,
    string SourcePath,
    DateTime CreatedAt)
{
    /// <summary>The version the row holds, as stored: <see cref="SkillVersion.Rehydrate"/> validates nothing.</summary>
    /// <returns>The domain version.</returns>
    public SkillVersion ToDomain() =>
        SkillVersion.Rehydrate(Name, ContentHash, Description, Body, Tags, SourcePath, CreatedAt);
}
