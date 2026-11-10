using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <c>Rehydrate</c> on the skill and its version.</summary>
public sealed class SkillRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    /// <summary>
    ///     A stored skill loads as it is, even one <see cref="Skill.Create"/> would refuse today: an uppercase name,
    ///     a blank description, an empty body, uppercase and repeated tags and a local time.
    ///     Red: validate or normalise in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Skill_Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        var local = DateTime.SpecifyKind(_at, DateTimeKind.Local);
        Skill.Create("Legacy Skill", " ", "", ["A", "A"], " ", "h", isActive: false, local).IsFailure
            .Should().BeTrue("the values below are ones Create refuses");

        var skill = Skill.Rehydrate("Legacy Skill", " ", "", ["B", "A", "A"], " ", "h", isActive: false, local);

        skill.Id.Should().Be("Legacy Skill");
        skill.Description.Should().Be(" ");
        skill.Body.Should().BeEmpty();
        skill.Tags.Should().Equal("B", "A", "A");
        skill.SourcePath.Should().Be(" ");
        skill.ContentHash.Should().Be("h");
        skill.IsActive.Should().BeFalse();
        skill.UpdatedAt.Should().Be(local);
        skill.UpdatedAt.Kind.Should().Be(DateTimeKind.Local);
    }

    /// <summary>
    ///     A stored version loads as it is, even one <see cref="SkillVersion.Create"/> would refuse today.
    ///     Red: validate or normalise in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Version_Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        var local = DateTime.SpecifyKind(_at, DateTimeKind.Local);
        SkillVersion.Create("Legacy Skill", "h", " ", "", ["A", "A"], " ", local).IsFailure
            .Should().BeTrue("the values below are ones Create refuses");

        var version = SkillVersion.Rehydrate("Legacy Skill", "h", " ", "", ["B", "A", "A"], " ", local);

        version.Name.Should().Be("Legacy Skill");
        version.ContentHash.Should().Be("h");
        version.Description.Should().Be(" ");
        version.Body.Should().BeEmpty();
        version.Tags.Should().Equal("B", "A", "A");
        version.SourcePath.Should().Be(" ");
        version.CreatedAt.Should().Be(local);
        version.CreatedAt.Kind.Should().Be(DateTimeKind.Local);
    }
}
