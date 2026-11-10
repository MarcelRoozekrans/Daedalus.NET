using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.RoleCharters;

/// <summary>
///     Maps <see cref="RoleCharterHead"/> to <c>"RoleCharters"</c>, the table the EF mapping named after the
///     <c>DbSet</c>. Every column carries its property's name.
/// </summary>
[TableMap(typeof(RoleCharterHead), "RoleCharters")]
internal static class RoleCharterHeadMap;
