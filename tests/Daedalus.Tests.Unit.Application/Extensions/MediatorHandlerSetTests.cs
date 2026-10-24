using System.Reflection;
using Daedalus.Application.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;

namespace Daedalus.Tests.Unit.Application.Extensions;

/// <summary>
///     The Mediator handlers <see cref="ApplicationServiceExtensions.AddApplicationServices"/> registers, against the
///     reflective scan of the retired <c>RegisterHandlersAot</c>. Phase 3.2 replaced the scan with the generated
///     <c>AddMediator(ServiceLifetime.Scoped)</c>; <see cref="OldScan"/> stays here as the oracle, so the generated
///     registration must give the same handlers by the same service type, implementation and lifetime.
/// </summary>
public sealed class MediatorHandlerSetTests
{
    /// <summary>The handlers in Daedalus.Application today, captured from <see cref="OldScan"/>.</summary>
    private static readonly string[] CapturedHandlers =
    [
        "Daedalus.Application.Commands.ConvertPrdToTasks.ConvertPrdToTasksCommandHandler",
        "Daedalus.Application.Commands.CreateProject.CreateProjectCommandHandler",
        "Daedalus.Application.Commands.CreateTask.CreateTaskCommandHandler",
        "Daedalus.Application.Commands.DeleteProject.DeleteProjectCommandHandler",
        "Daedalus.Application.Commands.DeleteTask.DeleteTaskCommandHandler",
        "Daedalus.Application.Commands.GeneratePrd.GeneratePrdCommandHandler",
        "Daedalus.Application.Commands.UpdateProject.UpdateProjectCommandHandler",
        "Daedalus.Application.Commands.UpdateTask.UpdateTaskCommandHandler",
    ];

    /// <summary>
    ///     <c>RegisterHandlersAot</c>'s predicate, copied verbatim: every non-abstract class in <paramref name="assembly"/>
    ///     that implements <see cref="IRequestHandler{TRequest, TResponse}"/>, registered by its own type as scoped.
    /// </summary>
    public static IReadOnlyList<(Type Service, Type Impl, ServiceLifetime Lifetime)> OldScan(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var handlers = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .ToList();

        var requestHandlerType = typeof(IRequestHandler<,>);
        var registered = new List<(Type, Type, ServiceLifetime)>();
        foreach (var handler in handlers)
        {
            var isRequestHandler = false;
            foreach (var iface in handler.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == requestHandlerType)
                {
                    isRequestHandler = true;
                    break;
                }
            }

            if (isRequestHandler)
            {
                registered.Add((handler, handler, ServiceLifetime.Scoped));
            }
        }

        return registered;
    }

    /// <summary>
    ///     The scan finds the captured handlers, so the oracle cannot go empty and pass the comparison below vacuously.
    ///     Red: add <c>&amp;&amp; !t.Name.Contains("Project", StringComparison.Ordinal)</c> to the class filter in
    ///     <see cref="OldScan"/>; the three project handlers drop out.
    /// </summary>
    [Fact]
    public void The_scan_finds_the_captured_handlers()
    {
        var scanned = OldScan(typeof(ApplicationServiceExtensions).Assembly);

        scanned.Select(h => h.Service.FullName).Should().BeEquivalentTo(CapturedHandlers);
    }

    /// <summary>
    ///     Every registration of a handler type, by service type, implementation and lifetime, equals the scan's. Red:
    ///     call <c>services.AddMediator(ServiceLifetime.Transient)</c> in <c>AddApplicationServices</c>; every lifetime
    ///     differs.
    /// </summary>
    [Fact]
    public void Add_application_services_registers_exactly_the_scanned_handlers()
    {
        var services = new ServiceCollection();
        services.AddApplicationServices(new ConfigurationBuilder().Build());

        var handlerTypes = OldScan(typeof(ApplicationServiceExtensions).Assembly).Select(h => h.Service).ToHashSet();
        var registered = services
            .Where(d => handlerTypes.Contains(d.ServiceType) || (d.ImplementationType is { } impl && handlerTypes.Contains(impl)))
            .Select(d => (d.ServiceType, d.ImplementationType, d.Lifetime))
            .ToList();

        registered.Should().BeEquivalentTo(
            OldScan(typeof(ApplicationServiceExtensions).Assembly).Select(h => (h.Service, (Type?)h.Impl, h.Lifetime)));
    }
}
