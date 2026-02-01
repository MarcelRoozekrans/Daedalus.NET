using System.Diagnostics.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Project = Daedalus.Domain.Entities.Project;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     EF Core DbContext for Ralph loop persistence.
///     Entity configurations are in the Configurations/ folder (IEntityTypeConfiguration pattern).
/// </summary>
[method:
    UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "DbContext used with full trimming mode; EF Core reflection suppression necessary")]
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Task> Tasks => Set<Task>();
    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();
    public DbSet<ExecutionSession> ExecutionSessions => Set<ExecutionSession>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<CodeAnalysisRequest> CodeAnalysisRequests => Set<CodeAnalysisRequest>();
    public DbSet<AnalysisIteration> AnalysisIterations => Set<AnalysisIteration>();
    public DbSet<StructuredLearningEntry> StructuredLearnings => Set<StructuredLearningEntry>();
    public DbSet<RepositoryConfiguration> RepositoryConfigurations => Set<RepositoryConfiguration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
