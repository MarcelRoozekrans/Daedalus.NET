using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Skills.Charters;
using static Daedalus.Tests.Unit.Configuration.ChartersTestSupport;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Phase 2.4 task B2: the reviewer's independence rests on holding no write tool, and that tool list now
///     lives in configuration (<see cref="AgentEnvelope"/>) while its prose, model and skills come from a synced
///     role charter (<c>roles/reviewer.md</c>). These tests build from the shipped <c>Daedalus.Api/appsettings.json</c>
///     and the shipped <c>roles/</c> files - never a hand-built envelope or charter - and check the two things
///     that would otherwise let that split come apart silently: a chartered agent composed from a mismatched
///     config/charter pair, and <c>ValidateCharterConfig</c> catching a chartered agent still defined in both
///     places.
/// </summary>
public sealed class CharteredRoleCompositionTests
{
    [Fact]
    public async Task The_reviewer_composed_from_shipped_config_and_shipped_charter_holds_no_write_tool()
    {
        await using var sp = await BuildWithApiConfigurationAndSyncedChartersAsync();
        var catalog = sp.GetRequiredService<IAgentCatalog>();

        var reviewer = catalog.Agents.Should().ContainSingle(a => a.Name == "reviewer").Subject;
        reviewer.Tools.Should().NotContain("roslyn__*",
            "a wildcard would silently admit any future write-capable roslyn tool");
        reviewer.Tools.Should().NotContain(t =>
            t.StartsWith("roslyn__apply_", StringComparison.Ordinal) || t.StartsWith("roslyn__rename_", StringComparison.Ordinal));
        reviewer.Tools.Should().NotContain(t =>
            t.StartsWith("git__", StringComparison.Ordinal) || t.StartsWith("repoaction__", StringComparison.Ordinal));
        reviewer.Model.Should().Be("claude-opus-5", "the model now comes from roles/reviewer.md");
        reviewer.Revision.Should().NotBeNullOrEmpty("the charter's content hash pins which version this definition was composed from");
    }

    /// <summary>
    ///     Fix round 1: <c>DaedalusAgentsOptions.CharterRoots</c> used to be a get-only <c>IList&lt;string&gt;</c>
    ///     pre-populated with <c>["roles"]</c>. <c>ConfigurationBinder</c> binds into a get-only collection by
    ///     appending rather than replacing it, so the shipped <c>"CharterRoots": [ "roles" ]</c> produced
    ///     <c>["roles", "roles"]</c> — which made <c>CharterSyncService</c> scan the same root twice, upsert
    ///     each role once and then hit its own already-seen role a second time as a duplicate (Thalos
    ///     <c>EventId 603</c>), and skip <c>roles/README.md</c> twice over. Binding must yield exactly one root.
    /// </summary>
    [Fact]
    public void Binding_the_shipped_Api_config_yields_exactly_one_charter_root()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ApiAppSettingsFileName, optional: false)
            .Build();

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        options.CharterRoots.Should().Equal("roles");
    }

    [Fact]
    public void A_chartered_agent_with_an_empty_tool_list_fails_registration()
    {
        var (services, options, configuration, environment) = LoadShippedOptions();
        var reviewer = options.Agents.Single(a => string.Equals(a.Name, "reviewer", StringComparison.Ordinal));
        reviewer.Tools.Clear();

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*reviewer*Tools*");
    }

    [Fact]
    public void A_chartered_agent_that_still_has_instructions_fails_registration()
    {
        var (services, options, configuration, environment) = LoadShippedOptions();
        var reviewer = options.Agents.Single(a => string.Equals(a.Name, "reviewer", StringComparison.Ordinal));
        reviewer.Instructions = "left behind";

        var act = () => services.AddDaedalusAgents(options, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*reviewer*Instructions*");
    }

    /// <summary>
    ///     Binds the shipped Api configuration into a fresh <see cref="DaedalusAgentsOptions"/> so a test can
    ///     mutate one field (an array, or a single scalar) in code, then register through the internal
    ///     <c>AddDaedalusAgents(DaedalusAgentsOptions, ...)</c> seam. <see cref="AgentConfig"/> is a mutable class
    ///     (not a record), so the <see cref="AgentConfig"/> instances inside the returned
    ///     <see cref="DaedalusAgentsOptions"/> are the live ones — mutating one is enough.
    /// </summary>
    private static (ServiceCollection Services, DaedalusAgentsOptions Options, IConfiguration Configuration, IHostEnvironment Environment) LoadShippedOptions()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ApiAppSettingsFileName, optional: false)
            .Build();

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());

        return (services, options, configuration, environment);
    }
}
