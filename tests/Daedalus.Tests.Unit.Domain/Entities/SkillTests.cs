using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for the <see cref="Skill"/> aggregate (the Thalos-free record of a synced procedure document).
/// </summary>
public sealed class SkillTests
{
    private static readonly DateTime _now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private static Result<Skill> Create(
        string name = "daedalus-migrations",
        string description = "How to add and apply an EF Core migration in this repo.",
        string body = "# Adding a migration\n1. ...",
        IEnumerable<string>? tags = null,
        string sourcePath = "skills/daedalus-migrations/SKILL.md",
        string contentHash = "0123456789abcdef",
        bool isActive = true) =>
        Skill.Create(name, description, body, tags ?? ["dotnet", "EF"], sourcePath, contentHash, isActive, _now);

    [Fact]
    public void Create_keeps_the_document_verbatim_and_normalises_tags()
    {
        var skill = Create(tags: ["  DotNet ", "ef", "EF", "", "   "]).Value;

        skill.Id.Should().Be("daedalus-migrations");
        skill.Description.Should().Be("How to add and apply an EF Core migration in this repo.");
        skill.Body.Should().Be("# Adding a migration\n1. ...");
        skill.Tags.Should().Equal("dotnet", "ef");
        skill.SourcePath.Should().Be("skills/daedalus-migrations/SKILL.md");
        skill.ContentHash.Should().Be("0123456789abcdef");
        skill.IsActive.Should().BeTrue();
        skill.UpdatedAt.Should().Be(_now);
    }

    [Theory]
    [InlineData("daedalus-migrations")]
    [InlineData("a")]
    [InlineData("a_b-c9")]
    public void Create_accepts_valid_names(string name) => Create(name).IsSuccess.Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Daedalus")]        // upper case
    [InlineData("9lives")]          // leading digit
    [InlineData("-leading")]        // leading dash
    [InlineData("has space")]
    [InlineData("has.dot")]
    public void Create_rejects_invalid_names(string name) =>
        Create(name).Error.Should().Contain("Name must match");

    [Fact]
    public void Name_may_be_64_chars_but_not_65()
    {
        Create("a" + new string('b', 63)).IsSuccess.Should().BeTrue();
        Create("a" + new string('b', 64)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Description_is_required_and_capped()
    {
        Create(description: "  ").Error.Should().Contain("Description is required");
        Create(description: new string('d', Skill.MaxDescriptionLength)).IsSuccess.Should().BeTrue();
        Create(description: new string('d', Skill.MaxDescriptionLength + 1)).Error.Should().Contain("300");
    }

    [Fact]
    public void Body_is_required_and_capped_at_64_kb()
    {
        Create(body: "   ").Error.Should().Contain("Body is required");
        Create(body: new string('x', Skill.MaxBodyLength)).IsSuccess.Should().BeTrue();
        Create(body: new string('x', Skill.MaxBodyLength + 1)).Error.Should().Contain("65536");
    }

    [Fact]
    public void Body_keeps_leading_and_trailing_whitespace()
    {
        // What the model reads is byte-for-byte what is in git (design section 3), so nothing is trimmed.
        Create(body: "\n# Title\n\n").Value.Body.Should().Be("\n# Title\n\n");
    }

    [Fact]
    public void Tags_are_capped_in_count_and_length()
    {
        Create(tags: Enumerable.Range(0, Skill.MaxTags + 1).Select(i => $"t{i}")).Error.Should().Contain("At most");
        Create(tags: [new string('t', Skill.MaxTagLength + 1)]).Error.Should().Contain("32");
    }

    [Fact]
    public void SourcePath_and_content_hash_are_required_and_capped()
    {
        Create(sourcePath: " ").Error.Should().Contain("Source path is required");
        Create(sourcePath: new string('p', Skill.MaxSourcePathLength + 1)).Error.Should().Contain("1024");
        Create(contentHash: " ").Error.Should().Contain("Content hash is required");
        Create(contentHash: new string('h', Skill.MaxContentHashLength + 1)).Error.Should().Contain("128");
    }

    [Fact]
    public void Update_replaces_every_field_and_bumps_the_timestamp()
    {
        var skill = Create().Value;
        var later = _now.AddHours(1);

        var result = skill.Update("New description.", "new body", ["x"], "skills/other/SKILL.md", "deadbeef", isActive: true, later);

        result.IsSuccess.Should().BeTrue();
        skill.Description.Should().Be("New description.");
        skill.Body.Should().Be("new body");
        skill.Tags.Should().Equal("x");
        skill.SourcePath.Should().Be("skills/other/SKILL.md");
        skill.ContentHash.Should().Be("deadbeef");
        skill.UpdatedAt.Should().Be(later);
    }

    [Fact]
    public void Update_validates_before_mutating_anything()
    {
        var skill = Create().Value;

        var result = skill.Update("ok", new string('x', Skill.MaxBodyLength + 1), null, "p", "h", true, _now.AddHours(1));

        result.IsFailure.Should().BeTrue();
        skill.Body.Should().Be("# Adding a migration\n1. ...");
        skill.Description.Should().Be("How to add and apply an EF Core migration in this repo.");
        skill.UpdatedAt.Should().Be(_now);
    }

    [Fact]
    public void An_inactive_skill_round_trips()
    {
        Create(isActive: false).Value.IsActive.Should().BeFalse();
    }
}
