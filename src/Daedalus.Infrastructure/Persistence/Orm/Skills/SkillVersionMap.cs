using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.Skills;

/// <summary>
///     Maps <see cref="SkillVersion"/> to <c>"SkillVersions"</c>. Every column carries its property's name;
///     <see cref="SkillVersion.Tags"/>, a hand-written view over the private list, is the <c>text[]</c> column.
/// </summary>
[TableMap(typeof(SkillVersion), "SkillVersions")]
[ColumnMap(nameof(SkillVersion.Tags))]
internal static class SkillVersionMap;
