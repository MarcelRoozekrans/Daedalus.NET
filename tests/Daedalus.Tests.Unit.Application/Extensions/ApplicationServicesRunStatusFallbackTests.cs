using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.DeleteTask;
using Daedalus.Application.Commands.UpdateTask;
using Daedalus.Application.Extensions;
using Daedalus.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Application.Extensions;

/// <summary>
///     The Console host calls <c>AddApplicationServices</c> but never <c>AddDaedalusAgents</c>, so the handlers that read
///     a run's status must resolve there too.
/// </summary>
public sealed class ApplicationServicesRunStatusFallbackTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITaskRepository>());
        services.AddApplicationServices(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Red: remove the <c>TryAddSingleton</c> in <c>AddApplicationServices</c>; resolving either handler throws.</summary>
    [Fact]
    public void The_update_and_delete_handlers_resolve_without_the_workflow_engine()
    {
        using var sp = Build();
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<UpdateTaskCommandHandler>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<DeleteTaskCommandHandler>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IWorkflowRunStatusReader>().Should().BeOfType<DisabledWorkflowRunStatusReader>();
    }
}
