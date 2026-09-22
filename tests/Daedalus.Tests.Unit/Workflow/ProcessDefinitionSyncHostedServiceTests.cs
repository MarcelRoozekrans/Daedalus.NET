using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Guards the one thing standing between a partially-migrated database and a crash-looping API host:
///     <see cref="ProcessDefinitionSyncHostedService.StartAsync"/>'s catch.
/// </summary>
/// <remarks>
///     <para>
///     <see cref="ProcessDefinitionSync.SyncAsync"/> degrades per document and returns a
///     <see cref="Result{T}"/> — but only for documents that fail to load or validate. Nothing upholds that
///     contract below it: <c>OrmProcessDefinitionStore</c> has no catch anywhere, so a database that is up but
///     missing <c>process_definition</c> throws out of the store, out of <c>SyncAsync</c>, and out of
///     <c>StartAsync</c>, taking the whole host — every non-workflow endpoint included — down with it.
///     </para>
///     <para>
///     Falsifiability, per assertion: delete the <c>try</c>/<c>catch</c> in
///     <see cref="ProcessDefinitionSyncHostedService.StartAsync"/> and
///     <see cref="A_throwing_store_does_not_fail_host_startup"/> goes red on the escaping exception; narrow the
///     exception filter to let <see cref="OperationCanceledException"/> be swallowed and
///     <see cref="A_cancelled_startup_still_propagates_cancellation"/> goes red instead. Both were verified red
///     by making exactly those edits. The sync is driven through a real
///     <see cref="ProcessDefinitionSync"/> over a real YAML document rather than a substitute, so the store is
///     genuinely reached — a test whose source yielded nothing would never call the store at all and would pass
///     with the catch removed.
///     </para>
/// </remarks>
public sealed class ProcessDefinitionSyncHostedServiceTests
{
    private const string Yaml = """
        process: sync-hosted-service-test
        version: 1
        nodes:
          gate:
            await: human_approval
            next: finish
          finish:
            terminal: succeeded
        """;

    [Fact]
    public async Task A_throwing_store_does_not_fail_host_startup()
    {
        var boom = new InvalidOperationException("42P01: relation \"process_definition\" does not exist");
        var service = BuildService(ThrowingStore(boom));

        var start = async () => await service.StartAsync(CancellationToken.None);

        await start.Should().NotThrowAsync(
            "a store-side failure must degrade the workflow engine alone, never take down a host whose other "
            + "endpoints do not depend on it");
    }

    /// <summary>
    ///     The other half of the filter: shutdown cancellation during startup is not a sync failure and must keep
    ///     propagating, the same rule <c>WorkflowStrandedRunSweepService</c> and <c>WorkflowOutboxDispatchService</c>
    ///     document for their own loops.
    /// </summary>
    [Fact]
    public async Task A_cancelled_startup_still_propagates_cancellation()
    {
        var service = BuildService(ThrowingStore(new OperationCanceledException()));

        var start = async () => await service.StartAsync(CancellationToken.None);

        await start.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ProcessDefinitionSyncHostedService BuildService(IProcessDefinitionStore store)
    {
        var source = Substitute.For<IProcessDefinitionSource>();
        source.ReadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<ProcessDocument>>(
                new[] { new ProcessDocument("sync-hosted-service-test.yaml", Yaml) }));

        // The YAML above references no agent and no skill, so the resolver is never consulted — but
        // ProcessDefinitionSync's constructor null-checks it, so it gets a substitute rather than null.
        var sync = new ProcessDefinitionSync(source, store, Substitute.For<IWorkflowReferenceResolver>());

        return new ProcessDefinitionSyncHostedService(sync, NullLogger<ProcessDefinitionSyncHostedService>.Instance);
    }

    /// <summary>
    ///     Throws from every member: which one <see cref="ProcessDefinitionSync.SyncAsync"/> reaches first is an
    ///     implementation detail of a package this repository does not own, and pinning it here would make this
    ///     guard fail for the wrong reason on a Thalos upgrade.
    /// </summary>
    private static IProcessDefinitionStore ThrowingStore(Exception exception)
    {
        var store = Substitute.For<IProcessDefinitionStore>();
        store.UpsertAndActivateAsync(Arg.Any<ProcessDefinition>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(exception);
        store.GetActiveVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(exception);
        store.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Throws(exception);
        store.TryRemoveAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Throws(exception);
        return store;
    }
}
