using Daedalus.Application.Services;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Tests for LearningsService parsing and classification logic.
///     Tests the internal static methods that do the actual text analysis.
/// </summary>
public class LearningsServiceParsingTests : UnitTestBase
{
    #region ParseRawLearnings

    [Fact]
    public void ParseRawLearnings_WithErrorPatterns_ShouldCreateErrorEntries()
    {
        // Arrange
        var rawLearnings = "⚠ 3 errors encountered:\n  - CS1061: Type does not contain definition (2 occurrence(s))";
        var sourceTaskId = Guid.NewGuid();

        // Act
        var entries = LearningsService.ParseRawLearnings(rawLearnings);

        // Assert
        entries.Should().NotBeEmpty();
        entries.Should().Contain(e => e.Category == LearningCategory.ErrorPattern);
    }

    [Fact]
    public void ParseRawLearnings_WithSuccessPatterns_ShouldCreateSuccessEntries()
    {
        // Arrange
        var rawLearnings = "✓ Success achieved at iteration 5\n  - Use similar approach to the iteration 5 response";
        var sourceTaskId = Guid.NewGuid();

        // Act
        var entries = LearningsService.ParseRawLearnings(rawLearnings);

        // Assert
        entries.Should().NotBeEmpty();
        entries.Should().Contain(e => e.Category == LearningCategory.SuccessPattern);
    }

    [Fact]
    public void ParseRawLearnings_WithPatternResolutionPairs_ShouldLinkThem()
    {
        // Arrange
        var rawLearnings = "Error: Missing using directive for ZLinq\n- Use AsValueEnumerable() from ZLinq namespace";
        var sourceTaskId = Guid.NewGuid();

        // Act
        var entries = LearningsService.ParseRawLearnings(rawLearnings);

        // Assert
        entries.Should().NotBeEmpty();
        var entry = entries[0];
        entry.Pattern.Should().NotBe(entry.Resolution);
    }

    [Fact]
    public void ParseRawLearnings_WithEmptyInput_ShouldReturnEmptyList()
    {
        // Act
        var entries = LearningsService.ParseRawLearnings("");

        // Assert
        entries.Should().BeEmpty();
    }

    [Fact]
    public void ParseRawLearnings_ShouldSkipVeryShortLines()
    {
        // Arrange
        var rawLearnings = "ok\nfoo\nbar";
        var sourceTaskId = Guid.NewGuid();

        // Act
        var entries = LearningsService.ParseRawLearnings(rawLearnings);

        // Assert
        entries.Should().BeEmpty(); // All lines < 5 chars after cleaning
    }

    [Fact]
    public void ParseRawLearnings_ShouldKeepCategorySeverityAndTags()
    {
        // Arrange — the task/project scope is no longer part of the parsed entry (the memory source carries the task id).
        var rawLearnings = "Build failed: fatal error in the middleware pipeline";

        // Act
        var entries = LearningsService.ParseRawLearnings(rawLearnings);

        // Assert
        entries.Should().NotBeEmpty();
        entries.Should().OnlyContain(e => e.Tags != null);
        entries[0].Category.Should().Be(LearningCategory.ErrorPattern);
        entries[0].Severity.Should().Be(LearningSeverity.Critical);
        entries[0].Tags.Should().Contain("middleware");
    }

    [Fact]
    public void ParseRawLearnings_ShouldStripIterationMarkers()
    {
        // Arrange
        var rawLearnings = "[Iteration 3] Error in DbContext configuration for PostgreSQL arrays";
        var sourceTaskId = Guid.NewGuid();

        // Act
        var entries = LearningsService.ParseRawLearnings(rawLearnings);

        // Assert
        entries.Should().NotBeEmpty();
        entries[0].Pattern.Should().NotContain("[Iteration");
    }

    #endregion

    #region ClassifyLine

    [Theory]
    [InlineData("Error: CS1061 type does not contain definition")]
    [InlineData("⚠ 3 errors encountered")]
    [InlineData("Exception thrown during migration")]
    [InlineData("Build failed with timeout")]
    public void ClassifyLine_WithErrorIndicators_ShouldReturnErrorPattern(string line)
    {
        // Act
        var category = LearningsService.ClassifyLine(line);

        // Assert
        category.Should().Be(LearningCategory.ErrorPattern);
    }

    [Theory]
    [InlineData("✓ Success achieved at iteration 5")]
    [InlineData("Task completed successfully")]
    [InlineData("Issue resolved by adding namespace")]
    [InlineData("Fixed by updating configuration")]
    public void ClassifyLine_WithSuccessIndicators_ShouldReturnSuccessPattern(string line)
    {
        // Act
        var category = LearningsService.ClassifyLine(line);

        // Assert
        category.Should().Be(LearningCategory.SuccessPattern);
    }

    [Theory]
    [InlineData("Use pipeline middleware pattern")]
    [InlineData("Architecture layer separation needed")]
    [InlineData("Design the repository structure")]
    public void ClassifyLine_WithArchitectureIndicators_ShouldReturnArchitectureDecision(string line)
    {
        // Act
        var category = LearningsService.ClassifyLine(line);

        // Assert
        category.Should().Be(LearningCategory.ArchitectureDecision);
    }

    [Theory]
    [InlineData("Add ZLinq package reference")]
    [InlineData("Library version mismatch detected")]
    [InlineData("Update dependency to latest")]
    public void ClassifyLine_WithDependencyIndicators_ShouldReturnDependencyInfo(string line)
    {
        // Act
        var category = LearningsService.ClassifyLine(line);

        // Assert
        category.Should().Be(LearningCategory.DependencyInfo);
    }

    [Fact]
    public void ClassifyLine_WithNoIndicators_ShouldReturnCodeConvention()
    {
        // Act
        var category = LearningsService.ClassifyLine("Use readonly struct for value types");

        // Assert
        category.Should().Be(LearningCategory.CodeConvention);
    }

    #endregion

    #region DetermineSeverity

    [Theory]
    [InlineData("Build failed with CS1061")]
    [InlineData("Compilation error in middleware")]
    [InlineData("Fatal crash during execution")]
    public void DetermineSeverity_WithCriticalIndicators_ShouldReturnCritical(string line)
    {
        // Act
        var severity = LearningsService.DetermineSeverity(line, LearningCategory.ErrorPattern);

        // Assert
        severity.Should().Be(LearningSeverity.Critical);
    }

    [Fact]
    public void DetermineSeverity_WithErrorCategory_ShouldReturnHigh()
    {
        // Act
        var severity = LearningsService.DetermineSeverity(
            "Some error occurred", LearningCategory.ErrorPattern);

        // Assert
        severity.Should().Be(LearningSeverity.High);
    }

    [Theory]
    [InlineData(LearningCategory.SuccessPattern)]
    [InlineData(LearningCategory.ArchitectureDecision)]
    public void DetermineSeverity_WithMediumCategories_ShouldReturnMedium(LearningCategory category)
    {
        // Act
        var severity = LearningsService.DetermineSeverity("Some learning", category);

        // Assert
        severity.Should().Be(LearningSeverity.Medium);
    }

    [Fact]
    public void DetermineSeverity_WithLowCategory_ShouldReturnLow()
    {
        // Act
        var severity = LearningsService.DetermineSeverity(
            "Some convention", LearningCategory.DependencyInfo);

        // Assert
        severity.Should().Be(LearningSeverity.Low);
    }

    #endregion

    #region ExtractTags

    [Theory]
    [InlineData("Use EF Core migrations for PostgreSQL", new[] { "ef core", "postgresql", "migration" })]
    [InlineData("Configure Blazor middleware pipeline", new[] { "blazor", "middleware", "pipeline" })]
    [InlineData("Register service in DI container", new[] { "service" })]
    public void ExtractTags_ShouldExtractKnownKeywords(string line, string[] expectedTags)
    {
        // Act
        var tags = LearningsService.ExtractTags(line);

        // Assert
        foreach (var expected in expectedTags)
        {
            tags.Should().Contain(expected);
        }
    }

    [Fact]
    public void ExtractTags_WithNoKeywords_ShouldReturnEmptyList()
    {
        // Act
        var tags = LearningsService.ExtractTags("Some random text with no keywords");

        // Assert
        tags.Should().BeEmpty();
    }

    #endregion
}
