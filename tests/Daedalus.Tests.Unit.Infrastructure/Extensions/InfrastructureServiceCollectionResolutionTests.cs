using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Daedalus.Tests.Unit.Infrastructure.Extensions;

/// <summary>
///     Guards DI resolution of <see cref="IPullRequestFactory"/>, <see cref="IWorkspaceOrchestrator"/> and
///     <see cref="IRalphLoopOrchestrator"/> against <see cref="InfrastructureServiceExtensions.AddExternalServices"/>
///     and <see cref="InfrastructureServiceExtensions.AddCodeAnalysisServices"/> — the exact registrations
///     <c>Daedalus.Console</c>'s composition root calls to build the object graph <c>RalphLoopWorker</c> resolves in
///     production.
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
///         (<see cref="IWorkspaceOrchestrator"/> and <see cref="IRalphLoopOrchestrator"/> depend on
///         <c>IProjectRepository</c> and <c>ICodeAnalysisRepository</c> respectively, both EF Core repositories).
///         Substituting those two interfaces after calling the real extension methods keeps this test in the fast
///         unit gate — no Postgres, no Testcontainers, sub-second — while still exercising the exact registration
///         calls that broke.
///     </para>
/// </remarks>
public sealed class InfrastructureServiceCollectionResolutionTests
{
    [Fact]
    public void Console_style_registrations_resolve_the_pull_request_and_orchestrator_graph()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var configuration = new ConfigurationBuilder().Build();

        // Mirrors Daedalus.Console/Program.cs's call sequence for this graph: IProjectRepository is registered
        // directly (not through an extension method), then AddExternalServices, then AddCodeAnalysisServices.
        // Deliberately WITHOUT AddDaedalusAgents — Console never calls it, and that asymmetry is what let the
        // dropped registration hide behind the API host's green tests.
        services.AddScoped<IProjectRepository>(_ => Substitute.For<IProjectRepository>());
        services.AddExternalServices(configuration);
        services.AddCodeAnalysisServices(configuration);

        // ICodeAnalysisRepository, IFailurePatternDatabase, IPromptContextStore and IBrainstormRepository are all
        // EF Core repositories needing ApplicationDbContext, which nothing in this test registers. Only
        // ICodeAnalysisRepository sits on the graph under test (IRalphLoopOrchestrator's constructor); the other
        // three are registered by AddExternalServices but never resolved here, so they are left alone.
        services.AddScoped<ICodeAnalysisRepository>(_ => Substitute.For<ICodeAnalysisRepository>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var act = () =>
        {
            scope.ServiceProvider.GetRequiredService<IPullRequestFactory>();
            scope.ServiceProvider.GetRequiredService<IWorkspaceOrchestrator>();
            scope.ServiceProvider.GetRequiredService<IRalphLoopOrchestrator>();
        };

        act.Should().NotThrow(
            "Daedalus.Console's RalphLoopWorker resolves this exact graph in production, and Ralph is still the " +
            "live pull-request-creation path until phase 2.5");
    }
}
