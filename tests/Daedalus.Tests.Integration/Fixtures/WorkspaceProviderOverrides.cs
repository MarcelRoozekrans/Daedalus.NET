using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Overrides of the host's <see cref="IRunWorkspaceProvider"/> for a test that needs the provider to misbehave in
///     one way, passed as <c>configureServices</c> to <see cref="ScratchWorkflowHost.StartAsync"/>. The manufacture run
///     starter takes <see cref="IRunWorkspaceProvider"/> from the container when it is first resolved, and the factory
///     runs this callback after every registration of the host, so the last registration here is the one it gets.
/// </summary>
internal static class WorkspaceProviderOverrides
{
    /// <summary>
    ///     Replaces the provider with <paramref name="provider"/>, so every create, find, list and remove goes to it
    ///     instead of the host's git provider. The host registers its <see cref="IRunBaseFileReader"/> and its
    ///     <see cref="IRunWorkspaceHandoff"/> as casts of the provider it resolves, so <paramref name="provider"/> must
    ///     implement both too, such as
    ///     <c>Substitute.For&lt;IRunWorkspaceProvider, IRunBaseFileReader, IRunWorkspaceHandoff&gt;()</c>, or resolving
    ///     the manufacture run starter or the publish action fails with an <see cref="InvalidCastException"/>.
    /// </summary>
    public static Action<IServiceCollection> Replace(IRunWorkspaceProvider provider) => services =>
    {
        services.RemoveAll<IRunWorkspaceProvider>();
        services.AddSingleton(provider);
    };

    /// <summary>
    ///     Keeps the host's own provider and makes it, after every successful create, leave a directory link named
    ///     <paramref name="linkName"/> inside the new workspace that points at <paramref name="target"/>: a checkout that
    ///     holds a link out of itself, as a hostile repository's could. Base-file reads go to the host's own provider,
    ///     which is its reader, so the real reader meets the link; so do handoffs, to the provider that is its handoff.
    /// </summary>
    public static Action<IServiceCollection> LinkOutOfEveryWorkspace(string linkName, string target, ICollection<IDisposable> links) =>
        services =>
        {
            var original = services.Single(d => d.ServiceType == typeof(IRunWorkspaceProvider));
            services.Remove(original);
            services.AddSingleton<IRunWorkspaceProvider>(sp =>
            {
                IRunWorkspaceProvider inner = original switch
                {
                    { ImplementationInstance: IRunWorkspaceProvider instance } => instance,
                    { ImplementationFactory: { } factory } => (IRunWorkspaceProvider)factory(sp),
                    { ImplementationType: { } type } => (IRunWorkspaceProvider)ActivatorUtilities.CreateInstance(sp, type),
                    _ => throw new InvalidOperationException("The host registers its workspace provider in a way this override cannot wrap."),
                };
                return new LinkingProvider(inner, linkName, target, links);
            });
        };

    private sealed class LinkingProvider(IRunWorkspaceProvider inner, string linkName, string target, ICollection<IDisposable> links)
        : IRunWorkspaceProvider, IRunBaseFileReader, IRunWorkspaceHandoff
    {
        public async ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct)
        {
            var created = await inner.CreateAsync(request, ct);
            if (created.IsSuccess)
            {
                links.Add(DirectoryLink.Create(Path.Combine(created.Value.Root, linkName), target));
            }

            return created;
        }

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) => inner.FindAsync(runId, ct);

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) => inner.RemoveAsync(runId, ct);

        public ValueTask<Result<string?, AgentError>> ReadBaseFileAsync(Guid runId, string relativePath, CancellationToken ct) =>
            ((IRunBaseFileReader)inner).ReadBaseFileAsync(runId, relativePath, ct);

        public ValueTask<Result<RunWorkspace, AgentError>> CheckoutForPublishAsync(Guid runId, CancellationToken ct) =>
            ((IRunWorkspaceHandoff)inner).CheckoutForPublishAsync(runId, ct);
    }
}
