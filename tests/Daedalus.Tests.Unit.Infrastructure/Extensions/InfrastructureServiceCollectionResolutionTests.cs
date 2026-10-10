using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Infrastructure.Extensions;

/// <summary>
///     Guards DI resolution of <see cref="IPullRequestFactory"/> and <see cref="IRalphLoopOrchestrator"/>
///     against <see cref="InfrastructureServiceExtensions.AddExternalServices"/> and
///     <see cref="InfrastructureServiceExtensions.AddCodeAnalysisServices"/>, the registrations a host without
///     <c>AddDaedalusAgents</c> uses to build the code-analysis object graph.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> Consolidating the GitHub HTTP client onto a single <c>GitHubApi</c> replaced
///         <c>AddHttpClient&lt;GitHubPullRequestFactory&gt;()</c> — which had registered the concrete type as a SIDE
///         EFFECT of giving it an <c>HttpClient</c> — with a call into <c>AddGitHubApi</c>, and dropped the
///         concrete-type registration entirely. Nothing was deliberately deleted, so review saw nothing wrong.
///         <c>PullRequestFactory</c> takes <c>GitHubPullRequestFactory</c> as a constructor parameter rather than
///         behind an interface, so resolving <see cref="IPullRequestFactory"/> — and anything built on top of it —
///         failed at runtime with a container-resolution exception. All four unit projects stayed green throughout
///         (none of them built a real <see cref="IServiceCollection"/>), and the build was clean; it surfaced only
///         when a container-bound Integration test was run.
///     </para>
///     <para>
///         <b>Why no database.</b> The real composition root also calls <c>AddApplicationDatabase</c> and registers
///         <c>ApplicationDbContext</c>-backed repositories, none of which this graph needs
///         (<see cref="IRalphLoopOrchestrator"/> depends on <c>ICodeAnalysisRepository</c>, an EF Core repository).
///         Substituting that interface after calling the real extension methods keeps this test in the fast
///         unit gate — no Postgres, no Testcontainers, sub-second — while still exercising the exact registration
///         calls that broke.
///     </para>
/// </remarks>
public sealed class InfrastructureServiceCollectionResolutionTests
{
    [Fact]
    public void Registrations_without_agents_resolve_the_pull_request_and_orchestrator_graph()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var configuration = new ConfigurationBuilder().Build();

        // AddExternalServices, then AddCodeAnalysisServices. Deliberately WITHOUT AddDaedalusAgents: a host that
        // registers code analysis alone must still resolve the graph, and that asymmetry is what let a dropped
        // registration hide behind the API host's green tests.
        services.AddExternalServices(configuration);
        services.AddCodeAnalysisServices(configuration);

        // ICodeAnalysisRepository, IFailurePatternDatabase and IBrainstormRepository are all EF Core repositories
        // needing ApplicationDbContext, which nothing in this test registers. Only ICodeAnalysisRepository sits on
        // the graph under test (IRalphLoopOrchestrator's constructor); the other two are registered by
        // AddExternalServices but never resolved here, so they are left alone.
        services.AddScoped<ICodeAnalysisRepository>(_ => Substitute.For<ICodeAnalysisRepository>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var act = () =>
        {
            scope.ServiceProvider.GetRequiredService<IPullRequestFactory>();
            scope.ServiceProvider.GetRequiredService<IRalphLoopOrchestrator>();
        };

        act.Should().NotThrow(
            "code analysis still resolves the pull request factory and the orchestrator from these registrations");
    }
}
