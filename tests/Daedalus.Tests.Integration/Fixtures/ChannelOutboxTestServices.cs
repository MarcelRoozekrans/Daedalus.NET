using Daedalus.Agents.Channels;
using Daedalus.Infrastructure.Persistence.Orm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     The channel outbox pipeline as the hosts register it, <see cref="ChannelOutboxServiceCollectionExtensions.AddChannelOutbox"/>,
///     over the fixture database, for tests that build their own provider.
/// </summary>
internal static class ChannelOutboxTestServices
{
    /// <summary>
    ///     Registers the fixture's data source and <see cref="DaedalusDb"/>, then the production channel pipeline. A
    ///     test's <paramref name="configure"/> runs after the production options, so it overrides only what it sets.
    /// </summary>
    public static IServiceCollection AddChannelOutboxOver(
        this IServiceCollection services, PostgresFixture fixture, Action<OutboxOptions>? configure = null)
    {
        services.TryAddSingleton(fixture.DataSource);
        services.TryAddSingleton(fixture.Db);
        services.AddChannelOutbox();
        if (configure is not null)
        {
            services.Configure(ChannelOutboxServiceCollectionExtensions.PipelineName, configure);
        }

        return services;
    }

    /// <summary>
    ///     Replaces the channel pipeline's keyed writer for <typeparamref name="T"/> with what <paramref name="wrap"/>
    ///     makes of the generated one, which <paramref name="create"/> builds on the pipeline's keyed store.
    /// </summary>
    public static IServiceCollection WrapChannelWriter<T>(
        this IServiceCollection services,
        Func<IOutboxStore, IOutboxSerializer, IOutboxWriter<T>> create,
        Func<IOutboxWriter<T>, IServiceProvider, IOutboxWriter<T>> wrap)
        where T : notnull
    {
        services.AddKeyedTransient(
            ChannelOutboxServiceCollectionExtensions.PipelineName,
            (sp, key) => wrap(
                create(sp.GetRequiredKeyedService<IOutboxStore>(key), sp.GetRequiredService<IOutboxSerializer>()),
                sp));
        return services;
    }

    /// <summary>The channel pipeline's writer for <typeparamref name="T"/>.</summary>
    public static IOutboxWriter<T> ChannelWriter<T>(this IServiceProvider services)
        where T : notnull =>
        services.GetRequiredKeyedService<IOutboxWriter<T>>(ChannelOutboxServiceCollectionExtensions.PipelineName);

    /// <summary>The channel pipeline's worker, the only <see cref="OutboxWorkerService"/> the pipeline registers.</summary>
    public static OutboxWorkerService ChannelWorker(this IServiceProvider services) =>
        services.GetServices<IHostedService>().OfType<OutboxWorkerService>().Single();
}
