using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Unit tests for coding standards section integration in RalphPromptTemplateBuilder.
/// </summary>
public class RalphPromptTemplateBuilderCodingStandardsTests : UnitTestBase
{
    private readonly RalphPromptTemplateBuilder _builder;

    public RalphPromptTemplateBuilderCodingStandardsTests()
    {
        var logger = Substitute.For<ILogger<RalphPromptTemplateBuilder>>();
        _builder = new RalphPromptTemplateBuilder(logger);
    }

    #region IncludeCodingStandards Disabled

    [Fact]
    public void GetDefaultSections_WhenCodingStandardsDisabled_ShouldNotIncludeAnySection()
    {
        // Arrange
        var options = CreateOptionsWithCodingStandards(
            "Some style guide content.",
            editorConfig: "root = true",
            includeCodingStandards: false);

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotContain(s => s.Category == PromptSectionCategory.CodingStandards);
    }

    #endregion

    #region Helpers

    private static RalphPromptTemplateOptions CreateOptionsWithCodingStandards(
        string? copilotInstructions = null,
        string? copilotInstructionsPath = null,
        string? editorConfig = null,
        bool includeCodingStandards = true)
    {
        return new RalphPromptTemplateOptions
        {
            TaskPrompt = "Implement feature X",
            CompletionPromise = "TASK_COMPLETE",
            IncludeCodingStandards = includeCodingStandards,
            WorkspaceContext = new WorkspaceContext
            {
                CopilotInstructions = copilotInstructions,
                CopilotInstructionsPath = copilotInstructionsPath,
                EditorConfig = editorConfig
            }
        };
    }

    #endregion

    #region IncludeCodingStandards Enabled — CopilotInstructions Present

    [Fact]
    public void GetDefaultSections_WithCopilotInstructions_ShouldIncludeCodingStandardsSection()
    {
        // Arrange
        var options = CreateOptionsWithCodingStandards(
            "Use 4-space indentation. Always use primary constructors.",
            ".github/copilot-instructions.md");

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var sections = result.Value;

        sections.Should().Contain(s => s.Category == PromptSectionCategory.CodingStandards);

        var codingSection = sections.First(s =>
            s.Category == PromptSectionCategory.CodingStandards &&
            string.Equals(s.PriorityLabel, "0d", StringComparison.Ordinal));
        codingSection.Content.Should().Contain("CODING STANDARDS");
        codingSection.Content.Should().Contain("Use 4-space indentation");
        codingSection.Content.Should().Contain("copilot-instructions.md");
    }

    [Fact]
    public void GetDefaultSections_WithCopilotInstructions_ShouldPlaceBetweenSetupAndTask()
    {
        // Arrange
        var options = CreateOptionsWithCodingStandards(
            "Follow repo conventions.");

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var sections = result.Value;

        var setupSections = sections.Where(s => s.Category == PromptSectionCategory.Setup).ToList();
        var codingSections = sections.Where(s => s.Category == PromptSectionCategory.CodingStandards).ToList();
        var taskSections = sections.Where(s => s.Category == PromptSectionCategory.Task).ToList();

        setupSections.Should().NotBeEmpty();
        codingSections.Should().NotBeEmpty();
        taskSections.Should().NotBeEmpty();

        var maxSetupPriority = setupSections.Max(s => s.Priority);
        var minCodingPriority = codingSections.Min(s => s.Priority);
        var minTaskPriority = taskSections.Min(s => s.Priority);

        minCodingPriority.Should().BeGreaterThan(maxSetupPriority,
            "coding standards should come after setup sections");
        minCodingPriority.Should().BeLessThan(minTaskPriority,
            "coding standards should come before task section");
    }

    #endregion

    #region IncludeCodingStandards Enabled — EditorConfig Present

    [Fact]
    public void GetDefaultSections_WithEditorConfig_ShouldIncludeEditorConfigSection()
    {
        // Arrange
        var options = CreateOptionsWithCodingStandards(
            editorConfig: "root = true\n[*.cs]\nindent_size = 4");

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var sections = result.Value;

        var editorSections = sections
            .Where(s => s.Category == PromptSectionCategory.CodingStandards &&
                        string.Equals(s.PriorityLabel, "0e", StringComparison.Ordinal))
            .ToList();
        editorSections.Should().ContainSingle();
        editorSections[0].Content.Should().Contain("EDITOR CONFIGURATION");
        editorSections[0].Content.Should().Contain("indent_size = 4");
    }

    [Fact]
    public void GetDefaultSections_WithBothCopilotAndEditorConfig_ShouldIncludeBothSections()
    {
        // Arrange
        var options = CreateOptionsWithCodingStandards(
            "Use Result<T> for all service methods.",
            editorConfig: "root = true");

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var codingSections = result.Value
            .Where(s => s.Category == PromptSectionCategory.CodingStandards)
            .ToList();

        codingSections.Should().HaveCount(2);
        codingSections.Should().Contain(s => string.Equals(s.PriorityLabel, "0d", StringComparison.Ordinal));
        codingSections.Should().Contain(s => string.Equals(s.PriorityLabel, "0e", StringComparison.Ordinal));
    }

    #endregion

    #region Empty / Null Content

    [Fact]
    public void GetDefaultSections_WithNullWorkspaceContext_ShouldNotIncludeCodingStandards()
    {
        // Arrange
        var options = new RalphPromptTemplateOptions
        {
            TaskPrompt = "Implement feature X",
            CompletionPromise = "TASK_COMPLETE",
            WorkspaceContext = null,
            IncludeCodingStandards = true
        };

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotContain(s => s.Category == PromptSectionCategory.CodingStandards);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDefaultSections_WithEmptyCopilotInstructions_ShouldNotIncludeCodingSection(
        string? copilotInstructions)
    {
        // Arrange
        var options = CreateOptionsWithCodingStandards(copilotInstructions);

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotContain(s =>
            s.Category == PromptSectionCategory.CodingStandards &&
            string.Equals(s.PriorityLabel, "0d", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDefaultSections_WithEmptyEditorConfig_ShouldNotIncludeEditorConfigSection(
        string? editorConfig)
    {
        // Arrange
        var options = CreateOptionsWithCodingStandards(editorConfig: editorConfig);

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotContain(s =>
            s.Category == PromptSectionCategory.CodingStandards &&
            string.Equals(s.PriorityLabel, "0e", StringComparison.Ordinal));
    }

    #endregion

    #region Source Path Attribution

    [Fact]
    public void GetDefaultSections_WithCopilotPath_ShouldIncludePathInContent()
    {
        // Arrange
        var options = CreateOptionsWithCodingStandards(
            "Follow conventions.",
            ".github/copilot-instructions.md");

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var codingSection = result.Value.First(s => string.Equals(s.PriorityLabel, "0d", StringComparison.Ordinal));
        codingSection.Content.Should().Contain(".github/copilot-instructions.md");
    }

    [Fact]
    public void GetDefaultSections_WithoutCopilotPath_ShouldNotIncludePathAttribution()
    {
        // Arrange
        var options = CreateOptionsWithCodingStandards(
            "Follow conventions.",
            null);

        // Act
        var result = _builder.GetDefaultSections(options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var codingSection = result.Value.First(s => string.Equals(s.PriorityLabel, "0d", StringComparison.Ordinal));
        codingSection.Content.Should().NotContain("loaded from");
    }

    #endregion
}
