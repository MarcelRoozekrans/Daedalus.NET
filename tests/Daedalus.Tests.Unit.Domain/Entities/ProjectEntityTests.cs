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

    #region Create with RepositoryUrl

    [Fact]
    public void Create_WithRepositoryUrl_ShouldSetUrl()
    {
        // Act
        var result = Project.Create(Guid.NewGuid(), "Project", "Description", "1.0",
            "https://github.com/org/repo");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.RepositoryUrl.Should().Be("https://github.com/org/repo");
    }

    [Fact]
    public void Create_WithDefaultBranch_ShouldSetBranch()
    {
        // Act
        var result = Project.Create(Guid.NewGuid(), "Project", "Description", "1.0",
            "https://github.com/org/repo", "develop");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.DefaultBranch.Should().Be("develop");
    }

    [Fact]
    public void Create_WithNullRepositoryUrl_ShouldDefaultToEmpty()
    {
        // Act
        var result = Project.Create(Guid.NewGuid(), "Project", "Description");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.RepositoryUrl.Should().BeEmpty();
    }

    [Fact]
    public void Create_WithNullDefaultBranch_ShouldDefaultToMain()
    {
        // Act
        var result = Project.Create(Guid.NewGuid(), "Project", "Description");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.DefaultBranch.Should().Be("main");
    }

    [Fact]
    public void Create_TrimsRepositoryUrl()
    {
        // Act
        var result = Project.Create(Guid.NewGuid(), "Project", "Description", "1.0",
            "  https://github.com/org/repo  ");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.RepositoryUrl.Should().Be("https://github.com/org/repo");
    }

    [Fact]
    public void Create_TrimsDefaultBranch()
    {
        // Act
        var result = Project.Create(Guid.NewGuid(), "Project", "Description", "1.0",
            "https://github.com/org/repo", "  develop  ");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.DefaultBranch.Should().Be("develop");
    }

    #endregion

    #region Create with Length Validation

    [Fact]
    public void Create_WithProjectNameExceeding256Chars_ShouldFail()
    {
        // Act
        var result = Project.Create(Guid.NewGuid(), new string('a', 257), "Description");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("256");
    }

    [Fact]
    public void Create_WithDescriptionExceeding2000Chars_ShouldFail()
    {
        // Act
        var result = Project.Create(Guid.NewGuid(), "Project", new string('a', 2001));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("2000");
    }

    [Fact]
    public void Create_WithVersionExceeding50Chars_ShouldFail()
    {
        // Act
        var result = Project.Create(Guid.NewGuid(), "Project", "Description", new string('a', 51));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("50");
    }

    #endregion

    #region UpdateVersion

    [Fact]
    public void UpdateVersion_WithValidVersion_ShouldSucceed()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateVersion("2.0");

        // Assert
        result.IsSuccess.Should().BeTrue();
        project.Version.Should().Be("2.0");
    }

    [Fact]
    public void UpdateVersion_WithNullVersion_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateVersion(null!);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Version");
    }

    [Fact]
    public void UpdateVersion_WithEmptyVersion_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateVersion("");

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void UpdateVersion_WithWhitespaceVersion_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateVersion("   ");

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void UpdateVersion_WithVersionExceeding50Chars_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateVersion(new string('x', 51));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("50");
    }

    [Fact]
    public void UpdateVersion_TrimsVersion()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        project.UpdateVersion("  2.0  ");

        // Assert
        project.Version.Should().Be("2.0");
    }

    [Fact]
    public void UpdateVersion_UpdatesModifiedAt()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        project.UpdateVersion("2.0");

        // Assert
        project.ModifiedAt.Should().NotBeNull();
        project.ModifiedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    #endregion

    #region UpdateMetadata

    [Fact]
    public void UpdateMetadata_WithValidData_ShouldSucceed()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateMetadata("New Name", "New Description");

        // Assert
        result.IsSuccess.Should().BeTrue();
        project.ProjectName.Should().Be("New Name");
        project.Description.Should().Be("New Description");
    }

    [Fact]
    public void UpdateMetadata_WithNullProjectName_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateMetadata(null!, "Description");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project name");
    }

    [Fact]
    public void UpdateMetadata_WithEmptyProjectName_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateMetadata("", "Description");

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void UpdateMetadata_WithNullDescription_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateMetadata("Name", null!);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Description");
    }

    [Fact]
    public void UpdateMetadata_WithProjectNameExceeding256Chars_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateMetadata(new string('a', 257), "Description");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("256");
    }

    [Fact]
    public void UpdateMetadata_WithDescriptionExceeding2000Chars_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateMetadata("Name", new string('a', 2001));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("2000");
    }

    [Fact]
    public void UpdateMetadata_TrimsInputs()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        project.UpdateMetadata("  Trimmed Name  ", "  Trimmed Description  ");

        // Assert
        project.ProjectName.Should().Be("Trimmed Name");
        project.Description.Should().Be("Trimmed Description");
    }

    [Fact]
    public void UpdateMetadata_UpdatesModifiedAt()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        project.UpdateMetadata("Name", "Description");

        // Assert
        project.ModifiedAt.Should().NotBeNull();
        project.ModifiedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    #endregion

    #region UpdateRepositoryUrl

    [Fact]
    public void UpdateRepositoryUrl_WithValidUrl_ShouldSucceed()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateRepositoryUrl("https://github.com/org/repo");

        // Assert
        result.IsSuccess.Should().BeTrue();
        project.RepositoryUrl.Should().Be("https://github.com/org/repo");
    }

    [Fact]
    public void UpdateRepositoryUrl_WithDefaultBranch_ShouldUpdateBranch()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateRepositoryUrl("https://github.com/org/repo", "develop");

        // Assert
        result.IsSuccess.Should().BeTrue();
        project.DefaultBranch.Should().Be("develop");
    }

    [Fact]
    public void UpdateRepositoryUrl_WithNullDefaultBranch_ShouldKeepExisting()
    {
        // Arrange
        var project = Project.Create(Guid.NewGuid(), "P", "D", "1.0", "https://old.url", "custom").Value;

        // Act
        var result = project.UpdateRepositoryUrl("https://new.url");

        // Assert
        result.IsSuccess.Should().BeTrue();
        project.RepositoryUrl.Should().Be("https://new.url");
        project.DefaultBranch.Should().Be("custom"); // Unchanged
    }

    [Fact]
    public void UpdateRepositoryUrl_WithUrlExceeding2000Chars_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateRepositoryUrl(new string('x', 2001));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("2000");
    }

    [Fact]
    public void UpdateRepositoryUrl_WithBranchExceeding100Chars_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.UpdateRepositoryUrl("https://github.com/org/repo", new string('x', 101));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("100");
    }

    [Fact]
    public void UpdateRepositoryUrl_TrimsUrl()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        project.UpdateRepositoryUrl("  https://github.com/org/repo  ");

        // Assert
        project.RepositoryUrl.Should().Be("https://github.com/org/repo");
    }

    [Fact]
    public void UpdateRepositoryUrl_TrimsBranch()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        project.UpdateRepositoryUrl("https://github.com/org/repo", "  develop  ");

        // Assert
        project.DefaultBranch.Should().Be("develop");
    }

    [Fact]
    public void UpdateRepositoryUrl_UpdatesModifiedAt()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        project.UpdateRepositoryUrl("https://github.com/org/repo");

        // Assert
        project.ModifiedAt.Should().NotBeNull();
        project.ModifiedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    #endregion

    #region RemoveTask

    [Fact]
    public void RemoveTask_WithValidTaskId_ShouldSucceed()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();
        var task = DomainTestFactory.CreateTask(taskId: "TASK-001");
        project.AddTask(task);

        // Act
        var result = project.RemoveTask("TASK-001");

        // Assert
        result.IsSuccess.Should().BeTrue();
        project.Tasks.Should().BeEmpty();
    }

    [Fact]
    public void RemoveTask_WithNonExistentTaskId_ShouldFail()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();

        // Act
        var result = project.RemoveTask("TASK-999");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public void RemoveTask_UpdatesModifiedAt()
    {
        // Arrange
        var project = DomainTestFactory.CreateProject();
        var task = DomainTestFactory.CreateTask(taskId: "TASK-001");
        project.AddTask(task);

        // Act
        project.RemoveTask("TASK-001");

        // Assert
        project.ModifiedAt.Should().NotBeNull();
    }

    #endregion
}
