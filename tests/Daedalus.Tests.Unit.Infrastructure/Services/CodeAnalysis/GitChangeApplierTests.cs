using AwesomeAssertions;
using CSharpFunctionalExtensions;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Unit tests for GitChangeApplier.
///     Tests code block extraction from LLM responses using colon-path, header-path, and parenthesized formats.
/// </summary>
public class GitChangeApplierTests
{
    private readonly GitChangeApplier _applier;

    public GitChangeApplierTests()
    {
        var logger = Substitute.For<ILogger<GitChangeApplier>>();
        _applier = new GitChangeApplier(logger);
    }

    #region ExtractChangesAsync - Colon-Path Pattern

    [Fact]
    public async Task ExtractChangesAsync_ColonPathFormat_ShouldExtractFilePathAndCode()
    {
        // Arrange
        var response = """
            Here is the updated file:

            ```csharp:src/Services/MyService.cs
            public class MyService
            {
                public void DoWork() { }
            }
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].FilePath.Should().Be("src/Services/MyService.cs");
        result.Value[0].ModifiedCode.Should().Contain("public class MyService");
    }

    [Fact]
    public async Task ExtractChangesAsync_MultipleColonPathBlocks_ShouldExtractAll()
    {
        // Arrange
        var response = """
            ```csharp:src/File1.cs
            class File1 { }
            ```

            And another file:

            ```csharp:src/File2.cs
            class File2 { }
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].FilePath.Should().Be("src/File1.cs");
        result.Value[1].FilePath.Should().Be("src/File2.cs");
    }

    [Fact]
    public async Task ExtractChangesAsync_ColonPathWithDifferentLanguages_ShouldExtract()
    {
        // Arrange
        var response = """
            ```json:appsettings.json
            { "key": "value" }
            ```

            ```xml:Directory.Build.props
            <Project></Project>
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].FilePath.Should().Be("appsettings.json");
        result.Value[1].FilePath.Should().Be("Directory.Build.props");
    }

    #endregion

    #region ExtractChangesAsync - Header-Path Pattern

    [Fact]
    public async Task ExtractChangesAsync_BoldPathFormat_ShouldExtract()
    {
        // Arrange
        var response = """
            **src/Models/User.cs**
            ```csharp
            public class User { }
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].FilePath.Should().Be("src/Models/User.cs");
    }

    [Fact]
    public async Task ExtractChangesAsync_TickPathFormat_ShouldExtract()
    {
        // Arrange
        var response = """
            `src/Config/Settings.json`
            ```json
            { "setting": true }
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].FilePath.Should().Be("src/Config/Settings.json");
    }

    #endregion

    #region ExtractChangesAsync - Parenthesized-Path Pattern

    [Fact]
    public async Task ExtractChangesAsync_ParenPathInHeading_ShouldExtract()
    {
        // Arrange
        var response = """
            ### 1. Create the Program (src/HelloWorld/Program.cs)

            ```csharp
            Console.WriteLine("Hello World");
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].FilePath.Should().Be("src/HelloWorld/Program.cs");
    }

    #endregion

    #region ExtractChangesAsync - Deduplication

    [Fact]
    public async Task ExtractChangesAsync_DuplicateFilePaths_ShouldKeepFirstOccurrence()
    {
        // Arrange - colon-path and bold-path for same file
        var response = """
            ```csharp:src/File.cs
            // First version
            class File { }
            ```

            **src/File.cs**
            ```csharp
            // Second version
            class File { }
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].ModifiedCode.Should().Contain("First version");
    }

    #endregion

    #region ExtractChangesAsync - Edge Cases

    [Fact]
    public async Task ExtractChangesAsync_NoCodeBlocks_ShouldReturnEmptyList()
    {
        // Arrange
        var response = "This is just plain text with no code blocks.";

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractChangesAsync_CodeBlockWithoutPath_ShouldNotExtract()
    {
        // Arrange
        var response = """
            ```csharp
            public class NoPath { }
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractChangesAsync_WithBackslashPaths_ShouldNormalize()
    {
        // Arrange
        var response = """
            ```csharp:src\Services\MyService.cs
            public class MyService { }
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].FilePath.Should().Be("src/Services/MyService.cs");
    }

    [Fact]
    public async Task ExtractChangesAsync_WithLeadingSlash_ShouldTrim()
    {
        // Arrange
        var response = """
            ```csharp:/src/File.cs
            public class File { }
            ```
            """;

        // Act
        var result = await _applier.ExtractChangesAsync(response);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].FilePath.Should().Be("src/File.cs");
    }

    #endregion

    #region ApplyChangesAsync

    [Fact]
    public async Task ApplyChangesAsync_WorkTreeNotExists_ShouldReturnFailure()
    {
        // Arrange
        var changes = new List<Daedalus.Domain.CodeAnalysis.CodeModification>
        {
            new() { FilePath = "test.cs", ModifiedCode = "code" }
        };

        // Act
        var result = await _applier.ApplyChangesAsync("/nonexistent/path", changes.AsReadOnly());

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("does not exist");
    }

    [Fact]
    public async Task ApplyChangesAsync_WithEmptyFilePath_ShouldSkipChange()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var changes = new List<Daedalus.Domain.CodeAnalysis.CodeModification>
            {
                new() { FilePath = "", ModifiedCode = "code" }
            };

            // Act
            var result = await _applier.ApplyChangesAsync(tempDir, changes.AsReadOnly());

            // Assert
            result.IsSuccess.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ApplyChangesAsync_ShouldWriteFilesToWorkspace()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var changes = new List<Daedalus.Domain.CodeAnalysis.CodeModification>
            {
                new() { FilePath = "src/test.cs", ModifiedCode = "public class Test { }" }
            };

            // Act
            var result = await _applier.ApplyChangesAsync(tempDir, changes.AsReadOnly());

            // Assert
            result.IsSuccess.Should().BeTrue();
            var filePath = Path.Combine(tempDir, "src", "test.cs");
            File.Exists(filePath).Should().BeTrue();
            (await File.ReadAllTextAsync(filePath)).Should().Be("public class Test { }");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ApplyChangesAsync_ShouldCreateSubdirectories()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var changes = new List<Daedalus.Domain.CodeAnalysis.CodeModification>
            {
                new() { FilePath = "deep/nested/dir/file.cs", ModifiedCode = "content" }
            };

            // Act
            var result = await _applier.ApplyChangesAsync(tempDir, changes.AsReadOnly());

            // Assert
            result.IsSuccess.Should().BeTrue();
            Directory.Exists(Path.Combine(tempDir, "deep", "nested", "dir")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region RevertChangesAsync

    [Fact]
    public async Task RevertChangesAsync_WorkTreeNotExists_ShouldReturnFailure()
    {
        // Act
        var result = await _applier.RevertChangesAsync("/nonexistent/path", "main");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("does not exist");
    }

    [Fact]
    public async Task RevertChangesAsync_WithValidPath_ShouldReturnSuccess()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Act
            var result = await _applier.RevertChangesAsync(tempDir, "main");

            // Assert
            result.IsSuccess.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    #endregion
}
