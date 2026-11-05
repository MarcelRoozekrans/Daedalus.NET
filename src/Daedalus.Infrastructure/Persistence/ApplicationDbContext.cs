using System.Diagnostics.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox.EfCore;
using Project = Daedalus.Domain.Entities.Project;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     EF Core DbContext for Daedalus persistence.
///     Entity configurations are in the Configurations/ folder (IEntityTypeConfiguration pattern).
/// </summary>
[method: RequiresUnreferencedCode("EF Core model building; removed with EF Core in milestone 3 phase 3.5")]
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Task> Tasks => Set<Task>();
    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();
    public DbSet<ExecutionSession> ExecutionSessions => Set<ExecutionSession>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<CodeAnalysisRequest> CodeAnalysisRequests => Set<CodeAnalysisRequest>();
    public DbSet<AnalysisIteration> AnalysisIterations => Set<AnalysisIteration>();
    public DbSet<RepositoryConfiguration> RepositoryConfigurations => Set<RepositoryConfiguration>();
    public DbSet<BrainstormSession> BrainstormSessions => Set<BrainstormSession>();
    public DbSet<BrainstormMessage> BrainstormMessages => Set<BrainstormMessage>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();

    /// <summary>Recurring autonomous runs; see <see cref="ScheduledRun"/>.</summary>
    public DbSet<ScheduledRun> ScheduledRuns => Set<ScheduledRun>();

    /// <summary>One row per firing of a <see cref="ScheduledRun"/>; see <see cref="ScheduledRunExecution"/>.</summary>
    public DbSet<ScheduledRunExecution> ScheduledRunExecutions => Set<ScheduledRunExecution>();

    /// <summary>Insert-only log of what host code observed during a workflow run; see <see cref="WorkflowRunRecord"/>.</summary>
    public DbSet<WorkflowRunRecord> WorkflowRunRecords => Set<WorkflowRunRecord>();

    /// <summary>The ZeroAlloc.Outbox durable-delivery table (<c>OutboxMessages</c>); see <see cref="OutboxDbContextExtensions.AddOutboxMessages"/>.</summary>
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();

    /// <summary>
    ///     Every entity configuration, applied by <see cref="OnModelCreating"/>, alphabetical by entity. Explicit, because
    ///     an assembly scan is not trim-safe. <c>ApplicationDbContextModelTests</c> fails, naming the class, when a
    ///     configuration in the assembly is missing here.
    /// </summary>
    internal static readonly (Type Configuration, Action<ModelBuilder> Apply)[] Configurations =
    [
        Entry(new AgentSessionConfiguration()),
        Entry(new AnalysisIterationConfiguration()),
        Entry(new BrainstormMessageConfiguration()),
        Entry(new BrainstormSessionConfiguration()),
        Entry(new CodeAnalysisRequestConfiguration()),
        Entry(new ExecutionSessionConfiguration()),
        Entry(new ProjectConfiguration()),
        Entry(new RepositoryConfigurationEntityConfiguration()),
        Entry(new ScheduledRunConfiguration()),
        Entry(new ScheduledRunExecutionConfiguration()),
        Entry(new TaskConfiguration()),
        Entry(new TaskExecutionConfiguration()),
        Entry(new WorkflowRunRecordConfiguration()),
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
