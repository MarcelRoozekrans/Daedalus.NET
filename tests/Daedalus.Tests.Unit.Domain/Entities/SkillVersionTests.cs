using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for the <see cref="SkillVersion"/> aggregate (an immutable snapshot of a synced <see cref="Skill"/>
///     document, keyed by name and content hash). Mirrors <see cref="SkillTests"/>: the limits are literally the same
///     constants, restated by <see cref="SkillVersion.Create"/> against <see cref="Skill"/>'s own <c>Max*</c> fields.
/// </summary>
public sealed class SkillVersionTests
{
    private static readonly DateTime _now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private static Result<SkillVersion> Create(
        string name = "daedalus-migrations",
        string contentHash = "0123456789abcdef",
        string description = "How to add and apply an EF Core migration in this repo.",
        string body = "# Adding a migration\n1. ...",
        IEnumerable<string>? tags = null,
        string sourcePath = "skills/daedalus-migrations/SKILL.md") =>
        SkillVersion.Create(name, contentHash, description, body, tags ?? ["dotnet", "EF"], sourcePath, _now);

    [Fact]
    public void Create_keeps_the_document_verbatim_and_normalises_tags()
    {
        var version = Create(tags: ["  DotNet ", "ef", "EF", "", "   "]).Value;

        version.Name.Should().Be("daedalus-migrations");
        version.ContentHash.Should().Be("0123456789abcdef");
        version.Description.Should().Be("How to add and apply an EF Core migration in this repo.");
        version.Body.Should().Be("# Adding a migration\n1. ...");
        version.Tags.Should().Equal("dotnet", "ef");
        version.SourcePath.Should().Be("skills/daedalus-migrations/SKILL.md");
        version.CreatedAt.Should().Be(_now);
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
    public void Content_hash_is_required_and_capped()
    {
        Create(contentHash: " ").Error.Should().Contain("Content hash is required");
        Create(contentHash: new string('h', Skill.MaxContentHashLength)).IsSuccess.Should().BeTrue();
        Create(contentHash: new string('h', Skill.MaxContentHashLength + 1)).Error.Should().Contain("128");
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
        // A version is a verbatim snapshot, same as the Skill it was synced from - nothing is trimmed.
        Create(body: "\n# Title\n\n").Value.Body.Should().Be("\n# Title\n\n");
    }

    [Fact]
    public void Tags_are_capped_in_count_and_length()
    {
        Create(tags: Enumerable.Range(0, Skill.MaxTags + 1).Select(i => $"t{i}")).Error.Should().Contain("At most");
        Create(tags: [new string('t', Skill.MaxTagLength + 1)]).Error.Should().Contain("32");
    }

    [Fact]
    public void SourcePath_is_required_and_capped()
    {
        Create(sourcePath: " ").Error.Should().Contain("Source path is required");
        Create(sourcePath: new string('p', Skill.MaxSourcePathLength)).IsSuccess.Should().BeTrue();
        Create(sourcePath: new string('p', Skill.MaxSourcePathLength + 1)).Error.Should().Contain("1024");
    }
}
