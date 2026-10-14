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

    /// <summary>A distinctive key for the leak checks: a recognisable fragment, and a length of 18.</summary>
    private const string SecretKey = "sk-or-zqx-7f3a91c2";

    private static ServiceProvider Compose(bool zeroRetention, string? key, ILoggerFactory? loggerFactory = null)
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
        if (loggerFactory is not null)
        {
            services.AddSingleton(loggerFactory);
        }

        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusAgents(options, configuration, environment);
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Red: gate the registration on the confirmation alone, ignoring the key; the client is registered.
    ///     Red: revert the key check to <c>IsNullOrEmpty</c> in <c>IsEnabled</c>; the blank key row registers a client, which Minos
    ///     would then refuse at host start.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void With_no_key_no_decision_client_is_registered_and_runs_are_unchanged(string? key)
    {
        using var provider = Compose(zeroRetention: true, key);

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
    ///     The registered start-up service, started from a provider that logs to a capture, says why decisions are off.
    ///     Red: register DecisionsStartupLog as a plain singleton instead of a hosted service; the single-instance check
    ///     fails. Red: return NoKey for the confirmation row of WhyOff; that row fails. Red: pass <c>true</c> for keyPresent
    ///     in the registration; the no-key rows log no reason. Red: build the log from a fresh <c>new DecisionsConfig</c> in
    ///     the registration; the confirmation rows read the wrong flag. Red: compute keyPresent with <c>IsNullOrEmpty</c>;
    ///     the blank row names no reason.
    /// </summary>
    [Theory]
    [InlineData(false, "sk-or-x", DecisionsStartupLog.NoZeroRetentionConfirmation)]
    [InlineData(true, null, DecisionsStartupLog.NoKey)]
    [InlineData(true, "   ", DecisionsStartupLog.NoKey)]
    [InlineData(false, null, DecisionsStartupLog.NoConfirmationAndNoKey)]
    public async Task The_start_up_log_names_why_decisions_are_off(bool zeroRetention, string? key, string reason)
    {
        var log = new CapturingLoggerFactory();
        await using var provider = Compose(zeroRetention, key, log);
        var hosted = provider.GetServices<IHostedService>().OfType<DecisionsStartupLog>().Should().ContainSingle().Subject;

        await hosted.StartAsync(CancellationToken.None);

        log.Messages.Should().ContainSingle(m => m.Contains("decisions are off", StringComparison.Ordinal)).Which.Should().Contain($"off: {reason}.");
    }

    /// <summary>
    ///     When decisions are on, the registered service says so and names the model. Red: log the off line in both
    ///     branches; the on line is missing.
    /// </summary>
    [Fact]
    public async Task The_start_up_log_says_when_decisions_are_on()
    {
        var log = new CapturingLoggerFactory();
        await using var provider = Compose(zeroRetention: true, Key, log);
        var hosted = provider.GetServices<IHostedService>().OfType<DecisionsStartupLog>().Should().ContainSingle().Subject;

        await hosted.StartAsync(CancellationToken.None);

        log.Messages.Should().ContainSingle(m => m.Contains("decisions are on", StringComparison.Ordinal))
            .Which.Should().Contain("jev-latest");
    }

    /// <summary>
    ///     No line the registered service logs, on or off, holds the key, a recognisable fragment of it or its length as a
    ///     number. Red: add the key to the on or the off message, or reword the message to drop "decisions are" and
    ///     add the key there; the key check fails, as it scans every captured line. Red: add <c>zqx</c> alone; the
    ///     fragment check fails. Red: add the key's length to either message; the length check fails.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_start_up_line_contains_the_key_a_fragment_of_it_or_its_length(bool zeroRetention)
    {
        var log = new CapturingLoggerFactory();
        await using var provider = Compose(zeroRetention, SecretKey, log);
        var hosted = provider.GetServices<IHostedService>().OfType<DecisionsStartupLog>().Should().ContainSingle().Subject;

        await hosted.StartAsync(CancellationToken.None);

        // Every captured line, not only the start-up line: a key in any other message of the composition fails too.
        var length = SecretKey.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        log.Messages.Should().NotContain(m => m.Contains(SecretKey, StringComparison.Ordinal), "the key is never logged");
        log.Messages.Should().NotContain(m => m.Contains("zqx", StringComparison.Ordinal), "no fragment of the key is logged");
        log.Messages.Should().NotContain(m => m.Contains(length, StringComparison.Ordinal), "the key's length is never logged");
        log.Messages.Should().ContainSingle(m => m.Contains("decisions are", StringComparison.Ordinal), "the registered service logged its line");
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

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Capture(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
        }
    }
}
