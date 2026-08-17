using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration;

/// <summary>
///     Base class for Aspire-integrated integration tests.
///     Provides common setup for database context, repositories, and Aspire resources.
/// </summary>
public abstract class AspireIntegrationTestBase(AspirePostgresFixture fixture) : IAsyncLifetime
{
    protected readonly AspirePostgresFixture _aspireFixture = fixture;
    protected ApplicationDbContext _dbContext = null!;

    public virtual async Task InitializeAsync()
    {
        // Reset database using Respawn
        await _aspireFixture.DatabaseResetter.ResetAsync();

        var options = PostgresFixture.CreateDbContextOptions(_aspireFixture.ConnectionString);

        _dbContext = new ApplicationDbContext(options);

        // Create default project for tests
        var project = IntegrationTestFactory.CreateProject(IntegrationTestFactory.GetDefaultProjectId());
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();
    }

    public virtual async Task DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }
}
