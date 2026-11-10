using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <c>Rehydrate</c> on the agent memory.</summary>
public sealed class AgentMemoryRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    /// <summary>
    ///     A stored memory loads as it is, even one <see cref="AgentMemory.Create"/> would refuse today: an empty id,
    ///     a blank owner, an uppercase kind, blank text, uppercase and repeated tags, an importance above one, an update
    ///     that precedes the creation and a negative recall count.
    ///     Red: validate or normalise in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        var agent = Guid.NewGuid();
        var created = _at;
        var updated = _at.AddHours(-1);
        var recalled = _at.AddDays(1);
        AgentMemory.Create(Guid.Empty, " ", agent, "Kind", " ", ["A", "A"], null, 5, created, true, updated, true, -1, recalled)
            .IsFailure.Should().BeTrue("the values below are ones Create refuses");

        var memory = AgentMemory.Rehydrate(
            Guid.Empty, " ", agent, "Kind", " ", ["B", "A", "A"], "src", 5, created, updated, recalled, -1, true, true);

        memory.Id.Should().Be(Guid.Empty);
        memory.OwnerId.Should().Be(" ");
        memory.AgentId.Should().Be(agent);
        memory.Kind.Should().Be("Kind");
        memory.Text.Should().Be(" ");
        memory.Tags.Should().Equal("B", "A", "A");
        memory.Source.Should().Be("src");
        memory.Importance.Should().Be(5);
        memory.CreatedAt.Should().Be(created);
        memory.UpdatedAt.Should().Be(updated);
        memory.LastRecalledAt.Should().Be(recalled);
        memory.RecallCount.Should().Be(-1);
        memory.IsArchived.Should().BeTrue();
        memory.IndexPending.Should().BeTrue();
    }
}
