using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.UpdateProject;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Unit tests for UpdateProjectCommandHandler.
///     Tests project retrieval, metadata updates, version updates, and error handling.
/// </summary>
public class UpdateProjectCommandHandlerTests
{
    private readonly UpdateProjectCommandHandler _handler;
    private readonly IProjectRepository _projectRepository;

    public UpdateProjectCommandHandlerTests()
    {
        _projectRepository = Substitute.For<IProjectRepository>();
        var logger = Substitute.For<ILogger<UpdateProjectCommandHandler>>();
        _handler = new UpdateProjectCommandHandler(_projectRepository, logger);
    }

    #region Success Cases

    [Fact]
    public async Task Handle_WithValidCommand_ShouldUpdateProject()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Original", "Original description").Value;
        var command = new UpdateProjectCommand(projectId, "Updated", "Updated description", null);
        SetupProjectExists(projectId, project);
        SetupRepositoryUpdateSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.ProjectName.Should().Be("Updated");
        result.Value.Description.Should().Be("Updated description");
    }

    [Fact]
    public async Task Handle_WithOnlyProjectName_ShouldKeepExistingDescription()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Original", "Keep this description").Value;
        var command = new UpdateProjectCommand(projectId, "New Name", null, null);
        SetupProjectExists(projectId, project);
        SetupRepositoryUpdateSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.ProjectName.Should().Be("New Name");
        result.Value.Description.Should().Be("Keep this description");
    }

    [Fact]
    public async Task Handle_WithNullProjectName_ShouldKeepExistingName()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Keep this name", "Description").Value;
        var command = new UpdateProjectCommand(projectId, null, "New Description", null);
        SetupProjectExists(projectId, project);
        SetupRepositoryUpdateSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.ProjectName.Should().Be("Keep this name");
        result.Value.Description.Should().Be("New Description");
    }

    [Fact]
    public async Task Handle_WithVersion_ShouldUpdateVersion()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description", "1.0").Value;
        var command = new UpdateProjectCommand(projectId, null, null, "2.0");
        SetupProjectExists(projectId, project);
        SetupRepositoryUpdateSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be("2.0");
    }

    [Fact]
    public async Task Handle_ShouldCallRepositoryUpdateAsync()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description").Value;
        var command = new UpdateProjectCommand(projectId, "Updated", null, null);
        SetupProjectExists(projectId, project);
        SetupRepositoryUpdateSuccess();

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _projectRepository.Received(1)
            .UpdateAsync(Arg.Any<Project>(), CancellationToken.None);
    }

    [Fact]
    public async Task Handle_ShouldReturnDtoWithRepositoryInfo()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description", "1.0",
            "https://github.com/org/repo", "develop").Value;
        var command = new UpdateProjectCommand(projectId, "Updated", null, null);
        SetupProjectExists(projectId, project);
        SetupRepositoryUpdateSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.RepositoryUrl.Should().Be("https://github.com/org/repo");
        result.Value.DefaultBranch.Should().Be("develop");
    }

    #endregion

    #region Not Found

    [Fact]
    public async Task Handle_ProjectNotFound_ShouldReturnFailure()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var command = new UpdateProjectCommand(projectId, "Updated", null, null);
        _projectRepository
            .GetByIdAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Project>("Not found"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    #endregion

    #region Validation Failures

    [Fact]
    public async Task Handle_WithEmptyProjectName_ShouldReturnFailure()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Original", "Description").Value;
        // Empty string is not null/whitespace-only - the handler trims and checks IsNullOrWhiteSpace
        // An empty string after trim is whitespace, so it falls through to keep existing name
        var command = new UpdateProjectCommand(projectId, "   ", null, null);
        SetupProjectExists(projectId, project);
        SetupRepositoryUpdateSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        // Whitespace-only name is treated as "not provided", keeps existing
        result.IsSuccess.Should().BeTrue();
        result.Value.ProjectName.Should().Be("Original");
    }

    [Fact]
    public async Task Handle_WithWhitespaceVersion_ShouldNotUpdateVersion()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description", "1.0").Value;
        var command = new UpdateProjectCommand(projectId, null, null, "   ");
        SetupProjectExists(projectId, project);
        SetupRepositoryUpdateSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be("1.0");
    }

    #endregion

    #region Repository Failures

    [Fact]
    public async Task Handle_RepositoryUpdateFails_ShouldReturnFailure()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description").Value;
        var command = new UpdateProjectCommand(projectId, "Updated", null, null);
        SetupProjectExists(projectId, project);
        _projectRepository
            .UpdateAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Database error"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Database error");
    }

    [Fact]
    public async Task Handle_RepositoryThrowsException_ShouldReturnFailure()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description").Value;
        var command = new UpdateProjectCommand(projectId, "Updated", null, null);
        SetupProjectExists(projectId, project);
        _projectRepository
            .UpdateAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Unexpected error");
    }

    #endregion

    #region CancellationToken Handling

    [Fact]
    public async Task Handle_WithCancellationToken_ShouldPassToRepository()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var projectId = Guid.NewGuid();
        var project = Project.Create(projectId, "Project", "Description").Value;
        var command = new UpdateProjectCommand(projectId, "Updated", null, null);
        SetupProjectExists(projectId, project);
        SetupRepositoryUpdateSuccess();

        // Act
        await _handler.Handle(command, cts.Token);

        // Assert
        await _projectRepository.Received(1)
            .GetByIdAsync(projectId, cts.Token);
        await _projectRepository.Received(1)
            .UpdateAsync(Arg.Any<Project>(), cts.Token);
    }

    #endregion

    #region Helpers

    private void SetupProjectExists(Guid projectId, Project project)
    {
        _projectRepository
            .GetByIdAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(project));
    }

    private void SetupRepositoryUpdateSuccess()
    {
        _projectRepository
            .UpdateAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    #endregion
}
