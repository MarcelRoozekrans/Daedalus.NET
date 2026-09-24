using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Skills.Charters;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Shared plumbing for tests that need the real <c>AddDaedalusAgents</c> composition root with its chartered
///     agents (<c>implementer</c>/<c>reviewer</c>) actually composed - <see cref="CharteredAgentCatalog"/> serves
///     only config agents until <c>CharterSyncService.SyncAsync</c> has run at least once, so a test that expects
///     to find a chartered agent in <see cref="IAgentCatalog"/> has to run the sync itself; nothing here starts
///     the host, so no <see cref="Microsoft.Extensions.Hosting.IHostedService"/> runs on its own.
/// </summary>
/// <remarks>
///     Used by <see cref="SquadConfigurationDriftTests"/> and <see cref="CharteredRoleCompositionTests"/>. Both
///     build from the real, shipped <c>Daedalus.Api.appsettings.json</c> and the real <c>roles/</c> files copied
///     to the test output (never a hand-built <c>DaedalusAgentsOptions</c> or charter) - see the Global
///     Constraints' "Build hosts from shipped config" rule.
/// </remarks>
internal static class ChartersTestSupport
{
    internal const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    /// <summary>Builds a <see cref="ServiceProvider"/> over the shipped API configuration and syncs its charters into an in-memory store.</summary>
    /// <param name="squadEnabledOverride">When set, overrides <c>Thalos:Squad:Enabled</c> for this build only.</param>
    internal static async Task<ServiceProvider> BuildWithApiConfigurationAndSyncedChartersAsync(bool? squadEnabledOverride = null)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ApiAppSettingsFileName, optional: false);
        if (squadEnabledOverride is { } enabled)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Thalos:Squad:Enabled"] = enabled ? "true" : "false",
            });
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusAgents(builder.Build(), environment);

        // The real registration wires PostgresRoleCharterStore (UseRoleCharterStore<PostgresRoleCharterStore>).
        // This test exercises real sync/composition, not Postgres - that has its own contract-test subclass in
        // Integration - so swap the store back to the in-memory one Thalos registers by default.
        services.Replace(ServiceDescriptor.Singleton<IRoleCharterStore>(sp => sp.GetRequiredService<InMemoryRoleCharterStore>()));

        var sp = services.BuildServiceProvider();

        // Nothing here calls IHost.StartAsync, so CharterSyncService.StartingAsync never runs on its own -
        // resolve it from the IHostedService registration Thalos adds and drive the sync directly.
        var sync = sp.GetServices<IHostedService>().OfType<CharterSyncService>().Single();
        var result = await sync.SyncAsync(CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : null);

        return sp;
    }
}
