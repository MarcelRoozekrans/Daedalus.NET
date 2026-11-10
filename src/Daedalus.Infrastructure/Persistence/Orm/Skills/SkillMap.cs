using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.Skills;

/// <summary>
///     Maps <see cref="Skill"/> to <c>"Skills"</c>. The primary key <see cref="Entity{TId}.Id"/> is the
///     <c>"Name"</c> column, and <see cref="Skill.Tags"/>, a hand-written view over the private list, is the
///     <c>text[]</c> column <c>"Tags"</c>; every other column carries its property's name.
/// </summary>
[TableMap(typeof(Skill), "Skills")]
[ColumnMap(nameof(Skill.Id), Column = "Name")]
[ColumnMap(nameof(Skill.Tags))]
internal static class SkillMap;
