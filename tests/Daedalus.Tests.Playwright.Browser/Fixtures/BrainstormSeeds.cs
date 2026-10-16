using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;

namespace Daedalus.Tests.Playwright.Browser.Fixtures;

/// <summary>
///     Brainstorm sessions through the real repository. The stub model never signals that a phase is complete, so a seeded
///     session opens with an assistant message carrying the marker, as the model's would, and offers Advance.
/// </summary>
public sealed class BrainstormSeeds(IServiceScopeFactory scopes)
{
    /// <summary>The seeded session's opening assistant message, with the phase-complete marker stripped as the page shows it.</summary>
    public const string OpeningMessage = "Who will use the widget, and what do they need first?";

    /// <summary>The reply the stubbed model gives to every prompt.</summary>
    public const string StubReply = "This is a test response from StubAgentFactory";

    /// <summary>
    ///     Starts a session on the seeded <c>Test Project</c> at <c>ContextGathering</c>, whose phase is signalled complete.
    ///     A scenario that sends or advances changes its session, so each one starts its own.
    /// </summary>
    public async Task<Guid> StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var projectId = await db.Projects.Where(p => p.ProjectName == "Test Project").Select(p => p.Id).SingleAsync(ct)
            .ConfigureAwait(false);

        var session = BrainstormSession.Create(projectId);
        if (session.IsFailure)
        {
            throw new InvalidOperationException($"Seeding a brainstorm session failed: {session.Error}");
        }

        var opened = session.Value.AddMessage(MessageRole.Assistant, $"{OpeningMessage} [PHASE_COMPLETE]");
        if (opened.IsFailure)
        {
            throw new InvalidOperationException($"Seeding the opening message failed: {opened.Error}");
        }

        var added = await scope.ServiceProvider.GetRequiredService<IBrainstormRepository>().AddAsync(session.Value, ct)
            .ConfigureAwait(false);
        if (added.IsFailure)
        {
            throw new InvalidOperationException($"Seeding a brainstorm session failed: {added.Error}");
        }

        return session.Value.Id;
    }
}
