using AI.Sentinel;
using AI.Sentinel.Detectors.Security;
using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Daedalus.Agents.Security;
using Daedalus.Agents.Skills;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Skills;
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
    public void Disabled_detector_names_switch_the_detector_off()
    {
        using var sp = Build(Config(("Thalos:Sentinel:DisabledDetectors:0", nameof(PromptInjectionDetector))));

        var seen = false;
        sp.GetRequiredService<SentinelOptions>().Configure<PromptInjectionDetector>(c =>
        {
            seen = true;
            c.Enabled.Should().BeFalse();
        });

        seen.Should().BeTrue();
    }

    [Fact]
    public void Memory_is_wired_with_the_postgres_store_and_the_ragnet_index()
    {
        var embeddings = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        using var sp = Build(Config(("ConnectionStrings:daedalus", "Host=localhost;Database=x;Username=u;Password=p")), embeddings);

        sp.GetRequiredService<IMemoryService>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryStore>().GetType().Name.Should().BeOneOf("PostgresMemoryStore", "MemoryStoreInstrumented");
        sp.GetRequiredService<PostgresMemoryStore>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryIndex>().GetType().FullName.Should().Contain("RagNet");
        sp.GetRequiredService<Daedalus.Application.Abstractions.ILearningsMemory>().Should().BeOfType<ThalosLearningsMemory>();
        sp.GetRequiredService<RagNetMemoryOptions>().Should().BeEquivalentTo(new { ConnectionString = "Host=localhost;Database=x;Username=u;Password=p", VectorDimensions = 768, EnsureSchemaOnStartup = true });
        sp.GetServices<IHostedService>().Should().Contain(h => h.GetType().Name == "RagNetMemorySchemaInitializer");
    }

    [Fact]
    public void Memory_index_is_unavailable_without_an_embedding_generator_but_the_host_still_resolves()
    {
        using var sp = Build(Config());

        sp.GetRequiredService<IMemoryService>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryIndex>().Should().BeSameAs(UnavailableMemoryIndex.Instance);
    }

    [Fact]
    public void Memory_config_binds_from_Thalos_Memory_with_daedalus_defaults()
    {
        using var sp = Build(Config(("Thalos:Memory:Recall:TopK", "7"), ("Thalos:Memory:VectorDimensions", "512"), ("Thalos:Memory:Reindex:RetryInterval", "00:00:30")));

        var config = sp.GetRequiredService<MemoryConfig>();
        config.SharedOwnerId.Should().Be("daedalus");
        config.VectorDimensions.Should().Be(512);
        config.Reindex.RetryInterval.Should().Be(TimeSpan.FromSeconds(30));
        config.Enabled.Should().BeTrue();
        config.RalphRecall.Should().BeEquivalentTo(new { TopK = 10, MinScore = 0.5 });

        var thalos = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
        thalos.Enabled.Should().BeTrue();
        thalos.SharedOwnerId.Should().Be("daedalus");
        thalos.Recall.TopK.Should().Be(7);
        sp.GetRequiredService<RagNetMemoryOptions>().VectorDimensions.Should().Be(512);
    }

    [Fact]
    public void Memory_can_be_disabled_from_configuration()
    {
        using var sp = Build(Config(("Thalos:Memory:Enabled", "false")));

        sp.GetRequiredService<MemoryConfig>().Enabled.Should().BeFalse();
        sp.GetRequiredService<IOptions<MemoryOptions>>().Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Agent_memory_settings_bind_onto_the_definition()
    {
        using var sp = Build(Config(("Thalos:Agents:0:Memory:Enabled", "false"), ("Thalos:Agents:0:Memory:TopK", "3")));

        var agent = sp.GetRequiredService<IAgentCatalog>().Agents.Single();
        agent.Memory.Should().BeEquivalentTo(new { Enabled = (bool?)false, TopK = (int?)3 });
    }

    [Fact]
    public void Agent_without_memory_section_inherits_the_global_settings()
    {
        using var sp = Build(Config());

        sp.GetRequiredService<IAgentCatalog>().Agents.Single().Memory.Should().BeNull();
    }

    [Fact]
    public void AddDaedalusMemory_registers_memory_for_the_ralph_console_host_without_agents()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusMemory(Config());
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IMemoryService>().Should().NotBeNull();
        sp.GetRequiredService<PostgresMemoryStore>().Should().NotBeNull();
        sp.GetRequiredService<Daedalus.Application.Abstractions.ILearningsMemory>().Should().BeOfType<ThalosLearningsMemory>();
        sp.GetRequiredService<MemoryConfig>().SharedOwnerId.Should().Be("daedalus");
        sp.GetServices<IHostedService>().Should().NotContain(h => h.GetType().Name == "ReindexPendingMemoriesHostedService");
        sp.GetRequiredService<IAgentCatalog>().Agents.Should().BeEmpty("the console host declares no agents");

        // The API host creates rag_chunks; two hosts racing CREATE EXTENSION/TABLE/INDEX can fail on the pg catalog.
        sp.GetRequiredService<RagNetMemoryOptions>().EnsureSchemaOnStartup.Should().BeFalse();
        sp.GetServices<IHostedService>().Should().NotContain(h => h.GetType().Name == "RagNetMemorySchemaInitializer");
    }

    [Fact]
    public void AddDaedalusAgents_is_the_host_that_creates_the_ragnet_schema()
    {
        using var sp = Build(Config());

        sp.GetRequiredService<RagNetMemoryOptions>().EnsureSchemaOnStartup.Should().BeTrue();
        sp.GetServices<IHostedService>().Should().ContainSingle(h => h.GetType().Name == "RagNetMemorySchemaInitializer");
    }

    [Fact]
    public void Api_host_runs_the_reindex_sweeper()
    {
        using var sp = Build(Config());

        sp.GetServices<IHostedService>().Should().ContainSingle(h => h is ReindexPendingMemoriesHostedService);
    }

    [Theory]
    [InlineData("Thalos:Memory:Reindex:Enabled", "false")]
    [InlineData("Thalos:Memory:Enabled", "false")]
    public void Reindex_sweeper_is_not_registered_when_switched_off(string key, string value)
    {
        using var sp = Build(Config((key, value)));

        sp.GetServices<IHostedService>().Should().NotContain(h => h is ReindexPendingMemoriesHostedService);
    }

    [Theory]
    [InlineData("Thalos:Memory:SharedOwnerId", "  ", "SharedOwnerId")]
    [InlineData("Thalos:Memory:VectorDimensions", "0", "VectorDimensions")]
    [InlineData("Thalos:Memory:RalphRecall:TopK", "0", "TopK")]
    [InlineData("Thalos:Memory:RalphRecall:MinScore", "1.5", "MinScore")]
    [InlineData("Thalos:Memory:Reindex:SweepInterval", "00:00:00", "SweepInterval")]
    public void Out_of_range_memory_settings_fail_fast_naming_the_key(string key, string value, string expectedInMessage)
    {
        var act = () => Build(Config((key, value)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expectedInMessage}*");
    }

    [Fact]
    public void Unknown_sentinel_action_or_detector_fails_fast()
    {
        // A fresh collection per case: a failed registration leaves the services added before the throw behind, and
        // memory registration refuses to run twice on one collection.
        var badAction = () => new ServiceCollection().AddDaedalusAgents(Config(("Thalos:Sentinel:OnCritical", "Explode")), Environment());
        badAction.Should().Throw<InvalidOperationException>().WithMessage("*OnCritical*Explode*");

        var badDetector = () => new ServiceCollection().AddDaedalusAgents(Config(("Thalos:Sentinel:DisabledDetectors:0", "NoSuchDetector")), Environment());
        badDetector.Should().Throw<InvalidOperationException>().WithMessage("*NoSuchDetector*");
    }

    [Theory]
    [InlineData("Thalos:Skills:Catalogue:MaxChars", "0", "MaxChars")]
    [InlineData("Thalos:Skills:Search:TopK", "0", "TopK")]
    [InlineData("Thalos:Skills:Search:MinScore", "1.5", "MinScore")]
    [InlineData("Thalos:Skills:Roots:0", "   ", "Roots")]
    public void Out_of_range_skill_settings_fail_fast_naming_the_key(string key, string value, string expectedInMessage)
    {
        var act = () => Build(Config((key, value)));

        // Both halves matter: the section name tells an operator which block of appsettings.json to open, and the
        // member name tells them which key in it. Asserting only the member would pass on a message naming Memory.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{SkillsConfig.SectionName}*")
            .WithMessage($"*{expectedInMessage}*");
    }

    [Fact]
    public void A_configured_skills_root_that_does_not_exist_fails_fast()
    {
        // An agent that silently lost every procedure looks exactly like a healthy one, so a broken Content copy or a
        // bad path must take the host down at registration rather than at the first turn.
        var missing = Path.Combine(Path.GetTempPath(), $"no-skills-{Guid.NewGuid():N}");

        var act = () => Build(Config(("Thalos:Skills:Roots:0", missing)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
    }

    [Fact]
    public void Skills_are_off_when_no_root_is_configured()
    {
        // The binder appends to pre-populated lists, so the default must be empty: the registration tests run with a
        // content root that has no skills folder, and "nothing configured" is not an error.
        using var sp = Build(Config());

        sp.GetRequiredService<SkillsConfig>().Roots.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_skills_skip_root_validation_entirely()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-skills-{Guid.NewGuid():N}");

        var act = () => Build(Config(("Thalos:Skills:Enabled", "false"), ("Thalos:Skills:Roots:0", missing)));

        act.Should().NotThrow();
    }

    [Fact]
    public void Skills_are_wired_with_the_postgres_store_when_a_root_is_configured()
    {
        var root = Directory.CreateTempSubdirectory("daedalus-skills-").FullName;
        try
        {
            using var sp = Build(Config(
                ("Thalos:Skills:Roots:0", root),
                ("Thalos:Skills:Catalogue:MaxChars", "1234"),
                ("Thalos:Skills:Search:TopK", "9")));

            sp.GetRequiredService<ISkillStore>().GetType().Name.Should().BeOneOf("PostgresSkillStore", "SkillStoreInstrumented");
            sp.GetRequiredService<PostgresSkillStore>().Should().NotBeNull();

            var options = sp.GetRequiredService<IOptions<SkillOptions>>().Value;
            options.Enabled.Should().BeTrue();
            // Equal([root], because) - NOT Equal(root, because): the params overload would read the reason string as a
            // second expected element and assert two roots.
            options.Roots.Should().Equal([root], "roots reach Thalos already resolved to absolute paths");
            options.Catalogue.MaxChars.Should().Be(1234);
            options.Search.TopK.Should().Be(9);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Relative_skill_roots_resolve_against_the_content_root()
    {
        var contentRoot = Directory.CreateTempSubdirectory("daedalus-content-").FullName;
        Directory.CreateDirectory(Path.Combine(contentRoot, "skills"));
        try
        {
            var env = Substitute.For<IHostEnvironment>();
            env.ContentRootPath.Returns(contentRoot);
            env.EnvironmentName.Returns("Development");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
            services.AddDaedalusAgents(Config(("Thalos:Skills:Roots:0", "skills")), env);
            using var sp = services.BuildServiceProvider();

            sp.GetRequiredService<IOptions<SkillOptions>>().Value.Roots
                .Should().Equal(Path.Combine(contentRoot, "skills"));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void Skills_can_be_disabled_from_configuration()
    {
        using var sp = Build(Config(("Thalos:Skills:Enabled", "false")));

        sp.GetRequiredService<SkillsConfig>().Enabled.Should().BeFalse();
        sp.GetRequiredService<IOptions<SkillOptions>>().Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Agent_skill_globs_bind_onto_the_definition()
    {
        using var sp = Build(Config(("Thalos:Agents:0:Skills:0", "daedalus-*"), ("Thalos:Agents:0:Skills:1", "thalos-release")));

        sp.GetRequiredService<IAgentCatalog>().Agents.Single().Skills.Should().Equal("daedalus-*", "thalos-release");
    }

    [Fact]
    public void An_agent_without_skill_globs_gets_none()
    {
        // Procedures are granted explicitly. Defaulting to "*" would hand every agent every procedure the moment one
        // is added to the repo, which is the opposite of the per-agent gate the design asks for.
        using var sp = Build(Config());

        sp.GetRequiredService<IAgentCatalog>().Agents.Single().Skills.Should().BeEmpty();
    }

    [Fact]
    public void AddDaedalusMemory_does_not_register_skills()
    {
        // Skills are API-host only: the Ralph console runs no Thalos agents, so it has nothing to hand a catalogue to.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusMemory(Config());
        using var sp = services.BuildServiceProvider();

        sp.GetService<ISkillStore>().Should().BeNull();
        sp.GetService<PostgresSkillStore>().Should().BeNull();
        sp.GetService<SkillsConfig>().Should().BeNull();
        sp.GetServices<IHostedService>().Should().NotContain(h => h.GetType().Name.Contains("Skill", StringComparison.Ordinal));
    }
}
