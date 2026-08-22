using Daedalus.Agents.Channels;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Channels;
using Thalos.Channels.Telegram;
using ZeroAlloc.Outbox;

namespace Daedalus.Tests.Unit.Application.Channels;

/// <summary>
///     Unit tests for <see cref="DaedalusChannelsServiceCollectionExtensions.AddDaedalusChannels"/>: it must resolve
///     <see cref="IConversationMap"/> to <see cref="PostgresConversationMap"/> (not Thalos's in-memory default),
///     leave the Telegram channel entirely unregistered when no bot token is configured, register it when one is,
///     never double-register the pump/dispatcher/adapters across repeated calls, and replace ZeroAlloc.Outbox's
///     throwing default dispatcher with <see cref="ChannelMessageQueuedDispatcher"/> regardless of call order.
/// </summary>
public sealed class DaedalusChannelsRegistrationTests
{
    // A realistic, non-default value throughout: matches Task 5's own dispatcher tests, and reads like a genuine
    // Telegram chat id rather than 0/empty/"test".
    private const string TelegramChatId = "482910337";

    private static IConfiguration Config(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Non-blank and non-default: ChannelOptions.Describe rejects a blank DefaultAgent.
            ["Thalos:Channels:DefaultAgent"] = "daedalus-assistant",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration TelegramConfig() => Config(
        ("Thalos:Channels:Telegram:BotToken", "123456789:AAHhqTGEiQaG3ZFOtVzO9fWlj-fake-token"),
        ("Thalos:Channels:Telegram:PrincipalId", "telegram:marcel"),
        ("Thalos:Channels:Telegram:AllowedUserIds:0", TelegramChatId));

    private static ServiceProvider Build(IConfiguration configuration, bool includeConsoleChannel = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusChannels(configuration, includeConsoleChannel);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Conversation_map_resolves_to_the_postgres_implementation_not_the_in_memory_default()
    {
        using var sp = Build(Config());

        // The concrete type, not the interface: resolving IConversationMap alone would pass whether UseConversationMap
        // ran or not, since Thalos always registers something behind the interface.
        sp.GetRequiredService<IConversationMap>().Should().BeOfType<PostgresConversationMap>();
        sp.GetRequiredService<IConversationMap>().Should().NotBeOfType<InMemoryConversationMap>();
    }

    [Fact]
    public void Telegram_is_absent_when_no_bot_token_is_configured()
    {
        using var sp = Build(Config());

        sp.GetServices<IChannelSource>().Should().BeEmpty("no console channel was requested and Telegram has no BotToken");
        sp.GetServices<IChannelAdapter>().Should().BeEmpty("no console channel was requested and Telegram has no BotToken");
    }

    [Fact]
    public void Telegram_is_absent_when_the_telegram_section_exists_but_the_bot_token_is_blank()
    {
        // A section can exist with other keys set (e.g. PrincipalId) while BotToken itself is still blank - that
        // must still count as "not configured", not trigger AddTelegramChannel and its OptionsValidationException.
        using var sp = Build(Config(("Thalos:Channels:Telegram:PrincipalId", "telegram:marcel")));

        sp.GetServices<IChannelSource>().Should().BeEmpty();
        sp.GetServices<IChannelAdapter>().Should().BeEmpty();
    }

    [Fact]
    public void Telegram_is_registered_when_a_bot_token_is_configured()
    {
        // The positive case, so the absence tests above are proven by a real gate and not by an IsTelegramConfigured
        // check that always returns false.
        using var sp = Build(TelegramConfig());

        sp.GetServices<IChannelSource>().Should().ContainSingle(s => s is TelegramChannelSource);
        sp.GetServices<IChannelAdapter>().Should().ContainSingle(a => a is TelegramChannelAdapter);
    }

    [Fact]
    public void Console_channel_is_absent_by_default()
    {
        // The API host has no TTY: AddDaedalusChannels must not read stdin unless explicitly asked to.
        using var sp = Build(Config());

        sp.GetServices<IChannelSource>().Should().NotContain(s => s.GetType().Name == "ConsoleChannelSource");
        sp.GetServices<IChannelAdapter>().Should().NotContain(a => a.GetType().Name == "ConsoleChannelAdapter");
    }

    [Fact]
    public void Console_channel_is_present_when_requested()
    {
        using var sp = Build(Config(), includeConsoleChannel: true);

        sp.GetServices<IChannelSource>().Should().ContainSingle(s => s.GetType().Name == "ConsoleChannelSource");
        sp.GetServices<IChannelAdapter>().Should().ContainSingle(a => a.GetType().Name == "ConsoleChannelAdapter");
    }

    [Fact]
    public void Calling_AddDaedalusChannels_twice_does_not_double_register_the_pump_dispatcher_or_telegram_channel()
    {
        // Descriptor counts, not resolved instances: TryAddEnumerable/Replace can leave exactly one resolvable
        // instance even when a bug left two ServiceDescriptors behind (e.g. two identical singletons), so counting
        // resolved objects can hide a duplicate registration that counting descriptors would not.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        var config = TelegramConfig();

        services.AddDaedalusChannels(config, includeConsoleChannel: true);
        services.AddDaedalusChannels(config, includeConsoleChannel: true);

        services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ChannelPump))
            .Should().Be(1, "the pump must not be double-registered");
        services.Count(d => d.ServiceType == typeof(IChannelSource))
            .Should().Be(2, "exactly one console source and one Telegram source, not two of each");
        services.Count(d => d.ServiceType == typeof(IChannelAdapter))
            .Should().Be(2, "exactly one console adapter and one Telegram adapter, not two of each");
        services.Count(d => d.ServiceType == typeof(IOutboxDispatcher<ChannelMessageQueued>))
            .Should().Be(1, "Replace must leave exactly one dispatcher registration, not accumulate one per call");
    }

    [Fact]
    public void Outbox_dispatcher_resolves_to_the_channel_dispatcher_when_the_default_was_registered_first()
    {
        // Mirrors the real host order: AddDaedalusAgents (which calls AddChannelOutbox, registering the throwing
        // DefaultOutboxDispatcher via TryAdd) runs before AddDaedalusChannels is added on top.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddOutbox().AddChannelMessageQueuedOutbox();

        services.AddDaedalusChannels(Config());
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IOutboxDispatcher<ChannelMessageQueued>>().Should().BeOfType<ChannelMessageQueuedDispatcher>();
    }

    [Fact]
    public void Outbox_dispatcher_resolves_to_the_channel_dispatcher_when_the_default_is_registered_afterwards()
    {
        // The reverse order: proves Replace, not TryAdd, is what makes this order-independent - a TryAdd here would
        // have already lost to nothing (nothing registered yet) and let the *later* AddChannelMessageQueuedOutbox's
        // TryAdd install the throwing default unopposed.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());

        services.AddDaedalusChannels(Config());
        services.AddOutbox().AddChannelMessageQueuedOutbox();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IOutboxDispatcher<ChannelMessageQueued>>().Should().BeOfType<ChannelMessageQueuedDispatcher>();
    }
}
