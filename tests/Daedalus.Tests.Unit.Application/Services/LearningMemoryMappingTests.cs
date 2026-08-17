using Daedalus.Application.Services;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>Tests for the Ralph learning → memory record mapping.</summary>
public sealed class LearningMemoryMappingTests
{
    [Fact]
    public void Text_joins_pattern_and_resolution_unless_identical()
    {
        LearningMemoryMapping.Text("CS1061 missing member", "Add using ZLinq").Should().Be("CS1061 missing member\nAdd using ZLinq");
        LearningMemoryMapping.Text("same", "same").Should().Be("same");
    }

    [Fact]
    public void Tags_are_category_severity_then_at_most_eight_lowercased_tags()
    {
        var tags = LearningMemoryMapping.Tags(
            LearningCategory.ErrorPattern,
            LearningSeverity.High,
            ["EF Core", "postgresql", "a", "b", "c", "d", "e", "f", "g", "h"]);

        tags.Should().HaveCount(10);
        tags.Take(2).Should().Equal("errorpattern", "high");
        tags[2].Should().Be("ef core");
    }

    [Fact]
    public void Tags_drop_blanks_and_duplicates()
    {
        var tags = LearningMemoryMapping.Tags(LearningCategory.CodeConvention, LearningSeverity.Low, ["  ", "Api", "api", "dto"]);

        tags.Should().Equal("codeconvention", "low", "api", "dto");
    }

    [Fact]
    public void Text_is_truncated_to_the_memory_limit()
    {
        var text = LearningMemoryMapping.Text(new string('a', 3000), new string('b', 3000));

        text.Should().HaveLength(AgentMemory.MaxTextLength);
        text.Should().StartWith("aaa");
    }

    [Fact]
    public void Tags_are_truncated_to_the_memory_tag_limit()
    {
        var tags = LearningMemoryMapping.Tags(LearningCategory.ErrorPattern, LearningSeverity.High, [new string('x', 100)]);

        tags.Should().HaveCount(3);
        tags[2].Should().HaveLength(AgentMemory.MaxTagLength);
        tags.Should().OnlyContain(t => t.Length <= AgentMemory.MaxTagLength);
    }

    [Fact]
    public void A_long_learning_still_satisfies_the_aggregate_that_mirrors_the_thalos_rules()
    {
        var learning = AgentMemory.Create(
            Guid.NewGuid(),
            "daedalus",
            null,
            "learning",
            LearningMemoryMapping.Text(new string('p', 5000), new string('r', 5000)),
            LearningMemoryMapping.Tags(LearningCategory.ErrorPattern, LearningSeverity.Critical, [new string('t', 90), "ef core"]),
            LearningMemoryMapping.Source(Guid.NewGuid()),
            LearningMemoryMapping.Importance(LearningSeverity.Critical),
            DateTime.UtcNow,
            indexPending: true);

        learning.IsSuccess.Should().BeTrue(learning.IsFailure ? learning.Error : null);
    }

    [Theory]
    [InlineData(LearningSeverity.Critical, 1.0)]
    [InlineData(LearningSeverity.High, 0.8)]
    [InlineData(LearningSeverity.Medium, 0.5)]
    [InlineData(LearningSeverity.Low, 0.3)]
    public void Importance_follows_severity(LearningSeverity severity, double expected) =>
        LearningMemoryMapping.Importance(severity).Should().Be(expected);

    [Fact]
    public void Source_names_the_task() =>
        LearningMemoryMapping.Source(new Guid(0x11111111, 0x2222, 0x3333, 0x44, 0x44, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55))
            .Should().Be("ralph:task/11111111-2222-3333-4444-555555555555");
}
