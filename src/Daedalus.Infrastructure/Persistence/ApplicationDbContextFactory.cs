using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Factory for design-time DbContext creation (EF Core migrations).
/// </summary>
public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseNpgsql(DatabaseSettings.GetDefaultConnectionString());

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
