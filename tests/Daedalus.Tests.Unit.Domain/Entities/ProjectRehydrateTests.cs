using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <see cref="Project.Rehydrate"/>.</summary>
public sealed class ProjectRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    /// <summary>
    ///     A stored project loads as it is, even one <see cref="Project.Create"/> would refuse today: a blank name,
    ///     description and version, and a name over 256 characters; with every other value, its row version and its
    ///     tasks kept, and the strings not trimmed. Red: validate or trim in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        var longName = new string('N', 300);
        Project.Create(Guid.NewGuid(), longName, " ", " ").IsFailure.Should().BeTrue("the values below are ones Create refuses");
        Project.Create(Guid.NewGuid(), " ", "d").IsFailure.Should().BeTrue("a blank name is one Create refuses");
        var id = Guid.NewGuid();
        var task = DomainTask.Create(
            Guid.NewGuid(), id, "TASK-1", "T", "D", Priority.Low, "P", 1, Complexity.Low, "P", _at).Value;

        var project = Project.Rehydrate(
            id, " ", longName, " ", _at, _at.AddHours(1), " dev ", " https://example.com/r.git ", 42u, [task]);

        project.Id.Should().Be(id);
        project.Version.Should().Be(" ");
        project.ProjectName.Should().Be(longName);
        project.Description.Should().Be(" ");
        project.CreatedAt.Should().Be(_at);
        project.ModifiedAt.Should().Be(_at.AddHours(1));
        project.DefaultBranch.Should().Be(" dev ");
        project.RepositoryUrl.Should().Be(" https://example.com/r.git ");
        project.RowVersion.Should().Be(42u);
        project.Tasks.Should().ContainSingle().Which.Should().BeSameAs(task);
    }

    /// <summary>A project created in memory has no row version until it is read. Red: give it any other default.</summary>
    [Fact]
    public void A_created_project_has_no_row_version()
    {
        Project.Create(Guid.NewGuid(), "Name", "Description").Value.RowVersion.Should().Be(0u);
    }
}
