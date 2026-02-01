using Daedalus.Application.Abstractions;
using Daedalus.Application.Queries.GetAllTasks;

namespace Daedalus.Tests.Unit.Application.Queries;

/// <summary>
///     Tests for GetAllTasksQueryHandler.
/// </summary>
public class GetAllTasksQueryHandlerTests
{
    private readonly GetAllTasksQueryHandler _handler;
    private readonly ITaskRepository _taskRepository;

    public GetAllTasksQueryHandlerTests()
    {
        _taskRepository = Substitute.For<ITaskRepository>();
        _handler = new GetAllTasksQueryHandler(_taskRepository);
    }

    [Fact]
    public async Task Handle_WithValidQuery_ShouldReturnPaginatedTasks()
    {
        // Arrange
        var query = new GetAllTasksQuery();
        var tasks = new List<DomainTask>
        {
            ApplicationTestFactory.CreateTask(title: "Task 1"),
            ApplicationTestFactory.CreateTask(title: "Task 2"),
            ApplicationTestFactory.CreateTask(title: "Task 3")
        };

        _taskRepository
            .GetPendingCountAsync(Arg.Any<CancellationToken>())
            .Returns(3);

        _taskRepository
            .GetPendingAsync(0, 10, Arg.Any<CancellationToken>())
            .Returns(Result.Success((IReadOnlyList<DomainTask>)tasks));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(3);
        result.Value.Total.Should().Be(3);
        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(10);
    }

    [Fact]
    public async Task Handle_WithEmptyTaskList_ShouldReturnEmptyResult()
    {
        // Arrange
        var query = new GetAllTasksQuery();
        var emptyList = new List<DomainTask>();

        _taskRepository
            .GetPendingCountAsync(Arg.Any<CancellationToken>())
            .Returns(0);

        _taskRepository
            .GetPendingAsync(0, 10, Arg.Any<CancellationToken>())
            .Returns(Result.Success((IReadOnlyList<DomainTask>)emptyList));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.Total.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithInvalidPageNumber_ShouldReturnFailure()
    {
        // Arrange
        var query = new GetAllTasksQuery(0);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Page must be greater than 0");
    }

    [Fact]
    public async Task Handle_WithNegativePageNumber_ShouldReturnFailure()
    {
        // Arrange
        var query = new GetAllTasksQuery(-1);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Page must be greater than 0");
    }

    [Fact]
    public async Task Handle_WithInvalidPageSize_ZeroShouldReturnFailure()
    {
        // Arrange
        var query = new GetAllTasksQuery(1, 0);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("PageSize must be between 1 and 100");
    }

    [Fact]
    public async Task Handle_WithInvalidPageSize_NegativeShouldReturnFailure()
    {
        // Arrange
        var query = new GetAllTasksQuery(1, -5);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("PageSize must be between 1 and 100");
    }

    [Fact]
    public async Task Handle_WithPageSizeExceedsMax_ShouldReturnFailure()
    {
        // Arrange
        var query = new GetAllTasksQuery(1, 101);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("PageSize must be between 1 and 100");
    }

    [Fact]
    public async Task Handle_WithMultiplePages_ShouldPaginateCorrectly()
    {
        // Arrange
        var query = new GetAllTasksQuery(2, 5);
        var allTasks = Enumerable.Range(1, 12)
            .Select(i => ApplicationTestFactory.CreateTask())
            .ToList();
        var pageItems = allTasks.Skip(5).Take(5).ToList();

        _taskRepository
            .GetPendingCountAsync(Arg.Any<CancellationToken>())
            .Returns(12);

        _taskRepository
            .GetPendingAsync(5, 5, Arg.Any<CancellationToken>())
            .Returns(Result.Success((IReadOnlyList<DomainTask>)pageItems));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(5);
        result.Value.Total.Should().Be(12);
        result.Value.Page.Should().Be(2);
        result.Value.PageSize.Should().Be(5);
    }

    [Fact]
    public async Task Handle_WithLastPage_ShouldReturnRemainingItems()
    {
        // Arrange
        var query = new GetAllTasksQuery(3, 5);
        var allTasks = Enumerable.Range(1, 12)
            .Select(i => ApplicationTestFactory.CreateTask())
            .ToList();
        var pageItems = allTasks.Skip(10).Take(5).ToList();

        _taskRepository
            .GetPendingCountAsync(Arg.Any<CancellationToken>())
            .Returns(12);

        _taskRepository
            .GetPendingAsync(10, 5, Arg.Any<CancellationToken>())
            .Returns(Result.Success((IReadOnlyList<DomainTask>)pageItems));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.Total.Should().Be(12);
    }

    [Fact]
    public async Task Handle_WithRepositoryFailure_ShouldReturnFailure()
    {
        // Arrange
        var query = new GetAllTasksQuery();

        _taskRepository
            .GetPendingCountAsync(Arg.Any<CancellationToken>())
            .Returns(0);

        _taskRepository
            .GetPendingAsync(0, 10, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<DomainTask>>("Database error"));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Database error");
    }

    [Fact]
    public async Task Handle_PassesCancellationTokenToRepository()
    {
        // Arrange
        var query = new GetAllTasksQuery();
        var tasks = new List<DomainTask> { ApplicationTestFactory.CreateTask() };
        var cts = new CancellationTokenSource();

        _taskRepository
            .GetPendingCountAsync(cts.Token)
            .Returns(1);

        _taskRepository
            .GetPendingAsync(0, 10, cts.Token)
            .Returns(Result.Success((IReadOnlyList<DomainTask>)tasks));

        // Act
        var result = await _handler.Handle(query, cts.Token);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _taskRepository.Received(1).GetPendingCountAsync(cts.Token);
        await _taskRepository.Received(1).GetPendingAsync(0, 10, cts.Token);
    }

    [Fact]
    public async Task Handle_MapsTasks_ToTaskDtos()
    {
        // Arrange
        var query = new GetAllTasksQuery();
        var tasks = new List<DomainTask>
        {
            ApplicationTestFactory.CreateTask(title: "Feature 1", description: "Build feature 1"),
            ApplicationTestFactory.CreateTask(title: "Feature 2", description: "Build feature 2")
        };

        _taskRepository
            .GetPendingCountAsync(Arg.Any<CancellationToken>())
            .Returns(2);

        _taskRepository
            .GetPendingAsync(0, 10, Arg.Any<CancellationToken>())
            .Returns(Result.Success((IReadOnlyList<DomainTask>)tasks));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items[0].Prompt.Should().Be("Test prompt");
        result.Value.Items[1].Prompt.Should().Be("Test prompt");
    }

    [Fact]
    public async Task Handle_WithSinglePage_ReturnsCorrectMetadata()
    {
        // Arrange
        var query = new GetAllTasksQuery(1, 50);
        var tasks = Enumerable.Range(1, 10)
            .Select(i => ApplicationTestFactory.CreateTask())
            .ToList();

        _taskRepository
            .GetPendingCountAsync(Arg.Any<CancellationToken>())
            .Returns(10);

        _taskRepository
            .GetPendingAsync(0, 50, Arg.Any<CancellationToken>())
            .Returns(Result.Success((IReadOnlyList<DomainTask>)tasks));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(10);
        result.Value.Total.Should().Be(10);
        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(50);
    }

    [Fact]
    public async Task Handle_WithRepositoryException_ShouldThrow()
    {
        // Arrange
        var query = new GetAllTasksQuery();

        _taskRepository
            .GetPendingCountAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        // Act
        var act = async () => await _handler.Handle(query, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
