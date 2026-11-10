using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.Brainstorms;

/// <summary>
///     Maps <see cref="BrainstormMessage"/> to <c>"BrainstormMessages"</c>. Every column carries its property's name, and
///     <see cref="BrainstormMessage.Role"/> and <see cref="BrainstormMessage.Phase"/> are stored as their integers, as
///     EF Core stored them.
/// </summary>
[TableMap(typeof(BrainstormMessage), "BrainstormMessages")]
internal static class BrainstormMessageMap;
