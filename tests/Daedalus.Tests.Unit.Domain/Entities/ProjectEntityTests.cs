using System.Collections.ObjectModel;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for Project aggregate root.
/// </summary>
public class ProjectEntityTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var projectName = "Test Project";
        var description = "A test project";
        var version = "1.0";

        // Act
        var result = Project.Create(projectId, projectName, description, version);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.Id.Should().Be(projectId);
        result.Value.ProjectName.Should().Be(projectName);
        result.Value.Description.Should().Be(description);
        result.Value.Version.Should().Be(version);
        result.Value.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.Value.ModifiedAt.Should().BeNull();
    }

    [Fact]
    public void Create_WithNullProjectName_ShouldFail()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, null!, "description");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project name");
    }

    [Fact]
    public void Create_WithEmptyProjectName_ShouldFail()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "", "description");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project name");
    }

    [Fact]
    public void Create_WithWhitespaceProjectName_ShouldFail()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "   ", "description");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project name");
    }

    [Fact]
    public void Create_WithNullDescription_ShouldFail()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "Project", null!);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Description");
    }

    [Fact]
    public void Create_WithEmptyDescription_ShouldFail()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "Project", "");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Description");
    }

    [Fact]
    public void Create_WithWhitespaceDescription_ShouldFail()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "Project", "   ");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Description");
    }

    [Fact]
    public void Create_WithNullVersion_ShouldFail()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "Project", "Description", null!);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Version");
    }

    [Fact]
    public void Create_WithEmptyVersion_ShouldFail()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "Project", "Description", "");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Version");
    }

    [Fact]
    public void Create_WithWhitespaceVersion_ShouldFail()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "Project", "Description", "   ");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Version");
    }

    [Fact]
    public void Create_WithValidParametersAndDefaultVersion_ShouldUseDefaultVersion()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "Project", "Description");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be("1.0");
    }

    [Fact]
    public void Create_TrimsInputs()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        // Act
        var result = Project.Create(projectId, "  Project Name  ", "  Description  ", "  1.0  ");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.ProjectName.Should().Be("Project Name");
        result.Value.Description.Should().Be("Description");
        result.Value.Version.Should().Be("1.0");
    }

    [Fact]
    public void AddTask_WithValidTask_ShouldSucceed()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();
        var task = DomainTestFactory.CreateTask();

        // Act
        var result = project.AddTask(task);

        // Assert
        result.IsSuccess.Should().BeTrue();
        project.Tasks.Should().ContainSingle();
        project.Tasks[0].Id.Should().Be(task.Id);
    }

    [Fact]
    public void AddTask_WithNullTask_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.AddTask(null);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("cannot be null");
    }

    [Fact]
    public void AddTask_WithDuplicateTaskId_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();
        var task = DomainTestFactory.CreateTask();
        project.AddTask(task);

        // Act
        var result = project.AddTask(task);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
    }

    [Fact]
    public void AddTask_MultipleValidTasks_ShouldSucceed()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();
        var task1 = DomainTestFactory.CreateTask();
        var task2 = DomainTestFactory.CreateTask();
        var task3 = DomainTestFactory.CreateTask();

        // Act
        var result1 = project.AddTask(task1);
        var result2 = project.AddTask(task2);
        var result3 = project.AddTask(task3);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result3.IsSuccess.Should().BeTrue();
        project.Tasks.Should().HaveCount(3);
    }

    [Fact]
    public void AddTask_UpdatesModifiedAt()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();
        var task = DomainTestFactory.CreateTask();

        // Act
        project.AddTask(task);

        // Assert
        project.ModifiedAt.Should().NotBeNull();
        project.ModifiedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Tasks_ReturnsReadOnlyList()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();
        var domainTask = DomainTestFactory.CreateTask();
        project.AddTask(domainTask);

        // Act
        var tasks = project.Tasks;

        // Assert
        tasks.Should().BeOfType<ReadOnlyCollection<DomainTask>>();
    }

    [Fact]
    public void Create_SetsCreatedAtToUtcNow()
    {
        // Arrange
        var beforeCreation = DateTime.UtcNow;

        // Act
        var result = Project.Create(Guid.NewGuid(), "Project", "Description");

        var afterCreation = DateTime.UtcNow;

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CreatedAt.Should().BeOnOrAfter(beforeCreation);
        result.Value.CreatedAt.Should().BeOnOrBefore(afterCreation);
    }

    [Fact]
    public void Create_InitializesEmptyTasksList()
    {
        // Arrange & Act
        var result = Project.Create(Guid.NewGuid(), "Project", "Description");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Tasks.Should().BeEmpty();
    }
}
