using System.Text.Json;
using Daedalus.Api;
using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using TaskDto = Daedalus.Application.DTOs.TaskDto;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     The context is the endpoints' only JSON resolver, so these pin what it writes and reads. MVC's own options
///     (web defaults, <c>DefaultIgnoreCondition</c> at <c>Never</c>) are the reference: a null member is written.
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
    public void Nulls_are_written()
    {
        var json = JsonSerializer.Serialize(TaskWithNulls(), ApiJsonSerializerContext.Default.TaskDto);

        // Red: DefaultIgnoreCondition = WhenWritingNull on the context drops "result" from the text.
        json.Should().Contain("\"result\":null");
        json.Should().Contain("\"completedAt\":null");
    }

    [Fact]
    public void Names_are_camel_case()
    {
        var json = JsonSerializer.Serialize(TaskWithNulls(), ApiJsonSerializerContext.Default.TaskDto);

        // Red: removing PropertyNamingPolicy from the context writes "TaskId" and "ProjectId".
        json.Should().Contain("\"taskId\":\"T-1\"");
        json.Should().Contain("\"projectId\":");
        json.Should().NotContain("\"TaskId\"");
    }

    [Fact]
    public void Reading_is_case_insensitive()
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
    public void Numbers_may_be_read_from_strings()
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
        var problem = new ProblemDetails { Status = 500, Title = "Internal Server Error" };

        var json = JsonSerializer.Serialize(problem, ApiJsonSerializerContext.Default.ProblemDetails);

        // Red: ProblemDetails carries its own WhenWritingNull on type, detail and instance, so the context's Never
        // must leave this body alone. WriteIndented = true on the context spreads the text over lines; a context
        // that wrote the three members as null would add them.
        json.Should().Be("{\"title\":\"Internal Server Error\",\"status\":500}");
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

    [Fact]
    public void Endpoint_options_write_nulls_as_MVC_does()
    {
        using var provider = new ServiceCollection().AddDaedalusEndpoints().BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

        var json = JsonSerializer.Serialize(TaskWithNulls(), options.GetTypeInfo(typeof(TaskDto)));

        // Red: a context that says WhenWritingNull, or Http options that override it, drop "result".
        json.Should().Contain("\"result\":null");
    }
}
