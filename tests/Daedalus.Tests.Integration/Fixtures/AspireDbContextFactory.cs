using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Enhanced database context factory for Aspire integration tests.
///     Manages DbContext lifecycle with Aspire-managed resources.
/// </summary>
public sealed class AspireDbContextFactory(AspirePostgresFixture fixture) : IAsyncLifetime
{
    private ApplicationDbContext? _dbContext;

    public ApplicationDbContext DbContext => _dbContext
                                             ?? throw new InvalidOperationException(
                                                 "DbContext not initialized. Call InitializeAsync first.");

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        _dbContext = new ApplicationDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }
}
