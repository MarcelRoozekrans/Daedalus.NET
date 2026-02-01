using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Unit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Unit tests for copilot instructions and .editorconfig loading
///     in FileSystemWorkspaceContextProvider.
/// </summary>
public class FileSystemWorkspaceContextProviderCopilotTests : UnitTestBase, IDisposable
{
    private readonly FileSystemWorkspaceContextProvider _provider;
    private readonly string _tempDir;

    public FileSystemWorkspaceContextProviderCopilotTests()
    {
        var logger = Substitute.For<ILogger<FileSystemWorkspaceContextProvider>>();
        _provider = new FileSystemWorkspaceContextProvider(logger);
        _tempDir = Path.Combine(Path.GetTempPath(), $"daedalus_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }

        GC.SuppressFinalize(this);
    }

    #region Copilot Instructions — Priority Order

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithMultipleCandidates_ShouldPickFirstMatch()
    {
        // Arrange — both .github/copilot-instructions.md and root copilot-instructions.md exist
        var githubDir = Path.Combine(_tempDir, ".github");
        Directory.CreateDirectory(githubDir);
        await File.WriteAllTextAsync(
            Path.Combine(githubDir, "copilot-instructions.md"),
            "GitHub directory version");
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "copilot-instructions.md"),
            "Root directory version");

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(_tempDir, ct: _cancellationToken);

        // Assert — .github path has higher priority
        result.IsSuccess.Should().BeTrue();
        result.Value.CopilotInstructions.Should().Be("GitHub directory version");
        result.Value.CopilotInstructionsPath.Should().Be(".github/copilot-instructions.md");
    }

    #endregion

    #region Copilot Instructions — Custom Paths

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithCustomPaths_ShouldUseCustomPaths()
    {
        // Arrange
        var docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(docsDir);
        var instructionsContent = "Custom location instructions.";
        await File.WriteAllTextAsync(
            Path.Combine(docsDir, "style-guide.md"),
            instructionsContent);

        IReadOnlyList<string> customPaths = ["docs/style-guide.md", ".github/copilot-instructions.md"];

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(
            _tempDir,
            copilotInstructionsPaths: customPaths,
            ct: _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CopilotInstructions.Should().Be(instructionsContent);
        result.Value.CopilotInstructionsPath.Should().Be("docs/style-guide.md");
    }

    #endregion

    #region Copilot Instructions — Not Found

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithNoCopilotInstructions_ShouldReturnNullFields()
    {
        // Arrange — empty workspace, no copilot instructions files

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(_tempDir, ct: _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CopilotInstructions.Should().BeNull();
        result.Value.CopilotInstructionsPath.Should().BeNull();
    }

    #endregion

    #region TotalCharactersLoaded

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithCopilotAndEditorConfig_ShouldCountInTotalCharacters()
    {
        // Arrange
        var githubDir = Path.Combine(_tempDir, ".github");
        Directory.CreateDirectory(githubDir);
        var copilotContent = "Use Result<T> everywhere.";
        var editorContent = "root = true";
        await File.WriteAllTextAsync(
            Path.Combine(githubDir, "copilot-instructions.md"),
            copilotContent);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, ".editorconfig"),
            editorContent);

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(_tempDir, ct: _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCharactersLoaded.Should()
            .BeGreaterThanOrEqualTo(copilotContent.Length + editorContent.Length);
    }

    #endregion

    #region Copilot Instructions — Default Path Discovery

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithGitHubCopilotInstructions_ShouldLoadContent()
    {
        // Arrange
        var githubDir = Path.Combine(_tempDir, ".github");
        Directory.CreateDirectory(githubDir);
        var instructionsContent = "# Coding Standards\nUse primary constructors. Use Result<T>.";
        await File.WriteAllTextAsync(
            Path.Combine(githubDir, "copilot-instructions.md"),
            instructionsContent);

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(_tempDir, ct: _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CopilotInstructions.Should().Be(instructionsContent);
        result.Value.CopilotInstructionsPath.Should().Be(".github/copilot-instructions.md");
    }

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithUnderscoreVariant_ShouldLoadContent()
    {
        // Arrange
        var githubDir = Path.Combine(_tempDir, ".github");
        Directory.CreateDirectory(githubDir);
        var instructionsContent = "Use 4-space indentation.";
        await File.WriteAllTextAsync(
            Path.Combine(githubDir, "copilot_instructions.md"),
            instructionsContent);

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(_tempDir, ct: _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CopilotInstructions.Should().Be(instructionsContent);
        result.Value.CopilotInstructionsPath.Should().Be(".github/copilot_instructions.md");
    }

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithRootLevelCopilotInstructions_ShouldLoadContent()
    {
        // Arrange
        var instructionsContent = "Follow clean architecture patterns.";
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "copilot-instructions.md"),
            instructionsContent);

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(_tempDir, ct: _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CopilotInstructions.Should().Be(instructionsContent);
        result.Value.CopilotInstructionsPath.Should().Be("copilot-instructions.md");
    }

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithCopilotMd_ShouldLoadContent()
    {
        // Arrange
        var instructionsContent = "Use ZLinq for hot paths.";
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "COPILOT.md"),
            instructionsContent);

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(_tempDir, ct: _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CopilotInstructions.Should().Be(instructionsContent);
        result.Value.CopilotInstructionsPath.Should().Be("COPILOT.md");
    }

    #endregion

    #region EditorConfig Loading

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithEditorConfig_ShouldLoadContent()
    {
        // Arrange
        var editorConfigContent = "root = true\n\n[*.cs]\nindent_size = 4\nindent_style = space";
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, ".editorconfig"),
            editorConfigContent);

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(_tempDir, ct: _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.EditorConfig.Should().Be(editorConfigContent);
    }

    [Fact]
    public async Task LoadWorkspaceContextAsync_WithoutEditorConfig_ShouldReturnNullEditorConfig()
    {
        // Arrange — no .editorconfig

        // Act
        var result = await _provider.LoadWorkspaceContextAsync(_tempDir, ct: _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.EditorConfig.Should().BeNull();
    }

    #endregion
}
