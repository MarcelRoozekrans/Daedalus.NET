using System.Diagnostics.CodeAnalysis;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox.EfCore;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     EF Core DbContext for Daedalus persistence.
///     Entity configurations are in the Configurations/ folder (IEntityTypeConfiguration pattern).
/// </summary>
[method: RequiresUnreferencedCode("EF Core model building; removed with EF Core in milestone 3 phase 3.5")]
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    /// <summary>Recurring autonomous runs; see <see cref="ScheduledRun"/>.</summary>
    public DbSet<ScheduledRun> ScheduledRuns => Set<ScheduledRun>();

    /// <summary>One row per firing of a <see cref="ScheduledRun"/>; see <see cref="ScheduledRunExecution"/>.</summary>
    public DbSet<ScheduledRunExecution> ScheduledRunExecutions => Set<ScheduledRunExecution>();

    /// <summary>The ZeroAlloc.Outbox durable-delivery table (<c>OutboxMessages</c>); see <see cref="OutboxDbContextExtensions.AddOutboxMessages"/>.</summary>
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();

    /// <summary>
    ///     Every entity configuration, applied by <see cref="OnModelCreating"/>, alphabetical by entity. Explicit, because
    ///     an assembly scan is not trim-safe. <c>ApplicationDbContextModelTests</c> fails, naming the class, when a
    ///     configuration in the assembly is missing here.
    /// </summary>
    internal static readonly (Type Configuration, Action<ModelBuilder> Apply)[] Configurations =
    [
        Entry(new ScheduledRunConfiguration()),
        Entry(new ScheduledRunExecutionConfiguration()),
    ];

    private static (Type Configuration, Action<ModelBuilder> Apply) Entry<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors
            | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields
            | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.Interfaces)] TEntity>(
        IEntityTypeConfiguration<TEntity> configuration) where TEntity : class =>
        (configuration.GetType(), modelBuilder => modelBuilder.ApplyConfiguration(configuration));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        foreach (var (_, apply) in Configurations)
            apply(modelBuilder);

        modelBuilder.AddOutboxMessages();
    }
}
