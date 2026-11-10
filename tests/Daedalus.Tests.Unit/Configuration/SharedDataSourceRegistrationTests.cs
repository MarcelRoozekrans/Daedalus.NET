using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Phase 3.5, ruling R4: the ZeroAlloc.ORM stores and the workflow poller share one <see cref="NpgsqlDataSource"/>.
///     <c>AddApplicationDatabase</c> and <c>AddDaedalusAgents</c> both register it with TryAdd, so the host ends up with
///     one registration whichever call runs first. The shipped Api configuration has the workflow engine on, so
///     <c>AddDaedalusAgents</c> takes its data-source branch here.
/// </summary>
public sealed class SharedDataSourceRegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task One_data_source_serves_the_stores_and_the_workflow_poller(bool databaseFirst)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Daedalus.Api.appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:daedalus"] = "Host=localhost;Database=daedalus;Username=test;Password=test",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        if (databaseFirst)
        {
            services.AddApplicationDatabase(configuration, "daedalus");
            services.AddDaedalusAgents(configuration, environment);
        }
        else
        {
            services.AddDaedalusAgents(configuration, environment);
            services.AddApplicationDatabase(configuration, "daedalus");
        }

        services.Where(d => d.ServiceType == typeof(NpgsqlDataSource)).Should().ContainSingle();
        services.Where(d => d.ServiceType == typeof(DaedalusDb)).Should().ContainSingle();
        await using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<DaedalusDb>().Should().NotBeNull();
    }
}
