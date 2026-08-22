using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos;
using Thalos.Channels;
using Thalos.Channels.Telegram;
using ZeroAlloc.Outbox;

namespace Daedalus.Agents.Channels;

/// <summary>Composition root for Thalos.NET.Channels on the Daedalus host.</summary>
public static class DaedalusChannelsServiceCollectionExtensions
{
    /// <summary>
    ///     Enables Thalos channels for this host: <c>Thalos:Channels</c> options and the <see cref="ChannelPump"/>
    ///     hosted service (<see cref="ChannelThalosBuilderExtensions.UseChannels(ThalosBuilder,IConfiguration)"/>),
    ///     the <see cref="PostgresConversationMap"/> in place of Thalos's in-memory default
    ///     (<see cref="ChannelThalosBuilderExtensions.UseConversationMap{TMap}"/>), the Telegram channel
    ///     (<c>Thalos:Channels:Telegram</c>) when — and only when — it is actually configured, and the
    ///     <see cref="ChannelMessageQueuedDispatcher"/> in place of ZeroAlloc.Outbox's throwing
    ///     <c>DefaultOutboxDispatcher&lt;ChannelMessageQueued&gt;</c> fallback, so a message queued through the
    ///     outbox (see <see cref="ChannelOutboxServiceCollectionExtensions.AddChannelOutbox"/>) actually reaches an
    ///     <see cref="IChannelAdapter"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    ///     Host configuration; <c>Thalos:Channels</c> and — when a <c>BotToken</c> is present —
    ///     <c>Thalos:Channels:Telegram</c> are read.
    /// </param>
    /// <param name="includeConsoleChannel">
    ///     Adds Thalos's in-box console channel (real stdin/stdout) when <see langword="true"/>. Defaults to
    ///     <see langword="false"/> because this method is shared by every host, including the API, which has no TTY
    ///     to read from — a console channel registered there would leave a hosted service blocked on
    ///     <see cref="Console.In"/> for a stream nobody is ever going to write to. <c>Daedalus.Cli</c> is the one
    ///     caller expected to pass <see langword="true"/>. See the remarks for why this is a parameter rather than a
    ///     host-side <c>ThalosBuilder</c> call.
    /// </param>
    /// <remarks>
    ///     <para>
    ///     <b>The console channel is a parameter of this method, not a separate call the CLI host makes on its
    ///     own.</b> Three shapes were open: always call <see cref="ChannelThalosBuilderExtensions.AddConsoleChannel"/>
    ///     here (wrong — the API host would read stdin), never call it and leave it to each host (wrong — the CLI
    ///     host would need its own <c>services.AddThalos(t =&gt; t.AddConsoleChannel())</c>, duplicating knowledge of
    ///     <c>Thalos.Channels.Console</c> and the <c>ThalosBuilder</c> wiring this method already owns), or gate it
    ///     behind a parameter that defaults to off. The parameter keeps <see cref="AddDaedalusChannels"/> the single
    ///     composition point both hosts call — matching the brief's "one registration method a host calls" — while
    ///     the default protects the host that must never get it by omission.
    ///     </para>
    ///     <para>
    ///     <b>Telegram is registered only when a <c>BotToken</c> is configured.</b>
    ///     <see cref="TelegramThalosBuilderExtensions.AddTelegramChannel(ThalosBuilder,IConfiguration)"/> always
    ///     registers a <c>TelegramChannelSource</c>/<c>TelegramChannelAdapter</c> pair and calls
    ///     <c>ValidateOnStart</c> on <see cref="TelegramOptions"/> — whose validator rejects a blank
    ///     <see cref="TelegramOptions.BotToken"/>, blank <see cref="TelegramOptions.PrincipalId"/> and empty
    ///     <see cref="TelegramOptions.AllowedUserIds"/>. Calling it unconditionally on a host with no Telegram
    ///     configuration would therefore not silently no-op; it would take the host down at start with an
    ///     <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>. <see cref="IsTelegramConfigured"/>
    ///     reads <c>Thalos:Channels:Telegram:BotToken</c> directly off <paramref name="configuration"/> — the same
    ///     first field <see cref="TelegramOptions.Describe"/> checks — so a host with no Telegram section, or one
    ///     with the section present but no token, gets no Telegram channel at all rather than a channel that fails
    ///     validation.
    ///     </para>
    ///     <para>
    ///     <b>The dispatcher replacement works regardless of call order relative to the outbox durability layer.</b>
    ///     <c>AddChannelMessageQueuedOutbox()</c> (generated for <see cref="ChannelMessageQueued"/>, invoked by
    ///     <see cref="ChannelOutboxServiceCollectionExtensions.AddChannelOutbox"/>, which
    ///     <c>AddDaedalusAgents</c> already calls) registers
    ///     <c>IOutboxDispatcher&lt;ChannelMessageQueued&gt;</c> as ZeroAlloc.Outbox's
    ///     <c>DefaultOutboxDispatcher&lt;ChannelMessageQueued&gt;</c> via <c>TryAddTransient</c> — a dispatcher whose
    ///     only job is to throw "no dispatcher registered". A <c>TryAdd</c> here would only win if this method ran
    ///     <i>before</i> that registration; on the API host, where <c>AddDaedalusAgents</c> (and therefore the
    ///     outbox wiring) runs first and <c>AddDaedalusChannels</c> is added on top, a <c>TryAdd</c> would silently
    ///     lose and every queued message would dead-letter against the throwing default forever. This method calls
    ///     <see cref="ServiceCollectionDescriptorExtensions.Replace"/> instead, which unconditionally wins no matter
    ///     which of the two registrations ran first — verified both orderings in
    ///     <c>DaedalusChannelsRegistrationTests</c>.
    ///     </para>
    ///     <para>
    ///     <b>This method does not itself call <c>AddChannelOutbox</c>.</b> That is deliberate: <c>AddOutbox</c>
    ///     registers its poller with a plain <c>AddHostedService&lt;OutboxWorkerService&gt;</c> (not <c>TryAdd</c>),
    ///     so calling it a second time on a host that already ran <c>AddDaedalusAgents</c> (which already calls
    ///     <c>AddChannelOutbox</c>) would start two pollers racing the same table. Any host that calls this method
    ///     is expected to have wired the outbox durability layer separately — in practice, by calling
    ///     <c>AddDaedalusAgents</c> first.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddDaedalusChannels(
        this IServiceCollection services,
        IConfiguration configuration,
        bool includeConsoleChannel = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddThalos(thalos =>
        {
            thalos.UseChannels(configuration)
                .UseConversationMap<PostgresConversationMap>();

            if (includeConsoleChannel)
            {
                thalos.AddConsoleChannel();
            }

            if (IsTelegramConfigured(configuration))
            {
                thalos.AddTelegramChannel(configuration);
            }
            else
            {
                services.AddSingleton<IHostedService, TelegramNotConfiguredNotice>();
            }
        });

        services.Replace(ServiceDescriptor.Singleton<IOutboxDispatcher<ChannelMessageQueued>, ChannelMessageQueuedDispatcher>());

        return services;
    }

    /// <summary>
    ///     A runtime-only <see cref="TelegramOptions.Enabled"/> flag exists for hosts that want to bind Telegram
    ///     from late configuration, but that is a different question from whether the channel should be
    ///     <i>registered</i> at all. <see cref="TelegramOptions.BotToken"/> is the field this checks: it is the
    ///     first thing <see cref="TelegramOptions.Describe"/> validates, and its absence is the one condition that
    ///     unambiguously means "this host was never given a bot to run" rather than "temporarily switched off".
    /// </summary>
    private static bool IsTelegramConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[$"{TelegramOptions.SectionName}:{nameof(TelegramOptions.BotToken)}"]);
}

/// <summary>
///     Logs, once at host startup, that the Telegram channel was not registered because
///     <see cref="TelegramOptions.BotToken"/> is not configured (see
///     <see cref="DaedalusChannelsServiceCollectionExtensions"/>'s private <c>IsTelegramConfigured</c> check).
///     Registered only on that not-configured branch, so a host that never intended to run Telegram stays silent
///     while a deployer who <i>did</i> intend it — and simply forgot the token — gets an explicit signal instead
///     of the channel quietly never appearing.
/// </summary>
internal sealed partial class TelegramNotConfiguredNotice(ILogger<TelegramNotConfiguredNotice> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogTelegramNotConfigured(logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 431, Level = LogLevel.Information,
        Message = "Telegram channel not configured: Thalos:Channels:Telegram:BotToken is empty. Set a bot token to enable it.")]
    private static partial void LogTelegramNotConfigured(ILogger logger);
}
