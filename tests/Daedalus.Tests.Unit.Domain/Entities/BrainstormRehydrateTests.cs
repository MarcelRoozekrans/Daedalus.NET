using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <see cref="BrainstormSession.Rehydrate"/> and <see cref="BrainstormMessage.Rehydrate"/>.</summary>
public sealed class BrainstormRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    /// <summary>
    ///     A stored session loads as it is, even one <see cref="BrainstormSession.Create"/> would refuse today: an empty
    ///     project id; with every other value, its row version and its messages kept in the order given. Red: validate in
    ///     <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Session_Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        BrainstormSession.Create(Guid.Empty).IsFailure.Should().BeTrue("the project id below is one Create refuses");
        var id = Guid.NewGuid();
        var first = BrainstormMessage.Rehydrate(Guid.NewGuid(), id, MessageRole.User, "a", BrainstormPhase.Proposals, _at);
        var second = BrainstormMessage.Rehydrate(Guid.NewGuid(), id, MessageRole.Assistant, "b", BrainstormPhase.Proposals, _at);

        var session = BrainstormSession.Rehydrate(
            id, Guid.Empty, BrainstormPhase.Abandoned, "design", "plan", true, _at, _at.AddHours(1), 42u, [first, second]);

        session.Id.Should().Be(id);
        session.ProjectId.Should().Be(Guid.Empty);
        session.Phase.Should().Be(BrainstormPhase.Abandoned);
        session.DesignDocument.Should().Be("design");
        session.ImplementationPlan.Should().Be("plan");
        session.PhaseCompleteSignaled.Should().BeTrue();
        session.CreatedAt.Should().Be(_at);
        session.CompletedAt.Should().Be(_at.AddHours(1));
        session.RowVersion.Should().Be(42u);
        session.Messages.Should().Equal(first, second);
    }

    /// <summary>
    ///     A stored message loads as it is, even one <see cref="BrainstormMessage.Create"/> would refuse today: an empty
    ///     session id and blank content. Red: validate in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Message_Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        BrainstormMessage.Create(Guid.Empty, MessageRole.User, " ", BrainstormPhase.Clarification).IsFailure.Should()
            .BeTrue("the values below are ones Create refuses");
        var id = Guid.NewGuid();

        var message = BrainstormMessage.Rehydrate(id, Guid.Empty, MessageRole.System, " ", BrainstormPhase.Clarification, _at);

        message.Id.Should().Be(id);
        message.BrainstormSessionId.Should().Be(Guid.Empty);
        message.Role.Should().Be(MessageRole.System);
        message.Content.Should().Be(" ");
        message.Phase.Should().Be(BrainstormPhase.Clarification);
        message.CreatedAt.Should().Be(_at);
    }

    /// <summary>A session created in memory has no row version until it is read. Red: give it any other default.</summary>
    [Fact]
    public void A_created_session_has_no_row_version()
    {
        BrainstormSession.Create(Guid.NewGuid()).Value.RowVersion.Should().Be(0u);
    }
}
