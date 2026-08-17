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
