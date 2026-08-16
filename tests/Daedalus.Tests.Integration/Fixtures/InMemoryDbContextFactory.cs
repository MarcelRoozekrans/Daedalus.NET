using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Creates an EF InMemory-backed <see cref="ApplicationDbContext"/> for tests that don't need PostgreSQL semantics.
///     The production model maps <c>StructuredLearningEntry.Embedding</c> to pgvector's <c>Vector</c>, which the InMemory
///     provider cannot handle, so the property is ignored here (mirrors <c>BrainstormRepositoryTests</c> in Unit.Infrastructure).
///     Prefer <see cref="PostgresFixture"/> for anything touching SQL, transactions or concurrency tokens.
/// </summary>
internal static class InMemoryDbContextFactory
{
    /// <summary>Creates a context over a fresh, uniquely named in-memory database (caller disposes).</summary>
    public static ApplicationDbContext Create()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new InMemoryApplicationDbContext(options);
    }

    private sealed class InMemoryApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<StructuredLearningEntry>().Ignore(e => e.Embedding);
        }
    }
}
