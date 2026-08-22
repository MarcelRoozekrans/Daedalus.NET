using Microsoft.Extensions.Configuration;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Pins <c>Thalos:Channels:DefaultAgent</c> against each host's own <c>Thalos:Agents</c> catalogue.
/// </summary>
/// <remarks>
///     A <c>DefaultAgent</c> that does not match any configured agent <c>Name</c> currently fails only at
///     <b>runtime</b> — the first time a channel needs to bind an implicit session (<c>ChannelPump</c> resolves
///     the value by scanning <c>IAgentCatalog.Agents</c> for a <c>Name</c> match; there is no startup validation).
///     This exact defect already shipped once on this branch (a ULID instead of a <c>Name</c> in
///     <c>Daedalus.Api/appsettings.json</c>, fixed in <c>26cef65</c>, caught only by a hands-on CLI session) —
///     this test turns that class of drift into a build-time failure instead of a second surprise at runtime.
/// </remarks>
public sealed class DefaultAgentConfigurationTests
{
    private static IConfiguration Load(string fileName) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: false)
            .Build();

    private static void AssertDefaultAgentMatchesAnAgentName(string fileName)
    {
        var configuration = Load(fileName);

        var defaultAgent = configuration["Thalos:Channels:DefaultAgent"];
        defaultAgent.Should().NotBeNullOrWhiteSpace($"{fileName} must configure Thalos:Channels:DefaultAgent");

        var agentNames = configuration.GetSection("Thalos:Agents")
            .GetChildren()
            .Select(agent => agent["Name"])
            .ToArray();

        agentNames.Should().NotBeEmpty($"{fileName} must declare at least one Thalos:Agents entry");
        agentNames.Should().Contain(defaultAgent,
            $"{fileName}'s Thalos:Channels:DefaultAgent ('{defaultAgent}') must be a Name in its own Thalos:Agents " +
            "array — it is resolved by AgentDefinition.Name, not by AgentId (ULID-backed, no string constructor), " +
            "and a mismatch fails only at runtime the first time a channel binds an implicit session");
    }

    [Fact]
    public void Api_default_agent_matches_a_configured_agent_name() =>
        AssertDefaultAgentMatchesAnAgentName("Daedalus.Api.appsettings.json");

    [Fact]
    public void Cli_default_agent_matches_a_configured_agent_name() =>
        AssertDefaultAgentMatchesAnAgentName("Daedalus.Cli.appsettings.json");
}
