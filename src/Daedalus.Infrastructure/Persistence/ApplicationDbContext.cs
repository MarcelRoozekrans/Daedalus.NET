using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox.EfCore;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     EF Core DbContext for Daedalus persistence.
///     Only the ZeroAlloc.Outbox table is left in the model; every Daedalus table is read and written through
///     ZeroAlloc.ORM, and Task 22 removes this context.
/// </summary>
[method: RequiresUnreferencedCode("EF Core model building; removed with EF Core in milestone 3 phase 3.5")]
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    /// <summary>The ZeroAlloc.Outbox durable-delivery table (<c>OutboxMessages</c>); see <see cref="OutboxDbContextExtensions.AddOutboxMessages"/>.</summary>
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddOutboxMessages();
    }
}
