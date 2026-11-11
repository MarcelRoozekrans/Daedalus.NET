using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Daedalus.Api;
using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Application.DTOs.WorkflowRuns;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using TaskDto = Daedalus.Application.DTOs.TaskDto;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     Two things are pinned. Through <c>ApiJsonSerializerContext.Default</c>, which the exception handler uses, the
///     context's own options apply. Through the endpoints' Http <c>JsonOptions</c>, where the context is only the
///     type resolver, the options <c>AddDaedalusEndpoints</c> leaves decide nulls, naming, case and numbers; MVC's
///     (web defaults, <c>DefaultIgnoreCondition</c> at <c>Never</c>) are the reference.
/// </summary>
public sealed class ApiJsonSerializerContextTests
{
    private static TaskDto TaskWithNulls() => new(
        Id: Guid.Empty,
        TaskId: "T-1",
        ProjectId: Guid.Empty,
        Title: "title",
        Description: "description",
        Priority: 1,
        Phase: "phase",
        ParallelGroup: 0,
        Dependencies: [],
        FilesToModify: [],
        EstimatedComplexity: 1,
        Prompt: "prompt",
        CompletionPromise: "done",
        MaxIterations: 1,
        Status: 0,
        CurrentSessionId: null,
        Result: null,
        IterationCount: 0,
        CreatedAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        CompletedAt: null,
        Learnings: null,
        LearningsUpdatedAt: null,
        Executions: [],
        WorkflowRunId: null,
        PullRequestUrl: null,
        RunStatus: null,
        AwaitingSignal: null);

    [Fact]
    public void Default_nulls_are_written()
    {
        var json = JsonSerializer.Serialize(TaskWithNulls(), ApiJsonSerializerContext.Default.TaskDto);

        // Red: DefaultIgnoreCondition = WhenWritingNull on the context drops "result" from the text.
        json.Should().Contain("\"result\":null");
        json.Should().Contain("\"completedAt\":null");
    }

    [Fact]
    public void Default_names_are_camel_case()
    {
        var json = JsonSerializer.Serialize(TaskWithNulls(), ApiJsonSerializerContext.Default.TaskDto);

        // Red: removing PropertyNamingPolicy from the context writes "TaskId" and "ProjectId".
        json.Should().Contain("\"taskId\":\"T-1\"");
        json.Should().Contain("\"projectId\":");
        json.Should().NotContain("\"TaskId\"");
    }

    [Fact]
    public void Default_reading_is_case_insensitive()
    {
        var parsed = JsonSerializer.Deserialize(
            "{\"TITLE\":\"upper\",\"Description\":\"mixed\"}",
            ApiJsonSerializerContext.Default.TaskDto);

        // Red: PropertyNameCaseInsensitive = false on the context leaves both members null.
        parsed.Should().NotBeNull();
        parsed!.Title.Should().Be("upper");
        parsed.Description.Should().Be("mixed");
    }

    [Fact]
    public void Default_numbers_may_be_read_from_strings()
    {
        var parsed = JsonSerializer.Deserialize(
            "{\"Priority\":\"3\"}",
            ApiJsonSerializerContext.Default.TaskDto);

        // Red: dropping NumberHandling = AllowReadingFromString makes the read throw JsonException.
        parsed!.Priority.Should().Be(3);
    }

    [Fact]
    public void Problem_details_keep_omitting_null_members()
    {
        // The shape GlobalExceptionHandler.HandleUnexpectedException builds, with a fixed trace id.
        var problem = new ProblemDetails
        {
            Status = 500,
            Title = "Internal server error",
            Detail = "An unexpected error occurred. Please try again later.",
            Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1"
        };
        problem.Extensions["traceId"] = "trace-1";

        var json = JsonSerializer.Serialize(problem, ApiJsonSerializerContext.Default.ProblemDetails);

        // Red: WriteIndented = true on the context spreads the text over lines; a different naming policy or an
        // ignore condition that wrote "instance":null changes the exact text.
        json.Should().Be(
            "{\"type\":\"https://tools.ietf.org/html/rfc7231#section-6.6.1\",\"title\":\"Internal server error\","
            + "\"status\":500,\"detail\":\"An unexpected error occurred. Please try again later.\",\"traceId\":\"trace-1\"}");
    }

    [Fact]
    public void Validation_problem_details_are_in_the_context()
    {
        var problem = new ValidationProblemDetails(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["name"] = ["bad"] })
        {
            Status = 400
        };

        var json = JsonSerializer.Serialize(problem, ApiJsonSerializerContext.Default.ValidationProblemDetails);

        // Red: removing the ValidationProblemDetails entry makes Default.ValidationProblemDetails stop compiling.
        json.Should().Contain("\"errors\":{\"name\":[\"bad\"]}");
    }

    [Fact]
    public void Stream_events_omit_unset_members()
    {
        var json = JsonSerializer.Serialize(
            new AgentEventDto("done"),
            AgentEventStreamJsonContext.Default.AgentEventDto);

        // Red: pointing this at ApiJsonSerializerContext, whose DefaultIgnoreCondition is Never, adds the eight
        // unset members as null (seen failing at index 14); so does Never on AgentEventStreamJsonContext.
        json.Should().Be("{\"kind\":\"done\"}");
    }

    [Fact]
    public void Endpoints_resolve_json_through_the_context_alone()
    {
        using var provider = new ServiceCollection().AddDaedalusEndpoints().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

        // Red: without the Clear call the default reflection resolver stays in the chain behind the context, and
        // without the Insert the chain holds only that resolver.
        options.TypeInfoResolverChain.Should().ContainSingle()
            .Which.Should().BeSameAs(ApiJsonSerializerContext.Default);
    }

    private static JsonSerializerOptions EndpointOptions(out ServiceProvider provider)
    {
        provider = new ServiceCollection().AddDaedalusEndpoints().BuildServiceProvider();
        return provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
    }

    [Fact]
    public void Endpoint_options_write_nulls_as_MVC_does()
    {
        var options = EndpointOptions(out var provider);
        using var _ = provider;

        var json = JsonSerializer.Serialize(TaskWithNulls(), options.GetTypeInfo<TaskDto>());

        // Red: DefaultIgnoreCondition = WhenWritingNull on options.SerializerOptions inside AddDaedalusEndpoints
        // drops "result". The context's own attribute does not matter here: the chain takes the Http options.
        json.Should().Contain("\"result\":null");
    }

    [Fact]
    public void Endpoint_options_write_camel_case_names()
    {
        var options = EndpointOptions(out var provider);
        using var _ = provider;

        var json = JsonSerializer.Serialize(TaskWithNulls(), options.GetTypeInfo<TaskDto>());

        // Red: PropertyNamingPolicy = null on options.SerializerOptions inside AddDaedalusEndpoints writes "TaskId".
        json.Should().Contain("\"taskId\":\"T-1\"").And.NotContain("\"TaskId\"");
    }

    [Fact]
    public void Endpoint_options_read_names_case_insensitively()
    {
        var options = EndpointOptions(out var provider);
        using var _ = provider;

        var parsed = JsonSerializer.Deserialize("{\"TITLE\":\"upper\"}", options.GetTypeInfo<TaskDto>());

        // Red: PropertyNameCaseInsensitive = false on options.SerializerOptions inside AddDaedalusEndpoints leaves
        // Title null.
        parsed!.Title.Should().Be("upper");
    }

    /// <summary>The request body types the controllers bind, as Task 2's implicit-required table lists them.</summary>
    public static TheoryData<Type> RequestBodyTypes => new(
        typeof(CreateProjectDto), typeof(UpdateProjectDto), typeof(CreateTaskDto), typeof(UpdateTaskDto),
        typeof(SubmitAnalysisRequest), typeof(IterateAnalysisRequest), typeof(FinalizeAnalysisRequest),
        typeof(CreateBrainstormSessionDto), typeof(SendBrainstormMessageDto), typeof(PrdRequestDto),
        typeof(PrdConversionRequestDto), typeof(CreateRepositoryConfigurationDto), typeof(UpdateRepositoryConfigurationDto),
        typeof(SendTurnRequestDto), typeof(StartWorkflowRunRequest), typeof(ResumeWorkflowRunRequest),
        typeof(CancelWorkflowRunRequest));

    /// <summary>
    ///     An empty object binds to the same value through the endpoints' source-generated contract as through MVC's
    ///     reflection-based one, so omitted members get the same constructor and initializer values on both stacks.
    /// </summary>
    [Theory]
    [MemberData(nameof(RequestBodyTypes))]
    public void Endpoint_options_construct_request_bodies_as_mvc_does(Type type)
    {
        var options = EndpointOptions(out var provider);
        using var _ = provider;
        var mvc = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var viaMvc = JsonSerializer.Serialize(JsonSerializer.Deserialize("{}", type, mvc), type, mvc);
        var viaEndpoint = JsonSerializer.Serialize(JsonSerializer.Deserialize("{}", options.GetTypeInfo(type)), type, mvc);

        // Red: make PrdRequestDto's UserRequirements and MaxItems init-only again. The generator then sets every
        //      omitted init-only member, so an empty object reads the user requirements and the item limit as null,
        //      not as the empty string and 10.
        viaEndpoint.Should().Be(viaMvc);
    }
}
