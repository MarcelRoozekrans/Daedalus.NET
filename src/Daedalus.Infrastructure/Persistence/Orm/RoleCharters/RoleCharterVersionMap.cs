using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.RoleCharters;

/// <summary>
///     Maps <see cref="RoleCharterVersion"/> to <c>"RoleCharterVersions"</c>. Every column carries its property's
///     name and none is special; <see cref="RoleCharterVersion.Skills"/> is the <c>text[]</c> column.
/// </summary>
[TableMap(typeof(RoleCharterVersion), "RoleCharterVersions")]
internal static class RoleCharterVersionMap;
