using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.CreateProject;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Commands;

/// <summary>
///     Unit tests for CreateProjectCommandHandler.
///     Tests command validation, project creation, repository operations, and DTO mapping.
/// </summary>
public class CreateProjectCommandHandlerTests
{
    private readonly CreateProjectCommandHandler _handler;
    private readonly IProjectRepository _projectRepository;

    public CreateProjectCommandHandlerTests()
    {
        _projectRepository = Substitute.For<IProjectRepository>();
        var logger = Substitute.For<ILogger<CreateProjectCommandHandler>>();
        _handler = new CreateProjectCommandHandler(_projectRepository, logger);
    }

    #region Success Cases

    [Fact]
    public async Task Handle_WithValidCommand_ShouldCreateProject()
    {
        // Arrange
        var command = new CreateProjectCommand("Test Project", "A test project", "1.0");
        SetupRepositoryAddSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.ProjectName.Should().Be("Test Project");
        result.Value.Description.Should().Be("A test project");
        result.Value.Version.Should().Be("1.0");
    }

    [Fact]
    public async Task Handle_WithRepositoryUrl_ShouldIncludeInDto()
    {
        // Arrange
        var command = new CreateProjectCommand(
            "Test Project", "Description", "1.0",
            "https://github.com/org/repo", "develop");
        SetupRepositoryAddSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.RepositoryUrl.Should().Be("https://github.com/org/repo");
        result.Value.DefaultBranch.Should().Be("develop");
    }

    [Fact]
    public async Task Handle_WithNullRepositoryUrl_ShouldDefaultToEmptyAndMain()
    {
        // Arrange
        var command = new CreateProjectCommand("Test Project", "Description", "1.0");
        SetupRepositoryAddSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.RepositoryUrl.Should().BeEmpty();
        result.Value.DefaultBranch.Should().Be("main");
    }

    [Fact]
    public async Task Handle_ShouldCallRepositoryAddAsync()
    {
        // Arrange
        var command = new CreateProjectCommand("Test Project", "Description", "1.0");
        SetupRepositoryAddSuccess();

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _projectRepository.Received(1)
            .AddAsync(Arg.Any<Project>(), CancellationToken.None);
    }

    [Fact]
    public async Task Handle_ShouldReturnDtoWithGeneratedId()
    {
        // Arrange
        var command = new CreateProjectCommand("Test Project", "Description", "1.0");
        SetupRepositoryAddSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Handle_ShouldReturnDtoWithEmptyTasksList()
    {
        // Arrange
        var command = new CreateProjectCommand("Test Project", "Description", "1.0");
        SetupRepositoryAddSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WithNullDescription_ShouldReturnFailure()
    {
        // Arrange - null description is mapped to string.Empty which fails domain validation
        var command = new CreateProjectCommand("Test Project", null!, "1.0");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        // The handler maps null to empty string, but domain rejects empty description
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Description");
    }

    [Fact]
    public async Task Handle_WithNullVersion_ShouldDefaultTo1_0()
    {
        // Arrange
        var command = new CreateProjectCommand("Test Project", "Description", null!);
        SetupRepositoryAddSuccess();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be("1.0");
    }

    #endregion

    #region Validation Failures

    [Fact]
    public async Task Handle_WithNullProjectName_ShouldReturnFailure()
    {
        // Arrange
        var command = new CreateProjectCommand(null!, "Description", "1.0");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project name");
    }

    [Fact]
    public async Task Handle_WithEmptyProjectName_ShouldReturnFailure()
    {
        // Arrange
        var command = new CreateProjectCommand("", "Description", "1.0");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project name");
    }

    [Fact]
    public async Task Handle_WithWhitespaceProjectName_ShouldReturnFailure()
    {
        // Arrange
        var command = new CreateProjectCommand("   ", "Description", "1.0");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project name");
    }

    #endregion

    #region Repository Failures

    [Fact]
    public async Task Handle_RepositoryAddFails_ShouldReturnFailure()
    {
        // Arrange
        var command = new CreateProjectCommand("Test Project", "Description", "1.0");
        _projectRepository
            .AddAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Project>("Database connection failed"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Database connection failed");
    }

    [Fact]
    public async Task Handle_RepositoryThrowsException_ShouldReturnFailure()
    {
        // Arrange
        var command = new CreateProjectCommand("Test Project", "Description", "1.0");
        _projectRepository
            .AddAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Repository error"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Repository error");
    }

    #endregion

    #region CancellationToken Handling

    [Fact]
    public async Task Handle_WithCancellationToken_ShouldPassToRepository()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var command = new CreateProjectCommand("Test Project", "Description", "1.0");
        SetupRepositoryAddSuccess();

        // Act
        await _handler.Handle(command, cts.Token);

        // Assert
        await _projectRepository.Received(1)
            .AddAsync(Arg.Any<Project>(), cts.Token);
    }

    #endregion

    #region Helpers

    private void SetupRepositoryAddSuccess()
    {
        _projectRepository
            .AddAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result.Success(callInfo.Arg<Project>()));
    }

    #endregion
}
