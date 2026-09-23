using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos.Memory;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Pins that the two decorators phase 2.3 task B5 depends on are actually applied by the real composition
///     root, not only constructible. Both wrap a service Thalos registers, and both are invisible from the
///     outside: an <see cref="IMemoryService"/> that is not wrapped answers every recall exactly as before and
///     simply records no tier, and an <see cref="IWorkflowReferenceResolver"/> that is not wrapped resolves
///     every name exactly as before and simply ignores <c>Thalos:Squad:Enabled</c>. Neither omission would fail
///     a build, a start-up, or any test that constructs the decorator itself.
/// </summary>
public sealed class WorkflowCompositionTests
{
    private static ServiceProvider BuildRealApiContainer()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Daedalus.Api.appsettings.json", optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusAgents(configuration, environment);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_memory_service_the_host_resolves_records_workflow_recall_tiers()
    {
        using var sp = BuildRealApiContainer();

        sp.GetRequiredService<IMemoryService>().Should().BeOfType<RecallTierRecordingMemoryService>(
            "MemoryContextProvider and the memory__* tools both resolve IMemoryService by interface, so an " +
            "unwrapped registration silently records no tier on any turn");
    }

    [Fact]
    public void The_workflow_reference_resolver_the_host_resolves_applies_the_squad_flag()
    {
        using var sp = BuildRealApiContainer();

        sp.GetRequiredService<IWorkflowReferenceResolver>().Should().BeOfType<SquadWorkflowReferenceResolver>(
            "without the wrapper Thalos:Squad:Enabled changes nothing at all, which is the state task B4 left " +
            "behind and wrote down");
    }

    /// <summary>
    ///     The decorator must not leave the inner instance resolvable by its own type as well: two live
    ///     <see cref="IMemoryService"/> instances would mean two dedupe and index paths over one store, and
    ///     whichever a caller happened to get would decide whether a tier was recorded.
    /// </summary>
    [Fact]
    public void Only_one_memory_service_is_registered()
    {
        using var sp = BuildRealApiContainer();

        sp.GetServices<IMemoryService>().Should().ContainSingle();
    }
}
