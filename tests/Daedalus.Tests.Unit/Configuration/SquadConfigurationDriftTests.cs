using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Guards the phase 2.3 manufacturing squad's roster and flag against the real, shipped configuration:
///     <c>Daedalus.Api/appsettings.json</c> (the manufacturing host) leaves <c>Thalos:Squad:Enabled</c> on,
///     <c>Daedalus.Cli/appsettings.json</c> (the interactive developer tool, same precedent as
///     <c>Thalos:Workflow:Enabled</c>) turns it off, the reviewer's tool list stays a positive read-only
///     enumeration, and both new agents are actually reachable through the real DI composition root rather than
///     only declared in JSON — the gap <c>Daedalus.Tests.Integration.Migrations.ProcessDefinitionSyncEndToEndTests</c>'
///     own history (Task 11, phase 2.2) found for <c>processes/</c>.
/// </summary>
public sealed class SquadConfigurationDriftTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private const string CliAppSettingsFileName = "Daedalus.Cli.appsettings.json";

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
        services.AddDaedalusAgents(Load(ApiAppSettingsFileName), environment);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Api_appsettings_declares_thalos_squad_enabled()
    {
        var configuration = Load(ApiAppSettingsFileName);

        // Asserting only the resolved value would pass just as well if the key were absent (SquadOptions.Enabled
        // defaults to false, so a missing key would actually read as disabled here) - but it would also pass if
        // some other default flipped. Assert the key is actually present before asserting what it says.
        configuration.GetSection("Thalos:Squad:Enabled").Exists().Should()
            .BeTrue($"{ApiAppSettingsFileName} must declare Thalos:Squad:Enabled explicitly");
        configuration.GetValue<bool>("Thalos:Squad:Enabled").Should()
            .BeTrue("the API is the host that runs manufacturing");
    }

    [Fact]
    public void Cli_appsettings_declares_thalos_squad_disabled()
    {
        var configuration = Load(CliAppSettingsFileName);

        // Both halves matter: SquadOptions.Enabled already defaults to false, so asserting only the value would
        // pass exactly as well with the key deleted entirely as with it present and false - the preceding phase
        // shipped exactly that gap for a different flag.
        configuration.GetSection("Thalos:Squad:Enabled").Exists().Should()
            .BeTrue($"{CliAppSettingsFileName} must declare Thalos:Squad:Enabled explicitly, not rely on the default");
        configuration.GetValue<bool>("Thalos:Squad:Enabled").Should().BeFalse();
    }

    [Fact]
    public void Reviewer_tool_list_holds_no_write_capable_roslyn_tool()
    {
        var options = new DaedalusAgentsOptions();
        Load(ApiAppSettingsFileName).GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        var reviewer = options.Agents.Should().ContainSingle(a => a.Name == "reviewer").Subject;

        // The wildcard check matters on its own: a reviewer configured with "roslyn__*" would still fail the two
        // prefix checks below to spot (StartsWith("roslyn__apply_") is false for the literal string "roslyn__*"),
        // so the tool-level isolation this phase exists to catch would slip through unnoticed without it.
        reviewer.Tools.Should().NotContain("roslyn__*",
            "a wildcard would silently admit any future write-capable roslyn tool");
        reviewer.Tools.Should().NotContain(t => t.StartsWith("roslyn__apply_", StringComparison.Ordinal));
        reviewer.Tools.Should().NotContain(t => t.StartsWith("roslyn__rename_", StringComparison.Ordinal));
    }

    [Fact]
    public void Reviewer_is_priced_on_a_different_model_line_from_the_default()
    {
        var configuration = Load(ApiAppSettingsFileName);
        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        var reviewer = options.Agents.Should().ContainSingle(a => a.Name == "reviewer").Subject;
        var defaultModel = configuration["Thalos:Anthropic:DefaultModel"];

        reviewer.Model.Should().NotBeNullOrWhiteSpace();
        reviewer.Model.Should().NotBe(defaultModel,
            "the reviewer must run a different model line from the implementer's inherited default, " +
            "or the two roles share every blind spot the split exists to avoid");
    }

    [Fact]
    public void Implementer_and_reviewer_resolve_from_real_configuration_and_appear_in_the_catalog()
    {
        using var sp = BuildWithApiConfiguration();

        var agents = sp.GetRequiredService<IAgentCatalog>().Agents;
        agents.Should().Contain(a => a.Name == "implementer");
        agents.Should().Contain(a => a.Name == "reviewer");

        // Declaring a roster in Thalos:Agents is not the same as it being reachable: this resolves both role
        // names through the real SquadAgentResolver, built over the real Thalos:Squad:Enabled=true from the
        // shipped Api configuration, not a hand-constructed SquadOptions.
        var resolver = sp.GetRequiredService<SquadAgentResolver>();
        resolver.Resolve("implementer").Should().Be("implementer");
        resolver.Resolve("reviewer").Should().Be("reviewer");
    }
}
