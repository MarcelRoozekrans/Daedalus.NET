using Daedalus.Application.Services;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Thalos;

namespace Daedalus.Tests.Integration.Services.CodeAnalysis;

/// <summary>
///     Guards DI resolution of <see cref="IPullRequestFactory"/>, <see cref="IWorkspaceOrchestrator"/> and
///     <see cref="IRalphLoopOrchestrator"/> on the real <c>Daedalus.Api</c> composition root — the one that calls
///     both <c>AddDaedalusAgents</c> and <c>AddCodeAnalysisServices</c> on the same container.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> Task 5's GitHub-client consolidation replaced
///         <c>AddHttpClient&lt;GitHubPullRequestFactory&gt;()</c> (which registered the concrete type as a side
///         effect of giving it an <c>HttpClient</c>) with a call into <c>AddGitHubApi</c> — and dropped the
///         concrete-type registration entirely. <c>PullRequestFactory</c> takes <c>GitHubPullRequestFactory</c> as
///         a constructor parameter, not behind an interface, so every consumer of <see cref="IPullRequestFactory"/>
///         failed to resolve. The four unit projects stayed green throughout, because none of them build a real
///         container — this only surfaces as a DI-resolution exception against a real
///         <see cref="IServiceCollection"/>.
///     </para>
///     <para>
///         <b>Why only the API root here.</b> The Console-style root (<c>AddCodeAnalysisServices</c> without
///         <c>AddDaedalusAgents</c> — the composition Daedalus.Console's <c>RalphLoopWorker</c> actually runs in
///         production) is covered by the faster
///         <c>Daedalus.Tests.Unit.Infrastructure.Extensions.InfrastructureServiceCollectionResolutionTests</c>,
///         which substitutes the two EF-Core-backed repositories this graph needs instead of paying for a real
///         Postgres container. That test belongs in the unit gate for the fast feedback; this one exists because
///         the API root additionally exercises <c>AddDaedalusAgents</c> and <c>AddGitHubApi</c>'s
///         double-registration guard together on one container, which the unit test does not, and because it runs
///         against the real, fully composed <c>Daedalus.Api</c> <c>Program</c> via <see cref="ApiWebApplicationFactory"/>
///         rather than a hand-assembled subset. The two roots are not symmetric and neither implies the other —
///         a fix that only satisfies one could still leave the other broken.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class PullRequestFactoryRegistrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void The_api_composition_root_resolves_the_full_graph_alongside_AddDaedalusAgents()
    {
        var runtime = Substitute.For<IAgentRuntime>();
        using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, runtime);
        using var scope = factory.Services.CreateScope();

        var act = () =>
        {
            scope.ServiceProvider.GetRequiredService<IPullRequestFactory>();
            scope.ServiceProvider.GetRequiredService<IWorkspaceOrchestrator>();
            scope.ServiceProvider.GetRequiredService<IRalphLoopOrchestrator>();
        };

        act.Should().NotThrow(
            "Daedalus.Api calls both AddDaedalusAgents and AddCodeAnalysisServices on one container, and " +
            "AddGitHubApi's double-registration guard must not leave either root's graph unresolvable");
    }
}
