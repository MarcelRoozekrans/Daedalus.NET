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
///     <c>Thalos:Channels:DefaultAgent</c> or a <c>RepoDigestPrompts.AgentNames</c> entry names no configured agent
///     (spec §7) rather than deferring the failure to the first message a channel needs to route
///     (<c>ChannelPump</c> resolves the name at that point, too late for an operator to catch before traffic
///     arrives) or the first time a <c>RunScoutStep</c>/<c>RunWriterStep</c> dispatcher resolves a workflow agent.
/// </summary>
/// <remarks>
///     <c>DefaultAgent</c> and <c>RepoDigestPrompts.AgentNames</c> are both checked, in one pass, every boot —
///     there are no sagas in this phase (<c>ZeroAlloc.Saga</c> was dropped as undriveable — see
///     <c>ScheduleReconciler.KnownTriggers</c>'s remarks), so workflow agent names come from the one workflow
///     that exists (<c>RepoDigest</c>) rather than from saga configuration. <see cref="AgentNameValidator.Validate"/>
///     takes <c>IEnumerable&lt;string&gt;</c> rather than a single name precisely so both sources are reported
///     together, not one restart per source.
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
    public async Task A_host_configured_with_an_unknown_workflow_agent_name_fails_to_start()
    {
        // RepoDigestPrompts.AgentNames ("scout", "writer") is checked unconditionally, independent of
        // Thalos:Channels:DefaultAgent — a misconfigured RepoDigest workflow agent must fail the boot rather
        // than surface the first time RunScoutStepDispatcher tries to resolve it at 07:00. Index 1 in
        // Daedalus.Api.appsettings.json's Agents array is "scout"; renaming it simulates the catalog entry
        // going missing (a typo, a dropped entry) without touching DefaultAgent at index 0.
        using var host = BuildHost(("Thalos:Agents:1:Name", "Scoot"));

        var act = async () => await host.StartAsync();

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("scout");
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
            // Same reason ApiWebApplicationFactory sets this: PostgresFixture's EnsureCreatedAsync builds only the
            // EF Core model, never Thalos.NET.Workflow.Orm's raw-SQL tables. This test is about agent-name
            // validation, not the workflow engine, but processes/manufacture.yaml (Task 11) is now a real file on
            // Thalos:Workflow's ProcessesRoot and flows into this host's output the same way skills/*.SKILL.md
            // does - left enabled, ProcessDefinitionSyncHostedService.StartAsync has something to sync, and unlike
            // the periodic workflow services its failure is not caught, so the host would fail to start on 42P01
            // rather than degrade.
            ["Thalos:Workflow:Enabled"] = "false",
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
