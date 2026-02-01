using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Tests for the StructuredLearningEntry domain entity.
/// </summary>
public class StructuredLearningEntryTests
{
    #region RecordReference

    [Fact]
    public void RecordReference_ShouldIncrementHitCount()
    {
        // Arrange
        var entry = StructuredLearningEntry.Create(
            LearningCategory.ErrorPattern,
            "Some error pattern",
            "Some resolution").Value;

        // Act
        entry.RecordReference();
        entry.RecordReference();
        entry.RecordReference();

        // Assert
        entry.HitCount.Should().Be(3);
        entry.LastReferencedAt.Should().NotBeNull();
    }

    #endregion

    #region Creation

    [Fact]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        // Arrange
        var category = LearningCategory.ErrorPattern;
        var pattern = "CS1061: Type does not contain definition";
        var resolution = "Add missing using directive for ZLinq";

        // Act
        var result = StructuredLearningEntry.Create(category, pattern, resolution);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Category.Should().Be(category);
        result.Value.Pattern.Should().Be(pattern);
        result.Value.Resolution.Should().Be(resolution);
        result.Value.Severity.Should().Be(LearningSeverity.Medium);
        result.Value.HitCount.Should().Be(0);
        result.Value.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Create_WithAllParameters_ShouldSucceed()
    {
        // Arrange
        var tags = new[] { "ef core", "migration" };
        var sourceTaskId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        // Act
        var result = StructuredLearningEntry.Create(
            LearningCategory.SuccessPattern,
            "Async streaming worked for large datasets",
            "Use IAsyncEnumerable for streaming",
            tags,
            sourceTaskId,
            projectId,
            LearningSeverity.High);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.SourceTaskId.Should().Be(sourceTaskId);
        result.Value.ProjectId.Should().Be(projectId);
        result.Value.Severity.Should().Be(LearningSeverity.High);
        result.Value.Tags.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithEmptyPattern_ShouldFail(string? pattern)
    {
        // Act
        var result = StructuredLearningEntry.Create(
            LearningCategory.ErrorPattern, pattern!, "Some resolution");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Pattern");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithEmptyResolution_ShouldFail(string? resolution)
    {
        // Act
        var result = StructuredLearningEntry.Create(
            LearningCategory.ErrorPattern, "Some pattern", resolution!);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Resolution");
    }

    [Fact]
    public void Create_ShouldTrimPatternAndResolution()
    {
        // Act
        var result = StructuredLearningEntry.Create(
            LearningCategory.CodeConvention,
            "  Use primary constructors  ",
            "  Reduce boilerplate  ");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Pattern.Should().Be("Use primary constructors");
        result.Value.Resolution.Should().Be("Reduce boilerplate");
    }

    [Fact]
    public void Create_WithWhitespaceOnlyTags_ShouldIgnoreThem()
    {
        // Arrange
        var tags = new[] { "valid", "  ", "", "also-valid" };

        // Act
        var result = StructuredLearningEntry.Create(
            LearningCategory.DependencyInfo,
            "ZLinq 1.5.4",
            "Use AsValueEnumerable()",
            tags);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Tags.Should().HaveCount(2);
    }

    [Fact]
    public void Create_ShouldNormalizeTagsToUpperCase()
    {
        // Arrange
        var tags = new[] { "Ef Core", "POSTGRESQL" };

        // Act
        var result = StructuredLearningEntry.Create(
            LearningCategory.DependencyInfo,
            "EF Core migration",
            "Run dotnet ef migrations add",
            tags);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Tags.Should().Contain("EF CORE");
        result.Value.Tags.Should().Contain("POSTGRESQL");
    }

    #endregion

    #region UpdateResolution

    [Fact]
    public void UpdateResolution_WithValidText_ShouldSucceed()
    {
        // Arrange
        var entry = StructuredLearningEntry.Create(
            LearningCategory.ErrorPattern,
            "Pattern",
            "Old resolution").Value;

        // Act
        var result = entry.UpdateResolution("Better resolution found");

        // Assert
        result.IsSuccess.Should().BeTrue();
        entry.Resolution.Should().Be("Better resolution found");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UpdateResolution_WithEmptyText_ShouldFail(string? resolution)
    {
        // Arrange
        var entry = StructuredLearningEntry.Create(
            LearningCategory.ErrorPattern,
            "Pattern",
            "Original").Value;

        // Act
        var result = entry.UpdateResolution(resolution!);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    #endregion

    #region AddTag

    [Fact]
    public void AddTag_WithValidTag_ShouldSucceed()
    {
        // Arrange
        var entry = StructuredLearningEntry.Create(
            LearningCategory.ErrorPattern,
            "Some pattern",
            "Some resolution").Value;

        // Act
        var result = entry.AddTag("ef core");

        // Assert
        result.IsSuccess.Should().BeTrue();
        entry.Tags.Should().Contain("EF CORE");
    }

    [Fact]
    public void AddTag_WithDuplicateTag_ShouldFail()
    {
        // Arrange
        var entry = StructuredLearningEntry.Create(
            LearningCategory.ErrorPattern,
            "Some pattern",
            "Some resolution").Value;
        entry.AddTag("middleware");

        // Act
        var result = entry.AddTag("MIDDLEWARE");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AddTag_WithEmptyTag_ShouldFail(string? tag)
    {
        // Arrange
        var entry = StructuredLearningEntry.Create(
            LearningCategory.ErrorPattern,
            "Some pattern",
            "Some resolution").Value;

        // Act
        var result = entry.AddTag(tag!);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    #endregion
}
