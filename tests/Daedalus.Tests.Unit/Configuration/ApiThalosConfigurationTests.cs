using AI.Sentinel;
using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Mcp;
using Thalos.Memory.RagNet;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Guards the shipped API configuration: <c>.mcp.json</c> flows into this test's output through the Daedalus.Api
///     project reference, and <c>appsettings.json</c> is linked explicitly as <c>Daedalus.Api.appsettings.json</c> (Console
///     and Web ship one too and would otherwise race for the plain name), so the file that is deployed is the file under test.
/// </summary>
public sealed class ApiThalosConfigurationTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private const string ConsoleAppSettingsFileName = "Daedalus.Console.appsettings.json";

    private static IConfiguration LoadApiConfiguration() => Load(ApiAppSettingsFileName);

    private static IConfiguration LoadConsoleConfiguration() => Load(ConsoleAppSettingsFileName);

    private static IConfiguration Load(string fileName) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: false)
            .Build();

    private static ServiceProvider BuildWithApiConfiguration()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusAgents(LoadApiConfiguration(), environment);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Appsettings_declares_the_daedalus_architect_agent_with_a_stable_ulid()
    {
        using var sp = BuildWithApiConfiguration();

        var agent = sp.GetRequiredService<IAgentCatalog>().Agents.Should().ContainSingle().Subject;

        // Sessions reference this id — changing it orphans them. Update the test only together with a data migration.
        agent.Id.Should().Be(AgentId.Parse("01M05YCM7DPKRG9X04870B2JYH", null));
        agent.Name.Should().Be("Daedalus Architect");
        agent.Tools.Should().Equal("roslyn__*", "daedalus__*", "memory__*", "context7__*");
        agent.Instructions.Should().Contain("roslyn__").And.Contain("daedalus__").And.Contain("memory__");
    }

    [Fact]
    public void Appsettings_binds_anthropic_defaults_tool_policies_and_sentinel_actions()
    {
        using var sp = BuildWithApiConfiguration();

        sp.GetRequiredService<IChatClientProvider>().DefaultModel.Should().Be("claude-sonnet-5");

        var policies = sp.GetRequiredService<IOptions<ThalosOptions>>().Value.ToolPolicies;
        policies.Select(p => (p.ToolPattern, p.PolicyName)).Should().Equal(("roslyn__apply_*", "developer"), ("roslyn__rename_*", "developer"));

        var sentinel = sp.GetRequiredService<SentinelOptions>();
        sentinel.OnCritical.Should().Be(SentinelAction.Quarantine);
        sentinel.OnHigh.Should().Be(SentinelAction.Alert);
        sentinel.OnMedium.Should().Be(SentinelAction.Log);
        sentinel.OnLow.Should().Be(SentinelAction.Log);
    }

    [Fact]
    public void Crash_recovery_hosted_service_is_registered()
    {
        using var sp = BuildWithApiConfiguration();

        sp.GetServices<IHostedService>().Should().ContainSingle(s => s.GetType().Name == "AgentSessionCrashRecovery");
    }

    [Fact]
    public void Api_host_owns_the_ragnet_schema_and_creates_it_on_start()
    {
        using var sp = BuildWithApiConfiguration();

        sp.GetRequiredService<RagNetMemoryOptions>().EnsureSchemaOnStartup.Should().BeTrue();
        sp.GetServices<IHostedService>().Should().ContainSingle(s => s.GetType().Name == "RagNetMemorySchemaInitializer");
    }

    /// <summary>
    ///     The console worker writes Ralph learnings into the same database as the API. If the two hosts disagreed on the
    ///     shared owner, Ralph would write memories nobody recalls; if they disagreed on the vector width, the second host
    ///     to touch <c>rag_chunks</c> would fail. Both files therefore declare the same block, pinned here.
    /// </summary>
    [Fact]
    public void Console_and_api_agree_on_the_shared_memory_settings()
    {
        var api = LoadApiConfiguration().GetSection(MemoryConfig.SectionName);
        var console = LoadConsoleConfiguration().GetSection(MemoryConfig.SectionName);

        console.Exists().Should().BeTrue("the Ralph worker binds Thalos:Memory through AddDaedalusMemory");
        foreach (var key in new[] { "SharedOwnerId", "VectorDimensions", "RalphRecall:TopK", "RalphRecall:MinScore" })
        {
            console[key].Should().Be(api[key], "Thalos:Memory:{0} must match between the API and console hosts", key);
        }
    }

    [Fact]
    public void Console_host_does_not_create_the_ragnet_schema()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusMemory(LoadConsoleConfiguration());
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<RagNetMemoryOptions>().EnsureSchemaOnStartup.Should().BeFalse(
            "the API host creates rag_chunks; concurrent CREATE from both hosts can fail on the pg catalog");
        sp.GetServices<IHostedService>().Should().NotContain(s => s.GetType().Name == "RagNetMemorySchemaInitializer");
    }

    /// <summary>
    ///     <c>UseRagNetMemory</c> is last-call-wins, so a host that called both registrations would silently take the
    ///     later <c>EnsureSchemaOnStartup</c> — on the API that means nobody creates <c>rag_chunks</c> and every memory
    ///     stays <c>index_pending</c> without anything failing. The registration refuses instead.
    /// </summary>
    [Fact]
    public void Registering_memory_twice_throws_instead_of_silently_disabling_schema_creation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusMemory(LoadConsoleConfiguration());

        var second = () => services.AddDaedalusMemory(LoadConsoleConfiguration());

        second.Should().Throw<InvalidOperationException>().WithMessage("*mutually exclusive*");
    }

    [Fact]
    public void Mcp_config_is_copied_next_to_the_api_and_parses()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ".mcp.json");
        File.Exists(path).Should().BeTrue(".mcp.json must be CopyToOutputDirectory=PreserveNewest in Daedalus.Api.csproj");

        var servers = McpConfigFile.Load(path);

        servers.Keys.Should().BeEquivalentTo("roslyn", "context7");
        servers["roslyn"].EffectiveType.Should().Be("stdio");
        servers["roslyn"].ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(2));
        servers["context7"].EffectiveType.Should().Be("http");
    }
}
