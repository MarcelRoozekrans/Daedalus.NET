using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Persistence;

/// <summary>
///     Phase 2.14, task 1: a message added to a stored brainstorm session is kept. The browser scenario saw a sent
///     message missing from the next read of the session.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrainstormMessagePersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Guid _sessionId;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        var project = IntegrationTestFactory.CreateProject();
        await using var db = NewContext();
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var session = BrainstormSession.Create(project.Id).Value;
        session.AddMessage(MessageRole.Assistant, "Who is it for?").IsSuccess.Should().BeTrue();
        (await Repository(db).AddAsync(session, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _sessionId = session.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ApplicationDbContext NewContext() => new(PostgresFixture.CreateDbContextOptions(fixture.ConnectionString));

    private static BrainstormRepository Repository(ApplicationDbContext db) =>
        new(db, Substitute.For<ILogger<BrainstormRepository>>());

    /// <summary>
    ///     As <c>BrainstormService.SendMessageAsync</c> does: read the session, add two messages, update.
    ///     Red: remove <c>ValueGeneratedNever</c> from <c>BrainstormMessageConfiguration</c>; EF reads the new messages as
    ///     existing rows, the update hits none, and <c>UpdateAsync</c> fails as "modified by another operation".
    /// </summary>
    [Fact]
    public async Task A_message_added_to_a_stored_session_is_kept()
    {
        await using (var db = NewContext())
        {
            var repository = Repository(db);
            var session = (await repository.GetByIdAsync(_sessionId, CancellationToken.None)).Value;
            session.AddMessage(MessageRole.User, "Shop owners.").IsSuccess.Should().BeTrue();
            session.AddMessage(MessageRole.Assistant, "What do they count?").IsSuccess.Should().BeTrue();

            var updated = await repository.UpdateAsync(session, CancellationToken.None);

            updated.IsSuccess.Should().BeTrue(updated.IsFailure ? updated.Error : string.Empty);
        }

        await using var read = NewContext();
        var contents = await read.Set<BrainstormMessage>().Where(m => m.BrainstormSessionId == _sessionId)
            .OrderBy(m => m.CreatedAt).Select(m => m.Content).ToListAsync();
        contents.Should().Equal("Who is it for?", "Shop owners.", "What do they count?");
    }
}
