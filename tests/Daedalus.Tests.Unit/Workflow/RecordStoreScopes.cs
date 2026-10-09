using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Builds the scope factory that host code resolves <see cref="IWorkflowRunRecordStore"/> from, for a test that
///     constructs such a type by hand: <see cref="ReviewLensRunner"/> and <see cref="WorkflowRunGateway"/>.
/// </summary>
internal static class RecordStoreScopes
{
    /// <summary>
    ///     A scope factory whose scopes resolve <paramref name="records"/>, or a substitute that accepts every append
    ///     and lists nothing, registered as a scoped <see cref="IWorkflowRunRecordStore"/>.
    /// </summary>
    public static IServiceScopeFactory For(IWorkflowRunRecordStore? records = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => records ?? Empty());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static IWorkflowRunRecordStore Empty()
    {
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(Guid.Empty, default, default).ReturnsForAnyArgs(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]));
        return records;
    }
}
