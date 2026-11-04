using System.Text;
using System.Text.Json;
using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Services.Git;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for the RepositoriesEndpoints handlers.
///     Uses mocked IRepositoryConfigurationRepository and IGitConnectionTester.
/// </summary>
public sealed class RepositoriesEndpointsIntegrationTests : IAsyncLifetime
{
    private readonly IRepositoryConfigurationRepository _repositoryMock =
        Substitute.For<IRepositoryConfigurationRepository>();

    private readonly IGitConnectionTester _connectionTesterMock = Substitute.For<IGitConnectionTester>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddDaedalusEndpoints().BuildServiceProvider();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _services.DisposeAsync();

    #region GetAll

    [Fact(Timeout = 5000)]
    public async Task GetAll_WithEntities_Returns200()
    {
        // Arrange
        var entities = new List<RepositoryConfiguration>
        {
            IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 1"),
            IntegrationTestFactory.CreateRepositoryConfiguration(name: "Repo 2")
        };
        _repositoryMock.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RepositoryConfiguration>>.Success(entities));

        // Act
        var result = await RepositoriesEndpoints.GetAllRepositories(
            CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer the InternalError problem in place of Ok in GetAllRepositories.
        var ok = result.Result.Should().BeOfType<Ok<IEnumerable<RepositoryConfigurationDto>>>().Subject;
        ok.StatusCode.Should().Be(200);
        // Red: answer an empty list in place of the mapped entities in GetAllRepositories.
        ok.Value!.Select(dto => dto.Name).Should().Equal("Repo 1", "Repo 2");
    }

    [Fact(Timeout = 5000)]
    public async Task GetAll_WithNoEntities_ReturnsEmptyList()
    {
        // Arrange
        _repositoryMock.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RepositoryConfiguration>>.Success(new List<RepositoryConfiguration>()));

        // Act
        var result = await RepositoriesEndpoints.GetAllRepositories(
            CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer the InternalError problem in place of Ok in GetAllRepositories.
        result.Result.Should().BeOfType<Ok<IEnumerable<RepositoryConfigurationDto>>>().Which.Value.Should().BeEmpty();
    }

    [Fact(Timeout = 5000)]
    public async Task GetAll_WhenRepositoryFails_Returns500()
    {
        // Arrange
        _repositoryMock.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RepositoryConfiguration>>.Failure("Database error"));

        // Act
        var result = await RepositoriesEndpoints.GetAllRepositories(
            CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer StatusCodes.Status503ServiceUnavailable in place of 500 in InternalError.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(500);
    }

    #endregion

    #region GetById

    [Fact(Timeout = 5000)]
    public async Task GetById_WithExistingId_Returns200()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Found Repo");
        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Success(entity));

        // Act
        var result = await RepositoriesEndpoints.GetRepositoryById(
            entity.Id, CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer the 404 problem in place of Ok in GetRepositoryById.
        var dto = result.Result.Should().BeOfType<Ok<RepositoryConfigurationDto>>().Subject.Value;
        dto.Should().NotBeNull();
        // Red: map Name from entity.Url in MapToDto.
        dto!.Name.Should().Be("Found Repo");
    }

    [Fact(Timeout = 5000)]
    public async Task GetById_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Failure("not found"));

        // Act
        var result = await RepositoriesEndpoints.GetRepositoryById(
            id, CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer ValidationError in place of NotFound in GetRepositoryById's failure branch.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(404);
    }

    #endregion

    #region Create

    [Fact(Timeout = 5000)]
    public async Task Create_WithValidDto_Returns201()
    {
        // Arrange
        var dto = new CreateRepositoryConfigurationDto
        {
            Name = "New Repo",
            Url = "https://github.com/org/new-repo",
            Platform = "GitHub",
            DefaultBranch = "main"
        };

        _repositoryMock.AddAsync(Arg.Any<RepositoryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<RepositoryConfiguration>.Success(callInfo.Arg<RepositoryConfiguration>()));

        // Act
        var result = await RepositoriesEndpoints.CreateRepository(
            await BindCreate(dto), CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer InternalError in place of Created in CreateRepository's success branch.
        var created = result.Result.Should().BeOfType<Created<RepositoryConfigurationDto>>().Subject;
        created.StatusCode.Should().Be(201);
        // Red: build the Location as /api/repositories/{id}, lower case, in CreateRepository.
        created.Location.Should().Be($"http://localhost/api/Repositories/{created.Value!.Id}");
        // Red: map Name from entity.Url in MapToDto.
        created.Value.Name.Should().Be("New Repo");
    }

    [Fact(Timeout = 5000)]
    public async Task Create_WithInvalidPlatform_Returns400()
    {
        // Arrange
        var dto = new CreateRepositoryConfigurationDto
        {
            Name = "Repo",
            Url = "https://example.com/repo",
            Platform = "InvalidPlatform"
        };

        // Act
        var result = await RepositoriesEndpoints.CreateRepository(
            await BindCreate(dto), CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer 422 in place of 400 in ValidationError.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact(Timeout = 5000)]
    public async Task Create_WhenSaveFails_Returns500()
    {
        // Arrange
        var dto = new CreateRepositoryConfigurationDto
        {
            Name = "Repo",
            Url = "https://github.com/org/repo",
            Platform = "GitHub"
        };

        _repositoryMock.AddAsync(Arg.Any<RepositoryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Failure("Database error"));

        // Act
        var result = await RepositoriesEndpoints.CreateRepository(
            await BindCreate(dto), CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer ValidationError in place of InternalError in CreateRepository's save branch.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(500);
    }

    #endregion

    #region Update

    [Fact(Timeout = 5000)]
    public async Task Update_WithValidDto_Returns204()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Original");
        var dto = new UpdateRepositoryConfigurationDto
        {
            Name = "Updated",
            Url = "https://github.com/org/repo",
            DefaultBranch = "main",
            IsActive = true
        };

        _repositoryMock.GetByIdTrackingAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Success(entity));
        _repositoryMock.UpdateAsync(entity, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await RepositoriesEndpoints.UpdateRepository(
            entity.Id, await BindUpdate(dto), CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer the NotFound problem in place of NoContent in UpdateRepository's success branch.
        result.Result.Should().BeOfType<NoContent>();
    }

    [Fact(Timeout = 5000)]
    public async Task Update_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        var dto = new UpdateRepositoryConfigurationDto
        {
            Name = "Updated",
            Url = "https://github.com/org/repo"
        };

        _repositoryMock.GetByIdTrackingAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Failure("not found"));

        // Act
        var result = await RepositoriesEndpoints.UpdateRepository(
            id, await BindUpdate(dto), CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer ValidationError in place of NotFound in UpdateRepository's lookup branch.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact(Timeout = 5000)]
    public async Task Update_WithConcurrencyConflict_Returns409()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Original");
        var dto = new UpdateRepositoryConfigurationDto
        {
            Name = "Updated",
            Url = "https://github.com/org/repo",
            DefaultBranch = "main",
            IsActive = true
        };

        _repositoryMock.GetByIdTrackingAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Success(entity));
        _repositoryMock.UpdateAsync(entity, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("The repository was modified by another operation"));

        // Act
        var result = await RepositoriesEndpoints.UpdateRepository(
            entity.Id, await BindUpdate(dto), CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: skip the conflict branch, so UpdateRepository answers InternalError for the "modified by another" error.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(409);
    }

    [Fact(Timeout = 5000)]
    public async Task Update_WithInvalidName_Returns400()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(name: "Original");
        var dto = new UpdateRepositoryConfigurationDto
        {
            Name = "",
            Url = "https://github.com/org/repo",
            DefaultBranch = "main",
            IsActive = true
        };

        _repositoryMock.GetByIdTrackingAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Success(entity));

        // Act
        var result = await RepositoriesEndpoints.UpdateRepository(
            entity.Id, await BindUpdate(dto), CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer InternalError in place of ValidationError in UpdateRepository's validation branch.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(400);
    }

    #endregion

    #region Delete

    [Fact(Timeout = 5000)]
    public async Task Delete_WithExistingId_Returns204()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.DeleteAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await RepositoriesEndpoints.DeleteRepository(
            id, CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer the NotFound problem in place of NoContent in DeleteRepository's success branch.
        result.Result.Should().BeOfType<NoContent>();
    }

    [Fact(Timeout = 5000)]
    public async Task Delete_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.DeleteAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Repository configuration not found"));

        // Act
        var result = await RepositoriesEndpoints.DeleteRepository(
            id, CreateContext(), _repositoryMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer InternalError in place of NotFound for a "not found" error in DeleteRepository.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(404);
    }

    #endregion

    #region TestConnection

    [Fact(Timeout = 5000)]
    public async Task TestConnection_WhenSuccessful_Returns200WithConnected()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(
            name: "Connectable", url: "https://github.com/org/repo");

        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Success(entity));
        _connectionTesterMock.TestConnectionAsync(
                "https://github.com/org/repo", null, null, Arg.Any<CancellationToken>())
            .Returns(Result<GitConnectionResult>.Success(new GitConnectionResult
            {
                Connected = true,
                Message = "Connection successful"
            }));

        // Act
        var result = await RepositoriesEndpoints.TestRepositoryConnection(
            entity.Id, CreateContext(), _repositoryMock, _connectionTesterMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer the 400 TestConnectionResponse in place of Ok in TestRepositoryConnection's success branch.
        var response = result.Result.Should().BeOfType<Ok<TestConnectionResponse>>().Subject.Value;
        response.Should().NotBeNull();
        // Red: Connected false in the success body of TestRepositoryConnection.
        response!.Connected.Should().BeTrue();
        // Red: a constant Message in place of the tester's message in the success body.
        response.Message.Should().Be("Connection successful");
    }

    [Fact(Timeout = 5000)]
    public async Task TestConnection_WhenFails_Returns400()
    {
        // Arrange
        var entity = IntegrationTestFactory.CreateRepositoryConfiguration(
            name: "Unreachable", url: "https://github.com/org/unreachable");

        _repositoryMock.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Success(entity));
        _connectionTesterMock.TestConnectionAsync(
                "https://github.com/org/unreachable", null, null, Arg.Any<CancellationToken>())
            .Returns(Result<GitConnectionResult>.Failure("Connection refused"));

        // Act
        var result = await RepositoriesEndpoints.TestRepositoryConnection(
            entity.Id, CreateContext(), _repositoryMock, _connectionTesterMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer 422 in place of 400 in TestRepositoryConnection's failure branch.
        var json = result.Result.Should().BeOfType<ApiJsonResult<TestConnectionResponse>>().Subject;
        json.StatusCode.Should().Be(400);
        // Red: Connected true in the failure body of TestRepositoryConnection.
        json.Value.Connected.Should().BeFalse();
    }

    [Fact(Timeout = 5000)]
    public async Task TestConnection_WithNonExistingId_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repositoryMock.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Failure("not found"));

        // Act
        var result = await RepositoriesEndpoints.TestRepositoryConnection(
            id, CreateContext(), _repositoryMock, _connectionTesterMock, _loggerFactory, CancellationToken.None);

        // Assert
        // Red: answer ValidationError in place of NotFound in TestRepositoryConnection's lookup branch.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(404);
    }

    #endregion

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private DefaultHttpContext CreateContext(string? body = null)
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
        }

        return context;
    }

    private Task<ApiBody<CreateRepositoryConfigurationDto>> BindCreate(CreateRepositoryConfigurationDto request) =>
        Bind(request);

    private Task<ApiBody<UpdateRepositoryConfigurationDto>> BindUpdate(UpdateRepositoryConfigurationDto request) =>
        Bind(request);

    /// <summary>Binds <paramref name="dto"/> as the request delegate binds the body, through its JSON text.</summary>
    private async Task<ApiBody<T>> Bind<T>(T dto)
        where T : class
    {
        var context = CreateContext(JsonSerializer.Serialize(dto, WebJson));
        var parameter = typeof(RepositoriesEndpoints).GetMethods().SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(ApiBody<T>));
        return await BindVia<ApiBody<T>>(context, parameter);
    }

    private static async Task<TSelf> BindVia<TSelf>(HttpContext context, System.Reflection.ParameterInfo parameter)
        where TSelf : class, IBindableFromHttpContext<TSelf> => (await TSelf.BindAsync(context, parameter))!;
}
