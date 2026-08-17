using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Unit tests for WorkspaceOrchestrator.
///     Tests workspace preparation, finalization, branch naming, and error handling.
/// </summary>
public class WorkspaceOrchestratorTests
{
    private readonly IGitRepositoryManager _gitManager;
    private readonly ILogger<WorkspaceOrchestrator> _logger;
    private readonly IPullRequestFactory _prFactory;
    private readonly IProjectRepository _projectRepository;
    private readonly WorkspaceOrchestrator _orchestrator;

    public WorkspaceOrchestratorTests()
    {
        _projectRepository = Substitute.For<IProjectRepository>();
        _gitManager = Substitute.For<IGitRepositoryManager>();
        _prFactory = Substitute.For<IPullRequestFactory>();
        _logger = Substitute.For<ILogger<WorkspaceOrchestrator>>();
        _orchestrator = new WorkspaceOrchestrator(_projectRepository, _gitManager, _prFactory, _logger);
    }

    #region PrepareWorkspaceAsync - Success

    [Fact]
    public async Task PrepareWorkspaceAsync_WithValidProject_ShouldReturnWorkspaceInfo()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Description", "1.0",
            "https://github.com/org/repo", "main").Value;
        SetupProjectExists(projectId, project);
        SetupCloneSuccess("/tmp/workspace");
        SetupBranchCreationSuccess();

        // Act
        var result = await _orchestrator.PrepareWorkspaceAsync(projectId, taskId, "My Task", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.WorkspacePath.Should().Be("/tmp/workspace");
        result.Value.BaseBranch.Should().Be("main");
        result.Value.RepositoryUrl.Should().Be("https://github.com/org/repo");
        result.Value.ProjectId.Should().Be(projectId);
        result.Value.TaskTitle.Should().Be("My Task");
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_ShouldUseProjectDefaultBranch()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Description", "1.0",
            "https://github.com/org/repo", "develop").Value;
        SetupProjectExists(projectId, project);
        SetupCloneSuccess("/tmp/workspace");
        SetupBranchCreationSuccess();

        // Act
        var result = await _orchestrator.PrepareWorkspaceAsync(
            projectId, Guid.NewGuid(), "Task", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.BaseBranch.Should().Be("develop");
        await _gitManager.Received(1).CloneRepositoryAsync(
            "https://github.com/org/repo", "develop", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_WithEmptyDefaultBranch_ShouldDefaultToMain()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Test Project", "Description", "1.0",
            "https://github.com/org/repo").Value;
        SetupProjectExists(projectId, project);
        SetupCloneSuccess("/tmp/workspace");
        SetupBranchCreationSuccess();

        // Act
        var result = await _orchestrator.PrepareWorkspaceAsync(
            projectId, Guid.NewGuid(), "Task", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.BaseBranch.Should().Be("main");
    }

    #endregion

    #region PrepareWorkspaceAsync - Failures

    [Fact]
    public async Task PrepareWorkspaceAsync_WithEmptyProjectId_ShouldReturnFailure()
    {
        // Act
        var result = await _orchestrator.PrepareWorkspaceAsync(
            Guid.Empty, Guid.NewGuid(), "Task", CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project ID is required");
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_ProjectNotFound_ShouldReturnFailure()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _projectRepository.GetByIdAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Project>("Not found"));

        // Act
        var result = await _orchestrator.PrepareWorkspaceAsync(
            projectId, Guid.NewGuid(), "Task", CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project not found");
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_NoRepositoryUrl_ShouldReturnFailure()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description").Value;
        SetupProjectExists(projectId, project);

        // Act
        var result = await _orchestrator.PrepareWorkspaceAsync(
            projectId, Guid.NewGuid(), "Task", CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no repository URL");
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_CloneFails_ShouldReturnFailure()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description", "1.0",
            "https://github.com/org/repo").Value;
        SetupProjectExists(projectId, project);
        _gitManager.CloneRepositoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GitOperationContext>("Clone failed"));

        // Act
        var result = await _orchestrator.PrepareWorkspaceAsync(
            projectId, Guid.NewGuid(), "Task", CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Failed to clone");
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_BranchCreationFails_ShouldCleanupAndReturnFailure()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description", "1.0",
            "https://github.com/org/repo").Value;
        SetupProjectExists(projectId, project);
        SetupCloneSuccess("/tmp/workspace");
        _gitManager.CreateFeatureBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("Branch creation failed"));
        _gitManager.CleanupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _orchestrator.PrepareWorkspaceAsync(
            projectId, Guid.NewGuid(), "Task", CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Failed to create feature branch");
        await _gitManager.Received(1).CleanupAsync("/tmp/workspace", Arg.Any<CancellationToken>());
    }

    #endregion

    #region FinalizeWorkspaceAsync - Success

    [Fact]
    public async Task FinalizeWorkspaceAsync_WithPushOnCompletion_ShouldCommitAndPush()
    {
        // Arrange
        var workspace = CreateWorkspaceInfo();
        _gitManager.CommitChangesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _gitManager.PushBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _prFactory.CreatePullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PullRequestResult { WebUrl = "https://github.com/org/repo/pull/1" }));
        _gitManager.CleanupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _orchestrator.FinalizeWorkspaceAsync(workspace, true, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("https://github.com/org/repo/pull/1");
    }

    [Fact]
    public async Task FinalizeWorkspaceAsync_WithoutPush_ShouldOnlyCleanup()
    {
        // Arrange
        var workspace = CreateWorkspaceInfo();
        _gitManager.CleanupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _orchestrator.FinalizeWorkspaceAsync(workspace, false, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
        await _gitManager.DidNotReceive().CommitChangesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitManager.DidNotReceive().PushBranchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FinalizeWorkspaceAsync_WhenPushFails_ShouldStillCleanup()
    {
        // Arrange
        var workspace = CreateWorkspaceInfo();
        _gitManager.CommitChangesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _gitManager.PushBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Push failed"));
        _gitManager.CleanupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _orchestrator.FinalizeWorkspaceAsync(workspace, true, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull(); // No PR URL since push failed
        await _gitManager.Received(1).CleanupAsync(workspace.WorkspacePath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FinalizeWorkspaceAsync_WhenExceptionThrown_ShouldReturnFailure()
    {
        // Arrange
        var workspace = CreateWorkspaceInfo();
        _gitManager.CommitChangesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));

        // Act
        var result = await _orchestrator.FinalizeWorkspaceAsync(workspace, true, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Unexpected error");
    }

    #endregion

    #region BuildFeatureBranchName

    [Fact]
    public void BuildFeatureBranchName_ShouldCreateCorrectFormat()
    {
        // Arrange
        var taskId = new Guid(0xa1b2c3d4, 0xe5f6, 0x7890, 0xab, 0xcd, 0xef, 0x12, 0x34, 0x56, 0x78, 0x90);

        // Act
        var result = WorkspaceOrchestrator.BuildFeatureBranchName(taskId, "Implement Authentication");

        // Assert
        result.Should().StartWith("ralph/a1b2c3d4-");
        result.Should().Contain("implement-authentication");
    }

    [Fact]
    public void BuildFeatureBranchName_ShouldSanitizeSpecialCharacters()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // Act
        var result = WorkspaceOrchestrator.BuildFeatureBranchName(taskId, "Fix: Bug #123 (urgent!)");

        // Assert
        result.Should().NotContainAny(":", "#", "(", ")", "!");
        result.Should().Contain("fix-bug-123-urgent");
    }

    [Fact]
    public void BuildFeatureBranchName_WithEmptyTitle_ShouldUseTaskFallback()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // Act
        var result = WorkspaceOrchestrator.BuildFeatureBranchName(taskId, "");

        // Assert
        result.Should().Contain("task");
    }

    [Fact]
    public void BuildFeatureBranchName_WithLongTitle_ShouldTruncateTo80Chars()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var longTitle = new string('a', 200);

        // Act
        var result = WorkspaceOrchestrator.BuildFeatureBranchName(taskId, longTitle);

        // Assert
        result.Length.Should().BeLessOrEqualTo(80);
    }

    [Fact]
    public void BuildFeatureBranchName_ShouldNotEndWithHyphenOrSlash()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // Act
        var result = WorkspaceOrchestrator.BuildFeatureBranchName(taskId, "test-task-");

        // Assert
        result.Should().NotEndWith("-");
        result.Should().NotEndWith("/");
    }

    [Fact]
    public void BuildFeatureBranchName_ShouldCollapseMultipleHyphens()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // Act
        var result = WorkspaceOrchestrator.BuildFeatureBranchName(taskId, "fix   multiple   spaces");

        // Assert
        result.Should().NotContain("--");
    }

    #endregion

    #region Helpers

    private void SetupProjectExists(Guid projectId, Project project)
    {
        _projectRepository.GetByIdAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(project));
    }

    private void SetupCloneSuccess(string workspacePath)
    {
        _gitManager.CloneRepositoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GitOperationContext { LocalWorkTreePath = workspacePath }));
    }

    private void SetupBranchCreationSuccess()
    {
        _gitManager.CreateFeatureBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Result.Success(callInfo.ArgAt<string>(1)));
    }

    private static WorkspaceInfo CreateWorkspaceInfo()
    {
        return new WorkspaceInfo(
            "/tmp/workspace",
            "ralph/a1b2c3d4-test-task",
            "main",
            "https://github.com/org/repo",
            Guid.NewGuid(),
            "Test Task");
    }

    #endregion
}
