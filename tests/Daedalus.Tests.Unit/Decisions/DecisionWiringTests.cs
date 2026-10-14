using Daedalus.Agents;
using Daedalus.Agents.Decisions;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Minos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Decisions;

/// <summary>
///     Phase 2.10 task B5: the OpenRouter decision client is registered only when decisions are enabled, the decider
///     receives it, and the start-up log says why decisions are off without naming the key.
/// </summary>
public sealed class DecisionWiringTests
{
    private const string Key = "sk-or-test-key-0123456789";

    private static ServiceProvider Compose(bool zeroRetention, string? key)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Daedalus.Api.appsettings.json", optional: false);
        if (key is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["OPENROUTER_API_KEY"] = key });
        }

        var configuration = builder.Build();
        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        options.Decisions.ZeroDataRetentionAccount = zeroRetention;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusAgents(options, configuration, environment);
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Red: gate the registration on the confirmation alone, ignoring the key; the client is registered.
    /// </summary>
    [Fact]
    public void With_no_key_no_decision_client_is_registered_and_runs_are_unchanged()
    {
        using var provider = Compose(zeroRetention: true, key: null);

        provider.GetService<IDecisionClient>().Should().BeNull();
        provider.GetRequiredService<NodeSelectionDecider>().HasClient.Should().BeFalse();
    }

    /// <summary>Red: gate the registration on the key alone, ignoring the confirmation; the client is registered.</summary>
    [Fact]
    public void With_a_key_but_no_zero_retention_confirmation_decisions_stay_off()
    {
        using var provider = Compose(zeroRetention: false, key: Key);

        provider.GetService<IDecisionClient>().Should().BeNull();
        provider.GetRequiredService<NodeSelectionDecider>().HasClient.Should().BeFalse();
    }

    /// <summary>
    ///     Red: pass null to the decider's constructor in the registration; HasClient is false. Red: register no client
    ///     when enabled; the client is null.
    /// </summary>
    [Fact]
    public void With_a_key_and_the_confirmation_the_decider_holds_the_client()
    {
        using var provider = Compose(zeroRetention: true, key: Key);

        provider.GetService<IDecisionClient>().Should().NotBeNull();
        provider.GetRequiredService<NodeSelectionDecider>().HasClient.Should().BeTrue();
    }

    /// <summary>
    ///     Red: register DecisionsStartupLog as a plain singleton instead of a hosted service; no host starts it, and the
    ///     single-instance check fails. Red: return NoKey for the confirmation row of WhyOff; that row fails.
    /// </summary>
    [Theory]
    [InlineData(false, true, DecisionsStartupLog.NoZeroRetentionConfirmation)]
    [InlineData(true, false, DecisionsStartupLog.NoKey)]
    [InlineData(false, false, DecisionsStartupLog.NoConfirmationAndNoKey)]
    public async Task The_start_up_log_names_why_decisions_are_off(bool zeroRetention, bool keyPresent, string reason)
    {
        await using var provider = Compose(zeroRetention, keyPresent ? Key : null);
        var log = new CapturingLoggerFactory();
        provider.GetServices<IHostedService>().OfType<DecisionsStartupLog>().Should().ContainSingle();

        await new DecisionsStartupLog(new DecisionsConfig { ZeroDataRetentionAccount = zeroRetention }, keyPresent, log.CreateLogger<DecisionsStartupLog>())
            .StartAsync(CancellationToken.None);

        log.Messages.Should().ContainSingle().Which.Should().Contain(reason).And.Contain("off");
    }

    /// <summary>Red: add sk-or, or the key's length of 25, to the on or off message; the matching check fails.</summary>
    [Fact]
    public async Task No_start_up_line_contains_the_key_or_its_length()
    {
        var log = new CapturingLoggerFactory();
        foreach (var (zero, present) in new[] { (true, true), (false, true), (true, false), (false, false) })
        {
            await new DecisionsStartupLog(new DecisionsConfig { ZeroDataRetentionAccount = zero }, present, log.CreateLogger<DecisionsStartupLog>())
                .StartAsync(CancellationToken.None);
        }

        log.Messages.Should().HaveCount(4);
        log.Messages.Should().NotContain(m => m.Contains(Key, StringComparison.Ordinal) || m.Contains("sk-or", StringComparison.Ordinal)
            || m.Contains(Key.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    /// <summary>When decisions are on, the line says so and names the model. Red: log the off line in both branches.</summary>
    [Fact]
    public async Task The_start_up_log_says_when_decisions_are_on()
    {
        var log = new CapturingLoggerFactory();

        await new DecisionsStartupLog(new DecisionsConfig { ZeroDataRetentionAccount = true }, keyPresent: true, log.CreateLogger<DecisionsStartupLog>())
            .StartAsync(CancellationToken.None);

        log.Messages.Should().ContainSingle().Which.Should().Contain("are on").And.Contain("jev-latest");
    }

    /// <summary>
    ///     Minos's real ActivitySource and Meter name is what the hosts subscribe to: a decision through the registered
    ///     OpenRouter client emits a Minos span and instrument, and every source and meter named Minos is in <c>AspireExtensions.TracedSources</c> and <c>Meters</c>. A fault handler stands in for OpenRouter,
    ///     so no network is used and the call fails, which still emits.
    ///     Red: write <c>"Minos.NET"</c> in place of <c>"Minos"</c> in <c>TracedSources</c> or in <c>Meters</c>; the name Minos
    ///     emits is then not subscribed. Red: drop <c>"Minos"</c> from either; the same.
    /// </summary>
    [Fact]
    public async Task Minos_emits_its_spans_and_metrics_on_names_the_hosts_subscribe_to()
    {
        var spanSources = new System.Collections.Concurrent.ConcurrentBag<string>();
        var meterNames = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var activities = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => spanSources.Add(a.Source.Name),
        };
        System.Diagnostics.ActivitySource.AddActivityListener(activities);
        using var meters = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                meterNames.Add(instrument.Meter.Name);
                listener.EnableMeasurementEvents(instrument);
            },
        };
        meters.Start();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSystemOneDecisionClient(o =>
        {
            o.Provider = Minos.SystemOne.SystemOneProvider.OpenRouter;
            o.ApiKey = Key;
            o.MaxRetries = 0;
        });
        services.ConfigureAll<Microsoft.Extensions.Http.HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FaultHandler()));
        await using var provider = services.BuildServiceProvider();
        var decider = new NodeSelectionDecider(
            provider.GetRequiredService<IDecisionClient>(), new DecisionsConfig(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NodeSelectionDecider>.Instance);
        var menu = new RoleMenu("default-model", ["cheap", "strong"], []);

        await decider.DecideAsync(new NodeSelectionContext("developer", "implement", "do the thing", 1, menu, NarrowTools: true), CancellationToken.None);

        var traced = Subscribed("TracedSources");
        var metered = Subscribed("Meters");
        // ZeroAlloc.Rest, the HTTP layer under the client, also emits; it is not Minos's, and no host subscribes to it.
        bool IsMinos(string name) => name.StartsWith("Minos", StringComparison.Ordinal);
        var minosSources = spanSources.Where(IsMinos).Distinct(StringComparer.Ordinal).ToList();
        var minosMeters = meterNames.Where(IsMinos).Distinct(StringComparer.Ordinal).ToList();
        minosSources.Should().NotBeEmpty("Minos emits a span for the call");
        minosSources.Should().OnlyContain(n => traced.Contains(n, StringComparer.Ordinal));
        minosMeters.Should().NotBeEmpty("Minos publishes its instruments");
        minosMeters.Should().OnlyContain(n => metered.Contains(n, StringComparer.Ordinal));
    }

    private static string[] Subscribed(string field) => (string[])typeof(Daedalus.ServiceDefaults.AspireExtensions)
        .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

    private sealed class FaultHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(Messages);

        public ILogger<T> CreateLogger<T>() => new CaptureOf<T>(Messages);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private class Capture(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
        }

        private sealed class CaptureOf<T>(List<string> messages) : Capture(messages), ILogger<T>;
    }
}
