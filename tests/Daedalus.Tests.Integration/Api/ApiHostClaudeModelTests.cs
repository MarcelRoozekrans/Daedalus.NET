using Daedalus.Infrastructure.Configuration;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Api;

/// <summary>
///     #385: the API host's Anthropic model, the one PRD generation, brainstorm and code analysis call, is Thalos' default
///     model, and the host refuses to start on a model with no price.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ApiHostClaudeModelTests(PostgresFixture fixture)
{
    /// <summary>Red: point ExternalServices:Llm:Claude:Model in the API's appsettings.json at a model of its own; it is that model.</summary>
    [Fact]
    public async Task The_shipped_host_calls_the_thalos_default_model()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());

        var claude = factory.Services.GetRequiredService<IOptions<ClaudeConfiguration>>().Value;

        claude.Model.Should().Be("claude-sonnet-5");
    }

    /// <summary>Red: drop the ClaudeModelPricedValidator registration from Program.cs; the host starts.</summary>
    [Fact]
    public async Task The_host_refuses_to_start_on_an_unpriced_model()
    {
        await using var factory = new ApiWebApplicationFactory(
            fixture.ConnectionString, Substitute.For<IAgentRuntime>(), settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ExternalServices:Llm:Claude:Model"] = "claude-sonnet-4-20250514-unpriced",
            });

        var act = () => factory.Services;

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("claude-sonnet-4-20250514-unpriced");
    }
}
