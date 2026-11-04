using System.Reflection;
using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Services.Git;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="RepositoriesEndpoints"/> answers what <c>RepositoriesController</c> answered: reads map the entity to
///     its DTO, a failed repository call is a hand-built <c>ProblemDetails</c> written as given, a created repository
///     is a 201 whose <c>Location</c> keeps MVC's <c>/api/Repositories</c> casing, a failed connection test is a 400
///     <c>TestConnectionResponse</c>, and each handler logs its start under the endpoints' category.
/// </summary>
public sealed class RepositoriesEndpointsTests
{
    private const string Category = "Daedalus.Api.Endpoints.RepositoriesEndpoints";

    private readonly IRepositoryConfigurationRepository _repository = Substitute.For<IRepositoryConfigurationRepository>();
    private readonly IGitConnectionTester _tester = Substitute.For<IGitConnectionTester>();

    [Fact]
    public async Task GetAllRepositories_maps_every_entity_to_its_dto_and_logs()
    {
        var first = Entity("One");
        var second = Entity("Two");
        _repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RepositoryConfiguration>>.Success([first, second]));
        var (factory, logger) = CreateLogging();
        await using var services = Services();

        var result = await RepositoriesEndpoints.GetAllRepositories(
            Create(services), _repository, factory, CancellationToken.None);

        // Red: answer an empty list in place of mapping result.Value in GetAllRepositories.
        var ok = result.Result.Should().BeOfType<Ok<IEnumerable<RepositoryConfigurationDto>>>().Subject;
        // Red: map Name from entity.Url in MapToDto.
        ok.Value!.Select(dto => dto.Name).Should().Equal("One", "Two");
        ok.Value!.Select(dto => dto.Id).Should().Equal(first.Id, second.Id);
        // Red: drop Log.FetchingAllRepositories from GetAllRepositories, or change EventId 100 to 199.
        LoggedEventId(logger).Id.Should().Be(100);
        // Red: pass the category "Other" to CreateLogger in GetAllRepositories.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GetAllRepositories_answers_a_hand_built_500_problem_when_the_repository_fails()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RepositoryConfiguration>>.Failure("Database error"));
        await using var services = Services();

        var result = await RepositoriesEndpoints.GetAllRepositories(
            Create(services, ProblemContentType.Json), _repository, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer an Ok with an empty list in place of the failure problem in GetAllRepositories.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: answer StatusCodes.Status503ServiceUnavailable in place of 500 in InternalError.
        problem.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: Title "Internal Server Error" in place of "Internal Error" in InternalError.
        problem.ProblemDetails.Title.Should().Be("Internal Error");
        // Red: Detail set to a constant in place of detail in InternalError.
        problem.ProblemDetails.Detail.Should().Be("Database error");
        // Red: ApiProblem.Of in place of ApiProblem.AsGiven in InternalError; the problem gains type and traceId.
        problem.ProblemDetails.Type.Should().BeNull();
        problem.ProblemDetails.Extensions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRepositoryById_returns_the_dto_and_logs_the_id()
    {
        var entity = Entity("Found");
        _repository.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(Result<RepositoryConfiguration>.Success(entity));
        var (factory, logger) = CreateLogging();
        await using var services = Services();

        var result = await RepositoriesEndpoints.GetRepositoryById(
            entity.Id, Create(services), _repository, factory, CancellationToken.None);

        // Red: answer a 404 problem in place of Ok in the success branch of GetRepositoryById.
        var dto = result.Result.Should().BeOfType<Ok<RepositoryConfigurationDto>>().Subject.Value!;
        // Red: leave IsActive, CreatedAt and LastUsedAt unmapped in MapToDto.
        dto.Should().BeEquivalentTo(Dto(entity));
        // Red: drop Log.FetchingRepositoryById from GetRepositoryById, or change EventId 101 to 199.
        LoggedEventId(logger).Id.Should().Be(101);
        // Red: pass the category "Other" to CreateLogger in GetRepositoryById.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GetRepositoryById_answers_a_hand_built_404_problem_when_the_repository_is_missing()
    {
        var id = Guid.NewGuid();
        _repository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Result<RepositoryConfiguration>.Failure("not found"));
        await using var services = Services();

        var result = await RepositoriesEndpoints.GetRepositoryById(
            id, Create(services, ProblemContentType.Json), _repository, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer an Ok with a null body in place of the failure problem in GetRepositoryById.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red: answer StatusCodes.Status410Gone in place of 404 in NotFound.
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: Title "Missing" in place of "Not Found", or Detail set to a constant, in NotFound.
        problem.ProblemDetails.Title.Should().Be("Not Found");
        problem.ProblemDetails.Detail.Should().Be("not found");
        // Red: ApiProblem.Of in place of ApiProblem.AsGiven in NotFound; the problem gains type and traceId.
        problem.ProblemDetails.Type.Should().BeNull();
        problem.ProblemDetails.Extensions.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRepository_answers_201_with_the_absolute_class_cased_location_and_saves_the_entity()
    {
        RepositoryConfiguration? saved = null;
        _repository.AddAsync(Arg.Any<RepositoryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                saved = call.Arg<RepositoryConfiguration>();
                return Result<RepositoryConfiguration>.Success(saved);
            });
        var (factory, logger) = CreateLogging();
        await using var services = Services();
        var context = WithJsonBody(services,
            "{\"name\":\"New\",\"url\":\"https://github.com/org/new\",\"platform\":\"GitLab\",\"defaultBranch\":\"dev\","
            + "\"authenticationMethod\":\"Token\",\"credentialIdentifier\":\"cred\",\"description\":\"d\"}");
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.com");
        var request = await BindAsync<ApiBody<CreateRepositoryConfigurationDto>>(
            context, Parameter<ApiBody<CreateRepositoryConfigurationDto>>("request"));

        var result = await RepositoriesEndpoints.CreateRepository(request, context, _repository, factory, CancellationToken.None);

        // Red: answer InternalError in place of Created in the success branch of CreateRepository.
        var created = result.Result.Should().BeOfType<Created<RepositoryConfigurationDto>>().Subject;
        created.Value!.Name.Should().Be("New");
        created.Value.Id.Should().Be(saved!.Id);
        // Red: build the location as /api/repositories/{id} (lower case), or without the host.
        created.Location.Should().Be($"https://example.com/api/Repositories/{saved.Id}");
        // Red: pass dto.DefaultBranch in place of dto.Platform, or null in place of dto.Description, in CreateRepository's call to RepositoryConfiguration.Create.
        saved.Platform.Should().Be("GitLab");
        saved.DefaultBranch.Should().Be("dev");
        saved.AuthenticationMethod.Should().Be("Token");
        saved.CredentialIdentifier.Should().Be("cred");
        saved.Description.Should().Be("d");
        // Red: drop Log.CreatingRepository from CreateRepository, or change EventId 102 to 199.
        LoggedEventId(logger).Id.Should().Be(102);
        // Red: pass the category "Other" to CreateLogger in CreateRepository.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task CreateRepository_answers_a_hand_built_400_problem_for_an_invalid_platform()
    {
        await using var services = Services();
        var context = WithJsonBody(services,
            "{\"name\":\"Repo\",\"url\":\"https://example.com/repo\",\"platform\":\"InvalidPlatform\",\"defaultBranch\":\"main\"}");
        var request = await BindAsync<ApiBody<CreateRepositoryConfigurationDto>>(
            context, Parameter<ApiBody<CreateRepositoryConfigurationDto>>("request"));
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(ProblemContentType.Json), "test"));

        var result = await RepositoriesEndpoints.CreateRepository(
            request, context, _repository, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer 422 in place of 400 in ValidationError.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: Title "Bad Request" in place of "Validation Error" in ValidationError.
        problem.ProblemDetails.Title.Should().Be("Validation Error");
        problem.ProblemDetails.Detail.Should().NotBeNullOrEmpty();
        // Red: ApiProblem.Of in place of ApiProblem.AsGiven in ValidationError; the problem gains type and traceId.
        problem.ProblemDetails.Type.Should().BeNull();
        // Red: save an entity before answering the validation problem in CreateRepository.
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task CreateRepository_answers_a_hand_built_500_problem_when_the_save_fails()
    {
        _repository.AddAsync(Arg.Any<RepositoryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(Result<RepositoryConfiguration>.Failure("Database error"));
        await using var services = Services();
        var context = WithJsonBody(services,
            "{\"name\":\"Repo\",\"url\":\"https://github.com/org/repo\",\"platform\":\"GitHub\",\"defaultBranch\":\"main\"}");
        var request = await BindAsync<ApiBody<CreateRepositoryConfigurationDto>>(
            context, Parameter<ApiBody<CreateRepositoryConfigurationDto>>("request"));

        var result = await RepositoriesEndpoints.CreateRepository(
            request, context, _repository, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer ValidationError in place of InternalError for a failed save in CreateRepository.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        // Red: Title "Internal Server Error" in place of "Internal Error" in InternalError.
        problem.ProblemDetails.Title.Should().Be("Internal Error");
        problem.ProblemDetails.Detail.Should().Be("Database error");
    }

    [Fact]
    public async Task UpdateRepository_updates_the_tracked_entity_answers_204_and_logs()
    {
        var entity = Entity("Original");
        _repository.GetByIdTrackingAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(Result<RepositoryConfiguration>.Success(entity));
        _repository.UpdateAsync(entity, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var (factory, logger) = CreateLogging();
        var (context, request) = await BindUpdate("Updated", isActive: false);

        var result = await RepositoriesEndpoints.UpdateRepository(
            entity.Id, request, context, _repository, factory, CancellationToken.None);

        // Red: answer the NotFound problem in place of NoContent in the success branch of UpdateRepository.
        result.Result.Should().BeOfType<NoContent>();
        // Red: call GetByIdAsync in place of GetByIdTrackingAsync in UpdateRepository; the stub is not met.
        // Red: pass dto.Url in place of dto.Name to entity.Update in UpdateRepository.
        entity.Name.Should().Be("Updated");
        // Red: pass true in place of dto.IsActive to entity.Update in UpdateRepository.
        entity.IsActive.Should().BeFalse();
        await _repository.Received(1).UpdateAsync(entity, Arg.Any<CancellationToken>());
        // Red: drop Log.UpdatingRepository from UpdateRepository, or change EventId 103 to 199.
        LoggedEventId(logger).Id.Should().Be(103);
        // Red: pass the category "Other" to CreateLogger in UpdateRepository.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task UpdateRepository_answers_a_hand_built_404_problem_for_a_missing_repository()
    {
        var id = Guid.NewGuid();
        _repository.GetByIdTrackingAsync(id, Arg.Any<CancellationToken>()).Returns(Result<RepositoryConfiguration>.Failure("not found"));
        var (context, request) = await BindUpdate("Updated");

        var result = await RepositoriesEndpoints.UpdateRepository(
            id, request, context, _repository, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer ValidationError in place of NotFound in the lookup branch of UpdateRepository.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: Title "Missing" in place of "Not Found", or Detail set to a constant, in the lookup branch.
        problem.ProblemDetails.Title.Should().Be("Not Found");
        problem.ProblemDetails.Detail.Should().Be("not found");
        // Red: ApiProblem.Of in place of ApiProblem.AsGiven in NotFound; the problem gains type and traceId.
        problem.ProblemDetails.Type.Should().BeNull();
    }

    [Fact]
    public async Task UpdateRepository_answers_a_hand_built_400_problem_for_an_invalid_name_and_does_not_save()
    {
        var entity = Entity("Original");
        _repository.GetByIdTrackingAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(Result<RepositoryConfiguration>.Success(entity));
        var (context, request) = await BindUpdate(string.Empty);

        var result = await RepositoriesEndpoints.UpdateRepository(
            entity.Id, request, context, _repository, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer InternalError in place of ValidationError in the validation branch of UpdateRepository.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: Title "Bad Request" in place of "Validation Error" in ValidationError.
        problem.ProblemDetails.Title.Should().Be("Validation Error");
        problem.ProblemDetails.Detail.Should().Be("Name cannot be empty");
        // Red: call UpdateAsync before checking updateResult in UpdateRepository.
        await _repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Theory]
    [InlineData("The repository was modified by another operation", StatusCodes.Status409Conflict, "Conflict")]
    [InlineData("THE REPOSITORY WAS MODIFIED BY ANOTHER OPERATION", StatusCodes.Status409Conflict, "Conflict")]
    [InlineData("Database error", StatusCodes.Status500InternalServerError, "Internal Error")]
    public async Task UpdateRepository_maps_a_failed_save_to_409_or_500(string error, int status, string title)
    {
        var entity = Entity("Original");
        _repository.GetByIdTrackingAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(Result<RepositoryConfiguration>.Success(entity));
        _repository.UpdateAsync(entity, Arg.Any<CancellationToken>()).Returns(Result.Failure(error));
        var (context, request) = await BindUpdate("Updated");

        var result = await RepositoriesEndpoints.UpdateRepository(
            entity.Id, request, context, _repository, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: drop StringComparison.OrdinalIgnoreCase in UpdateRepository, or answer 500 for every failed save.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(status);
        // Red: Title "Internal Error" in place of "Conflict" in the conflict branch of UpdateRepository.
        problem.ProblemDetails.Title.Should().Be(title);
        problem.ProblemDetails.Detail.Should().Be(error);
    }

    [Fact]
    public async Task DeleteRepository_answers_204_and_logs_the_id()
    {
        var id = Guid.NewGuid();
        _repository.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var (factory, logger) = CreateLogging();
        await using var services = Services();

        var result = await RepositoriesEndpoints.DeleteRepository(id, Create(services), _repository, factory, CancellationToken.None);

        // Red: answer a 404 problem in place of NoContent in the success branch of DeleteRepository.
        result.Result.Should().BeOfType<NoContent>();
        // Red: drop Log.DeletingRepository from DeleteRepository, or change EventId 104 to 199.
        LoggedEventId(logger).Id.Should().Be(104);
        // Red: pass the category "Other" to CreateLogger in DeleteRepository.
        factory.Received(1).CreateLogger(Category);
    }

    [Theory]
    [InlineData("Repository configuration not found", StatusCodes.Status404NotFound, "Not Found")]
    [InlineData("REPOSITORY CONFIGURATION NOT FOUND", StatusCodes.Status404NotFound, "Not Found")]
    [InlineData("Database error", StatusCodes.Status500InternalServerError, "Internal Error")]
    public async Task DeleteRepository_maps_a_failure_to_404_or_500(string error, int status, string title)
    {
        var id = Guid.NewGuid();
        _repository.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(Result.Failure(error));
        await using var services = Services();

        var result = await RepositoriesEndpoints.DeleteRepository(
            id, Create(services, ProblemContentType.Json), _repository, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: drop StringComparison.OrdinalIgnoreCase in DeleteRepository, or answer 500 for every failure.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(status);
        // Red: swap the NotFound and InternalError arms of the conditional in DeleteRepository.
        problem.ProblemDetails.Title.Should().Be(title);
        problem.ProblemDetails.Detail.Should().Be(error);
        // Red: ApiProblem.Of in place of ApiProblem.AsGiven in NotFound; the 404 rows gain type and traceId.
        problem.ProblemDetails.Type.Should().BeNull();
    }

    [Fact]
    public async Task TestRepositoryConnection_answers_200_with_the_connected_message_and_tests_the_stored_url()
    {
        var entity = Entity("Connectable");
        _repository.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(Result<RepositoryConfiguration>.Success(entity));
        _tester.TestConnectionAsync(entity.Url, entity.AuthenticationMethod, null, Arg.Any<CancellationToken>())
            .Returns(Result<GitConnectionResult>.Success(new GitConnectionResult { Connected = true, Message = "Connection successful" }));
        var (factory, logger) = CreateLogging();
        await using var services = Services();

        var result = await RepositoriesEndpoints.TestRepositoryConnection(
            entity.Id, Create(services), _repository, _tester, factory, CancellationToken.None);

        // Red: answer the 400 TestConnectionResponse in place of Ok in the success branch of TestRepositoryConnection.
        var ok = result.Result.Should().BeOfType<Ok<TestConnectionResponse>>().Subject;
        // Red: Connected false, or the tester's message dropped, in the success body.
        ok.Value.Should().Be(new TestConnectionResponse { Connected = true, Message = "Connection successful" });
        // Red: pass a constant URL in place of entity.Url to TestConnectionAsync; the stub is not met.
        await _tester.Received(1).TestConnectionAsync(entity.Url, entity.AuthenticationMethod, null, Arg.Any<CancellationToken>());
        // Red: drop Log.TestingConnection from TestRepositoryConnection, or change EventId 105 to 199.
        LoggedEventId(logger).Id.Should().Be(105);
        // Red: pass the category "Other" to CreateLogger in TestRepositoryConnection.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task TestRepositoryConnection_answers_a_400_test_connection_body_when_the_tester_fails()
    {
        var entity = Entity("Unreachable");
        _repository.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(Result<RepositoryConfiguration>.Success(entity));
        _tester.TestConnectionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<(string username, string password)?>(), Arg.Any<CancellationToken>())
            .Returns(Result<GitConnectionResult>.Failure("Connection refused"));
        await using var services = Services();

        var result = await RepositoriesEndpoints.TestRepositoryConnection(
            entity.Id, Create(services), _repository, _tester, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer a problem in place of the TestConnectionResponse body in the failure branch.
        var json = result.Result.Should().BeOfType<ApiJsonResult<TestConnectionResponse>>().Subject;
        // Red: answer 422 in place of 400 in the failure branch of TestRepositoryConnection.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: Connected true, or the error dropped from Message, in the failure body.
        json.Value.Should().Be(new TestConnectionResponse { Connected = false, Message = "Connection refused" });
    }

    [Fact]
    public async Task TestRepositoryConnection_answers_a_hand_built_404_problem_for_a_missing_repository()
    {
        var id = Guid.NewGuid();
        _repository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Result<RepositoryConfiguration>.Failure("not found"));
        await using var services = Services();

        var result = await RepositoriesEndpoints.TestRepositoryConnection(
            id, Create(services, ProblemContentType.Json), _repository, _tester, Substitute.For<ILoggerFactory>(), CancellationToken.None);

        // Red: answer ValidationError in place of NotFound in the lookup branch of TestRepositoryConnection.
        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: Title "Missing" in place of "Not Found", or Detail set to a constant, in the lookup branch.
        problem.ProblemDetails.Title.Should().Be("Not Found");
        problem.ProblemDetails.Detail.Should().Be("not found");
        // Red: call the tester before checking the lookup in TestRepositoryConnection.
        await _tester.DidNotReceiveWithAnyArgs().TestConnectionAsync(default!, default, default, default);
    }

    private static async Task<(DefaultHttpContext Context, ApiBody<UpdateRepositoryConfigurationDto> Request)> BindUpdate(
        string name, bool isActive = true)
    {
        await using var services = Services();
        var context = WithJsonBody(services,
            $"{{\"name\":\"{name}\",\"url\":\"https://github.com/org/repo\",\"defaultBranch\":\"main\",\"isActive\":{(isActive ? "true" : "false")}}}");
        var request = await BindAsync<ApiBody<UpdateRepositoryConfigurationDto>>(
            context, Parameter<ApiBody<UpdateRepositoryConfigurationDto>>("request"));
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(ProblemContentType.Json), "test"));
        return (context, request);
    }

    private static RepositoryConfiguration Entity(string name) =>
        RepositoryConfiguration.Create(Guid.NewGuid(), name, "https://github.com/org/repo").Value;

    private static RepositoryConfigurationDto Dto(RepositoryConfiguration entity) =>
        new()
        {
            Id = entity.Id,
            Name = entity.Name,
            Url = entity.Url,
            Platform = entity.Platform,
            DefaultBranch = entity.DefaultBranch,
            AuthenticationMethod = entity.AuthenticationMethod,
            CredentialIdentifier = entity.CredentialIdentifier,
            IsActive = entity.IsActive,
            CreatedAt = entity.CreatedAt,
            LastUsedAt = entity.LastUsedAt,
            Description = entity.Description
        };

    private static ParameterInfo Parameter<T>(string name) =>
        typeof(RepositoriesEndpoints).GetMethods().SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(T) && string.Equals(p.Name, name, StringComparison.Ordinal));

    private static (ILoggerFactory Factory, ILogger Logger) CreateLogging()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        return (factory, logger);
    }

    private static EventId LoggedEventId(ILogger logger) =>
        logger.ReceivedCalls().Single(call => string.Equals(call.GetMethodInfo().Name, nameof(ILogger.Log), StringComparison.Ordinal))
            .GetArguments()[1] is EventId id ? id : throw new InvalidOperationException("No EventId argument.");
}
