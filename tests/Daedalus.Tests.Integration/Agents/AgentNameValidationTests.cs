using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Covers <see cref="AgentNameValidator"/>: the pure aggregation logic (naming every unknown name at once) and
///     its wiring into <c>ScheduleReconcilerHostedService</c>, which stops a host at boot when
///     <c>Thalos:Channels:DefaultAgent</c> names no configured agent (spec §7) rather than deferring the failure to
///     the first message a channel needs to route (<c>ChannelPump</c> resolves the name at that point, too late for
///     an operator to catch before traffic arrives).
/// </summary>
/// <remarks>
///     Scope: <c>DefaultAgent</c> only. There are no sagas in this phase (<c>ZeroAlloc.Saga</c> was dropped as
///     undriveable — see <c>ScheduleReconciler.KnownTriggers</c>'s remarks) and no workflow-agent-name type exists
///     yet, so this suite does not — and cannot — cover a saga referencing an unknown agent name. That check moves
///     to whichever later task introduces workflow agent names; it extends <see cref="AgentNameValidator.Validate"/>
///     with more names in the same collection, which is why the method takes <c>IEnumerable&lt;string&gt;</c>
///     rather than a single name.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class AgentNameValidationTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_host_configured_with_an_unknown_DefaultAgent_fails_to_start()
    {
        using var host = BuildHost(("Thalos:Channels:DefaultAgent", "Nonexistent Agent"));

        var act = async () => await host.StartAsync();

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Nonexistent Agent");
    }

    [Fact]
    public async Task A_DefaultAgent_differing_only_in_case_from_the_catalog_entry_still_starts_the_host()
    {
        // Daedalus.Api.appsettings.json's catalog names "Daedalus Architect"; agent names are typed by humans on
        // phones, so ChannelPump resolves DefaultAgent case-insensitively (StringComparison.OrdinalIgnoreCase) and
        // AgentNameValidator must match that comparison, or a name a human typed correctly but differently cased
        // would fail this exact test by refusing to start the host.
        using var host = BuildHost(("Thalos:Channels:DefaultAgent", "daedalus architect"));

        var act = async () => await host.StartAsync();

        await act.Should().NotThrowAsync();
        await host.StopAsync();
    }

    [Fact]
    public void The_error_names_every_unknown_agent_at_once_not_just_the_first()
    {
        var catalog = new FakeAgentCatalog("Daedalus Architect");

        var act = () => AgentNameValidator.Validate(catalog, ["scout", "Daedalus Architect", "writer"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*scout*")
            .WithMessage("*writer*")
            .Which.Message.Should().NotContain(
                "Daedalus Architect", "the one name that does match a catalog entry must not be reported as unknown");
    }

    private IHost BuildHost(params (string Key, string Value)[] overrides)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.ContentRootPath = AppContext.BaseDirectory;
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.appsettings.json"), optional: false);

        var overrideValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:daedalus"] = fixture.ConnectionString,
        };
        foreach (var (key, value) in overrides)
        {
            overrideValues[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(overrideValues);

        builder.Services.AddPooledDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        builder.Services.AddDaedalusAgents(builder.Configuration, builder.Environment);
        return builder.Build();
    }

    /// <summary>A minimal <see cref="IAgentCatalog"/> test double naming exactly the agents given to its constructor.</summary>
    private sealed class FakeAgentCatalog : IAgentCatalog
    {
        public FakeAgentCatalog(params string[] names) =>
            Agents = [.. names.Select(name => new AgentDefinition { Id = AgentId.New(), Name = name, Instructions = "irrelevant" })];

        public IReadOnlyList<AgentDefinition> Agents { get; }

        public bool TryGet(AgentId id, out AgentDefinition definition)
        {
            var match = Agents.FirstOrDefault(a => a.Id == id);
            definition = match!;
            return match is not null;
        }
    }
}
