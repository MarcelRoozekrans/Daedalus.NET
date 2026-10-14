using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Builds the scope factory that host code resolves <see cref="IWorkflowRunRecordStore"/> from, for a test that
///     constructs such a type by hand: <see cref="ReviewLensRunner"/>, <see cref="NodeSelectionRunner"/> and
///     <see cref="WorkflowRunGateway"/>.
/// </summary>
internal static class RecordStoreScopes
{
    /// <summary>
    ///     A scope factory whose scopes resolve <paramref name="records"/>, or a substitute that accepts every append
    ///     and lists nothing, registered as a scoped <see cref="IWorkflowRunRecordStore"/>, plus whatever
    ///     <paramref name="configure"/> registers, such as the agent catalog <see cref="NodeSelectionRunner"/> reads.
    /// </summary>
    public static IServiceScopeFactory For(IWorkflowRunRecordStore? records = null, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => records ?? Empty());
        configure?.Invoke(services);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static IWorkflowRunRecordStore Empty()
    {
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(Guid.Empty, default, default).ReturnsForAnyArgs(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]));
        return records;
    }
}
