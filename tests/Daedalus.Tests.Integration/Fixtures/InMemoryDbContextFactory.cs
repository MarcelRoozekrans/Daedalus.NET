using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Creates an EF InMemory-backed <see cref="ApplicationDbContext"/> for tests that don't need PostgreSQL semantics.
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

        return new ApplicationDbContext(options);
    }
}
