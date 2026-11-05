using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <c>Rehydrate</c> on the role charter version and head.</summary>
public sealed class RoleCharterRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    /// <summary>
    ///     A stored version loads as it is, even one <see cref="RoleCharterVersion.Create"/> would refuse today: an
    ///     uppercase role, a blank description and source path and a local time.
    ///     Red: validate in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Version_Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        var local = DateTime.SpecifyKind(_at, DateTimeKind.Local);
        RoleCharterVersion.Create("Legacy Role", "h", " ", "body", null, ["a"], " ", local).IsFailure
            .Should().BeTrue("the values below are ones Create refuses");

        var version = RoleCharterVersion.Rehydrate("Legacy Role", "h", " ", "body", "m", ["b", "a"], " ", local);

        version.Role.Should().Be("Legacy Role");
        version.ContentHash.Should().Be("h");
        version.Description.Should().Be(" ");
        version.Instructions.Should().Be("body");
        version.Model.Should().Be("m");
        version.Skills.Should().Equal("b", "a");
        version.SourcePath.Should().Be(" ");
        version.UpdatedAt.Should().Be(local);
        version.UpdatedAt.Kind.Should().Be(DateTimeKind.Local);
    }

    /// <summary>
    ///     A stored head loads as it is, an inactive one included, which <see cref="RoleCharterHead.Create"/> never
    ///     makes. Red: force <c>IsActive</c> true, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Head_Rehydrate_keeps_every_value_including_an_inactive_head()
    {
        var head = RoleCharterHead.Rehydrate("reviewer", "hash-9", isActive: false, _at);

        head.Role.Should().Be("reviewer");
        head.CurrentHash.Should().Be("hash-9");
        head.IsActive.Should().BeFalse();
        head.UpdatedAt.Should().Be(_at);
    }
}
