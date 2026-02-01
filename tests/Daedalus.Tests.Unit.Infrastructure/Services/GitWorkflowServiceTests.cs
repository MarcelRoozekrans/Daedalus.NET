using System.Diagnostics;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Unit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Integration tests for <see cref="GitWorkflowService" />.
///     Uses real git repositories created in temp directories.
/// </summary>
public class GitWorkflowServiceTests : UnitTestBase, IAsyncLifetime
{
    private readonly GitWorkflowService _sut;
    private readonly string _tempDir;
    private bool _gitAvailable;

    public GitWorkflowServiceTests()
    {
        var logger = Substitute.For<ILogger<GitWorkflowService>>();
        _sut = new GitWorkflowService(logger);
        _tempDir = Path.Combine(Path.GetTempPath(), $"daedalus_git_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public async Task InitializeAsync()
    {
        _gitAvailable = await IsGitAvailableAsync();

        if (_gitAvailable)
        {
            await RunAsync("git", "init", _tempDir);
            await RunAsync("git", "config user.email \"test@example.com\"", _tempDir);
            await RunAsync("git", "config user.name \"Test User\"", _tempDir);

            // Create an initial commit so HEAD exists
            var filePath = Path.Combine(_tempDir, "README.md");
            await File.WriteAllTextAsync(filePath, "# Test Repo");
            await RunAsync("git", "add -A", _tempDir);
            await RunAsync("git", "commit -m \"Initial commit\"", _tempDir);
        }
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            // Git creates read-only files in .git — remove read-only attribute first
            foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_tempDir, true);
        }

        return Task.CompletedTask;
    }

    #region IncrementPatchVersion (Pure Logic — No Git Required)

    [Theory]
    [InlineData(null, "0.0.1")]
    [InlineData("", "0.0.1")]
    [InlineData("invalid", "0.0.1")]
    [InlineData("0.0.1", "0.0.2")]
    [InlineData("0.0.9", "0.0.10")]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("v1.2.3", "1.2.4")]
    [InlineData("v0.0.0", "0.0.1")]
    [InlineData("10.20.30", "10.20.31")]
    public void IncrementPatchVersion_ShouldReturnExpectedVersion(string? currentTag, string expected)
    {
        // Act
        var result = GitWorkflowService.IncrementPatchVersion(currentTag);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region Integration Workflow

    [Fact]
    public async Task Workflow_CommitThenTag_ShouldWorkEndToEnd()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Create file and commit
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "feature.cs"), "public class Feature {}");
        var commitResult = await _sut.CommitAfterSuccessAsync(
            _tempDir, "task-789", 1, "Added feature", _cancellationToken);
        commitResult.IsSuccess.Should().BeTrue();

        // Tag the completion
        var tagResult = await _sut.TagOnCompletionAsync(_tempDir, "task-789", _cancellationToken);
        tagResult.IsSuccess.Should().BeTrue();
        tagResult.Value.Should().Be("0.0.1");

        // Verify tag exists
        var latestTag = await _sut.GetLatestTagAsync(_tempDir, _cancellationToken);
        latestTag.Value.Should().Be("0.0.1");

        // Make more changes and commit
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "feature2.cs"), "public class Feature2 {}");
        var commit2 = await _sut.CommitAfterSuccessAsync(
            _tempDir, "task-790", 1, "Added feature 2", _cancellationToken);
        commit2.IsSuccess.Should().BeTrue();

        // Tag again — should increment
        var tag2 = await _sut.TagOnCompletionAsync(_tempDir, "task-790", _cancellationToken);
        tag2.IsSuccess.Should().BeTrue();
        tag2.Value.Should().Be("0.0.2");
    }

    #endregion

    #region CommitAfterSuccessAsync

    [Fact]
    public async Task CommitAfterSuccessAsync_WithChanges_ShouldCreateCommit()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Arrange — create a file change
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "test.txt"), "new file");

        // Act
        var result = await _sut.CommitAfterSuccessAsync(
            _tempDir, "task-123", 1, "Fixed bug", _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe("unknown");
        result.Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CommitAfterSuccessAsync_WithNoChanges_ShouldReturnNoChanges()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Act — no file changes since initial commit
        var result = await _sut.CommitAfterSuccessAsync(
            _tempDir, "task-123", 1, "No changes", _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("no-changes");
    }

    [Fact]
    public async Task CommitAfterSuccessAsync_CommitMessageContainsRalphPrefix()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Arrange
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "content");

        // Act
        await _sut.CommitAfterSuccessAsync(
            _tempDir, "task-456", 2, "Added feature", _cancellationToken);

        // Assert — verify commit message via git log
        var logResult = await RunAsync("git", "log -1 --pretty=%s", _tempDir);
        logResult.Should().Contain("[ralph]");
        logResult.Should().Contain("task-456");
        logResult.Should().Contain("iteration 2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CommitAfterSuccessAsync_WithEmptyWorkspacePath_ShouldReturnFailure(string? path)
    {
        // Act
        var result = await _sut.CommitAfterSuccessAsync(
            path!, "task-123", 1, "summary", _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Workspace path cannot be empty");
    }

    #endregion

    #region TagOnCompletionAsync

    [Fact]
    public async Task TagOnCompletionAsync_WhenNoTagsExist_ShouldCreate001()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Act
        var result = await _sut.TagOnCompletionAsync(_tempDir, "task-123", _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("0.0.1");
    }

    [Fact]
    public async Task TagOnCompletionAsync_WhenTagExists_ShouldIncrementPatch()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Arrange — create a tag manually
        await RunAsync("git", "tag -a 0.0.1 -m \"First tag\"", _tempDir);

        // Create a new commit so we can tag at a different point
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "second.txt"), "second");
        await RunAsync("git", "add -A", _tempDir);
        await RunAsync("git", "commit -m \"Second commit\"", _tempDir);

        // Act
        var result = await _sut.TagOnCompletionAsync(_tempDir, "task-456", _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("0.0.2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TagOnCompletionAsync_WithEmptyWorkspacePath_ShouldReturnFailure(string? path)
    {
        // Act
        var result = await _sut.TagOnCompletionAsync(path!, "task-123", _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    #endregion

    #region GetLatestTagAsync

    [Fact]
    public async Task GetLatestTagAsync_WhenNoTags_ShouldReturnNull()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Act
        var result = await _sut.GetLatestTagAsync(_tempDir, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestTagAsync_WhenTagExists_ShouldReturnTag()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Arrange
        await RunAsync("git", "tag -a v1.2.3 -m \"Test tag\"", _tempDir);

        // Act
        var result = await _sut.GetLatestTagAsync(_tempDir, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("v1.2.3");
    }

    #endregion

    #region ResetToLastGoodAsync

    [Fact]
    public async Task ResetToLastGoodAsync_ShouldRevertChanges()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Arrange — create a committed change, then an uncommitted change
        var filePath = Path.Combine(_tempDir, "tracked.txt");
        await File.WriteAllTextAsync(filePath, "original");
        await RunAsync("git", "add -A", _tempDir);
        await RunAsync("git", "commit -m \"Add tracked file\"", _tempDir);

        // Make uncommitted changes
        await File.WriteAllTextAsync(filePath, "modified");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "untracked.txt"), "untracked");

        // Act
        var result = await _sut.ResetToLastGoodAsync(_tempDir, null, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        (await File.ReadAllTextAsync(filePath)).Should().Be("original");
        File.Exists(Path.Combine(_tempDir, "untracked.txt")).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResetToLastGoodAsync_WithEmptyWorkspacePath_ShouldReturnFailure(string? path)
    {
        // Act
        var result = await _sut.ResetToLastGoodAsync(path!, null, _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    #endregion

    #region PushAsync

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PushAsync_WithEmptyWorkspacePath_ShouldReturnFailure(string? path)
    {
        // Act
        var result = await _sut.PushAsync(path!, _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task PushAsync_WithNoRemote_ShouldReturnFailure()
    {
        if (SkipIfGitUnavailable())
        {
            return;
        }

        // Act — no remote configured in temp repo
        var result = await _sut.PushAsync(_tempDir, _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Failed to push");
    }

    #endregion

    #region Helpers

    private bool SkipIfGitUnavailable()
    {
        // In xUnit 2.x we cannot skip dynamically, so we return early and assert nothing
        return !_gitAvailable;
    }

    private static async Task<bool> IsGitAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunAsync(string command, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Trim();
    }

    #endregion
}
