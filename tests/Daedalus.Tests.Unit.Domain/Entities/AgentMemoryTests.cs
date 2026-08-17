using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for the <see cref="AgentMemory"/> aggregate (the Thalos-free record of a curated memory).
/// </summary>
public sealed class AgentMemoryTests
{
    private static readonly DateTime T0 = new(2026, 8, 17, 10, 0, 0, DateTimeKind.Utc);

    private static AgentMemory Valid(string text = "The user prefers xUnit.") =>
        AgentMemory.Create(Guid.NewGuid(), "alice", null, "fact", text, ["Testing", " xunit "], "tool:memory__remember", 0.7, T0, indexPending: true).Value;

    [Fact]
    public void Create_sets_fields_and_normalises_tags()
    {
        var m = Valid();
        m.OwnerId.Should().Be("alice");
        m.AgentId.Should().BeNull();
        m.Kind.Should().Be("fact");
        m.Text.Should().Be("The user prefers xUnit.");
        m.Tags.Should().Equal("testing", "xunit");
        m.Source.Should().Be("tool:memory__remember");
        m.Importance.Should().Be(0.7);
        m.CreatedAt.Should().Be(T0);
        m.UpdatedAt.Should().Be(T0);
        m.LastRecalledAt.Should().BeNull();
        m.RecallCount.Should().Be(0);
        m.IsArchived.Should().BeFalse();
        m.IndexPending.Should().BeTrue();
    }

    [Fact]
    public void Create_keeps_text_and_kind_verbatim()
    {
        // Thalos round-trips texts at the limit untrimmed (multi-line, trailing newline); custom kinds are stored as given.
        var text = string.Concat(Enumerable.Repeat("memo 🚀\n", AgentMemory.MaxTextLength / 8));
        var m = AgentMemory.Create(Guid.NewGuid(), "alice", Guid.NewGuid(), "ralph-learning", text, [], "", 0.5, T0, false).Value;

        m.Text.Should().Be(text);
        m.Kind.Should().Be("ralph-learning");
        m.AgentId.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" fact ")]
    [InlineData("Fact")]
    [InlineData("1st")]
    [InlineData("has space")]
    [InlineData("a.b")]
    public void Create_rejects_kinds_that_are_not_lowercase_identifiers(string kind)
    {
        // Same rule as Thalos MemoryKind.IsValid: ^[a-z][a-z0-9_-]{0,31}$ (a violation must not become a database error).
        AgentMemory.Create(Guid.NewGuid(), "alice", null, kind, "t", [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "alice", null, new string('k', 33), "t", [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "alice", null, "a" + new string('k', 31), "t", [], "", 0.5, T0, false).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_owner_ids_longer_than_256()
    {
        AgentMemory.Create(Guid.NewGuid(), new string('o', 257), null, "fact", "t", [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), new string('o', 256), null, "fact", "t", [], "", 0.5, T0, false).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_honours_bookkeeping_supplied_as_given_and_validates_it()
    {
        // The store inserts Thalos records "as given": UpdatedAt, archived flag and recall bookkeeping may be pre-set.
        var m = AgentMemory.Create(Guid.NewGuid(), "alice", null, "fact", "t", [], "", 0.5, T0, false,
            updatedAt: T0.AddMinutes(1), isArchived: true, recallCount: 3, lastRecalledAt: T0.AddMinutes(2)).Value;

        m.UpdatedAt.Should().Be(T0.AddMinutes(1));
        m.IsArchived.Should().BeTrue();
        m.RecallCount.Should().Be(3);
        m.LastRecalledAt.Should().Be(T0.AddMinutes(2));

        AgentMemory.Create(Guid.NewGuid(), "alice", null, "fact", "t", [], "", 0.5, T0, false, updatedAt: T0.AddSeconds(-1)).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "alice", null, "fact", "t", [], "", 0.5, T0, false, recallCount: -1).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("alice", "")]
    [InlineData("alice", " ")]
    public void Create_requires_owner_and_text(string owner, string text)
    {
        AgentMemory.Create(Guid.NewGuid(), owner, null, "fact", text, [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_empty_id_bad_kind_long_text_too_many_tags_and_importance_out_of_range()
    {
        AgentMemory.Create(Guid.Empty, "a", null, "fact", "t", [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "", "t", [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", new string('x', 4001), [], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", "t", Enumerable.Range(0, 11).Select(i => $"t{i}"), "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", "t", [new string('t', 33)], "", 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", "t", [], new string('s', 257), 0.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", "t", [], "", 1.5, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", "t", [], "", -0.1, T0, false).IsFailure.Should().BeTrue();
        AgentMemory.Create(Guid.NewGuid(), "a", null, "fact", "t", [], "", double.NaN, T0, false).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Update_changes_only_supplied_fields_and_bumps_updated_at()
    {
        var m = Valid();
        var later = T0.AddMinutes(5);

        m.Update(text: null, tags: ["b"], importance: null, isArchived: true, indexPending: false, later).IsSuccess.Should().BeTrue();

        m.Text.Should().Be("The user prefers xUnit.");
        m.Tags.Should().Equal("b");
        m.Importance.Should().Be(0.7);
        m.IsArchived.Should().BeTrue();
        m.IndexPending.Should().BeFalse();
        m.UpdatedAt.Should().Be(later);
    }

    [Fact]
    public void Update_of_index_pending_alone_does_not_bump_updated_at()
    {
        // Index bookkeeping is not a content change (Thalos MemoryUpdate.TouchesContent).
        var m = Valid();

        m.Update(null, null, null, null, indexPending: false, T0.AddMinutes(10)).IsSuccess.Should().BeTrue();

        m.IndexPending.Should().BeFalse();
        m.UpdatedAt.Should().Be(T0);
    }

    [Fact]
    public void Update_with_empty_tags_clears_them_and_normalises_new_tags()
    {
        var m = Valid();

        m.Update(null, [], null, null, null, T0.AddMinutes(1)).IsSuccess.Should().BeTrue();
        m.Tags.Should().BeEmpty();

        m.Update(null, ["Bar", " bar ", "baz"], null, null, null, T0.AddMinutes(2)).IsSuccess.Should().BeTrue();
        m.Tags.Should().Equal("bar", "baz");
    }

    [Fact]
    public void Update_validates_text_tags_and_importance()
    {
        var m = Valid();
        m.Update(" ", null, null, null, null, T0).IsFailure.Should().BeTrue();
        m.Update(new string('x', 4001), null, null, null, null, T0).IsFailure.Should().BeTrue();
        m.Update(null, null, 2.0, null, null, T0).IsFailure.Should().BeTrue();
        m.Update(null, Enumerable.Range(0, 11).Select(i => $"t{i}").ToList(), null, null, null, T0).IsFailure.Should().BeTrue();

        // A failed update leaves the aggregate untouched.
        m.Text.Should().Be("The user prefers xUnit.");
        m.Tags.Should().Equal("testing", "xunit");
        m.Importance.Should().Be(0.7);
        m.UpdatedAt.Should().Be(T0);
    }

}
