using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Daedalus.Api;

/// <summary>
///     Factory for design-time DbContext creation (EF Core migrations). It lives in Api, not Infrastructure, because it
///     is design time only: its call to the RequiresUnreferencedCode constructor of <see cref="ApplicationDbContext"/>
///     would need an annotation that <see cref="IDesignTimeDbContextFactory{TContext}"/> cannot carry. The documented
///     <c>dotnet ef</c> commands all use Api as the startup project.
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
