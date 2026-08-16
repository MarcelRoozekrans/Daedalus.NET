using AI.Sentinel;
using Daedalus.Agents;
using Daedalus.Agents.Security;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Tools;
using ZeroAlloc.Authorization;

namespace Daedalus.Tests.Unit.Application.Agents;

public sealed class DaedalusAgentsRegistrationTests
{
    private const string Ulid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    private static IConfiguration Config(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Anthropic:DefaultModel"] = "claude-sonnet-5",
            ["Thalos:Agents:0:Id"] = Ulid,
            ["Thalos:Agents:0:Name"] = "Daedalus Assistant",
            ["Thalos:Agents:0:Description"] = "Knows the codebase",
            ["Thalos:Agents:0:Instructions"] = "You are helpful.",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IHostEnvironment Environment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        env.EnvironmentName.Returns("Development");
        return env;
    }

    private static ServiceProvider Build(IConfiguration configuration, IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusAgents(configuration, Environment(), embeddings);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Catalog_contains_the_configured_agent_with_parsed_ulid_and_wildcard_tools()
    {
        using var sp = Build(Config());

        var agent = sp.GetRequiredService<IAgentCatalog>().Agents.Should().ContainSingle().Subject;

        agent.Id.Should().Be(AgentId.Parse(Ulid, null));
        agent.Name.Should().Be("Daedalus Assistant");
        agent.Description.Should().Be("Knows the codebase");
        agent.Instructions.Should().Be("You are helpful.");
        agent.Model.Should().BeNull();
        agent.Tools.Should().Equal("*");
    }

    [Fact]
    public void Agent_id_may_be_a_guid_and_tools_model_and_max_tokens_bind()
    {
        var guid = Guid.NewGuid();
        using var sp = Build(Config(
            ("Thalos:Agents:0:Id", guid.ToString()),
            ("Thalos:Agents:0:Model", "claude-opus-4-1"),
            ("Thalos:Agents:0:MaxOutputTokens", "2048"),
            ("Thalos:Agents:0:Tools:0", "daedalus__*"),
            ("Thalos:Agents:0:Tools:1", "roslyn__find_*")));

        var agent = sp.GetRequiredService<IAgentCatalog>().Agents.Single();

        agent.Id.Should().Be(new AgentId(guid));
        agent.Model.Should().Be("claude-opus-4-1");
        agent.MaxOutputTokens.Should().Be(2048);
        agent.Tools.Should().Equal("daedalus__*", "roslyn__find_*");
    }

    [Fact]
    public void Invalid_agent_id_fails_fast_at_registration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddDaedalusAgents(Config(("Thalos:Agents:0:Id", "not-an-id")), Environment());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Daedalus Assistant*not-an-id*");
    }

    [Fact]
    public void Session_store_is_the_postgres_store_behind_the_telemetry_proxy()
    {
        using var sp = Build(Config());

        sp.GetRequiredService<IAgentSessionStore>().GetType().Name.Should().Be("AgentSessionStoreInstrumented");
        sp.GetRequiredService<Daedalus.Agents.Sessions.PostgresAgentSessionStore>().Should().NotBeNull();
    }

    [Fact]
    public void Runtime_resolves_without_an_anthropic_api_key()
    {
        using var sp = Build(Config());

        var act = () => sp.GetRequiredService<IAgentRuntime>();

        act.Should().NotThrow();
        sp.GetRequiredService<IChatClientProvider>().Name.Should().Be("anthropic");
        sp.GetRequiredService<IChatClientProvider>().DefaultModel.Should().Be("claude-sonnet-5");
    }

    [Fact]
    public void Knowledge_tools_are_registered_as_the_daedalus_local_tool_source()
    {
        using var sp = Build(Config());

        var sources = sp.GetServices<IToolSource>().ToList();

        sources.Should().ContainSingle(s => s is LocalToolSource).Which.Name.Should().Be("daedalus");
    }

    [Fact]
    public void Tool_policies_and_developer_policy_are_registered()
    {
        using var sp = Build(Config(
            ("Thalos:ToolPolicies:0:Pattern", "roslyn__apply_*"),
            ("Thalos:ToolPolicies:0:Policy", "developer")));

        var bindings = sp.GetRequiredService<IOptions<ThalosOptions>>().Value.ToolPolicies;
        bindings.Should().ContainSingle().Which.Should().BeEquivalentTo(new { ToolPattern = "roslyn__apply_*", PolicyName = "developer" });
        sp.GetServices<IAuthorizationPolicy>().Should().ContainSingle(p => p is DeveloperPolicy);
    }

    [Fact]
    public void Sentinel_is_configured_from_options_and_gets_the_embedding_generator()
    {
        var embeddings = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        using var sp = Build(Config(("Thalos:Sentinel:OnHigh", "quarantine"), ("Thalos:Sentinel:OnLow", "PassThrough")), embeddings);

        var sentinel = sp.GetRequiredService<SentinelOptions>();

        sentinel.OnCritical.Should().Be(SentinelAction.Quarantine);
        sentinel.OnHigh.Should().Be(SentinelAction.Quarantine);
        sentinel.OnMedium.Should().Be(SentinelAction.Log);
        sentinel.OnLow.Should().Be(SentinelAction.PassThrough);
        sentinel.EmbeddingGenerator.Should().BeSameAs(embeddings);
    }

    [Fact]
    public void Sentinel_without_embedding_generator_leaves_it_null()
    {
        using var sp = Build(Config());

        sp.GetRequiredService<SentinelOptions>().EmbeddingGenerator.Should().BeNull();
    }

    [Fact]
    public void Sentinel_can_be_disabled()
    {
        using var sp = Build(Config(("Thalos:Sentinel:Enabled", "false")));

        sp.GetService<SentinelOptions>().Should().BeNull();
    }

    [Fact]
    public void Unknown_sentinel_action_or_detector_fails_fast()
    {
        var services = new ServiceCollection();

        var badAction = () => services.AddDaedalusAgents(Config(("Thalos:Sentinel:OnCritical", "Explode")), Environment());
        badAction.Should().Throw<InvalidOperationException>().WithMessage("*OnCritical*Explode*");

        var badDetector = () => services.AddDaedalusAgents(Config(("Thalos:Sentinel:DisabledDetectors:0", "NoSuchDetector")), Environment());
        badDetector.Should().Throw<InvalidOperationException>().WithMessage("*NoSuchDetector*");
    }
}
