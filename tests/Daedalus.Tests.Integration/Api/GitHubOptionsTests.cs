using Daedalus.Infrastructure.Services.GitHub;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos;

namespace Daedalus.Tests.Integration.Api;

/// <summary>
///     Phase 2.11, defect 4: <see cref="GitHubOptions.MaxItemsPerCategory"/>'s code default of 15 applies on the Api host;
///     no appsettings key overrides it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class GitHubOptionsTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Red: put <c>"MaxItemsPerCategory": 50</c> back into <c>src/Daedalus.Api/appsettings.json</c>; the value is 50.</summary>
    [Fact]
    public void The_Api_host_takes_the_per_category_cap_from_the_code_default()
    {
        using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());

        factory.Services.GetRequiredService<IOptions<GitHubOptions>>().Value.MaxItemsPerCategory.Should().Be(15);
    }
}
