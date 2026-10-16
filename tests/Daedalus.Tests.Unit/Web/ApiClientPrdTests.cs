using System.Net;
using Daedalus.Application.DTOs;
using Daedalus.Web.Services;
using Complexity = Daedalus.Domain.Entities.Complexity;
using Priority = Daedalus.Domain.Entities.Priority;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The PRD calls read the API's camelCase answers, which the page's case-sensitive read once bound as nulls (R8), and a
///     refusal carries the <c>error</c> the controller writes, not the raw JSON.
/// </summary>
public sealed class ApiClientPrdTests
{
    private const string GeneratePath = "/api/prd/generate";
    private const string ConvertPath = "/api/prd/convert-to-tasks";

    private static readonly Guid ProjectId = new(0x4d7c1b2a, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    private const string PrdJson = """
        {
          "id": "prd-1",
          "projectId": "4d7c1b2a-0000-0000-0000-000000000001",
          "productOverview": "A widget shop.",
          "goals": ["Sell widgets"],
          "assumptions": ["Widgets exist"],
          "features": [{"title":"Cart","description":"A cart","acceptanceCriteria":"Holds widgets","priority":1,"estimatedComplexity":2}],
          "tasks": [],
          "generatedAt": "2026-10-16T08:00:00Z"
        }
        """;

    private const string TasksJson = """
        [{"id":"7a000000-0000-0000-0000-000000000001","taskId":"TASK-1","projectId":"4d7c1b2a-0000-0000-0000-000000000001",
          "title":"Cart","description":"A cart","priority":1,"phase":"Implementation","parallelGroup":1,
          "dependencies":[],"filesToModify":[],"estimatedComplexity":2,"prompt":"p","completionPromise":"","maxIterations":0,
          "status":0,"iterationCount":0,"createdAt":"2026-10-16T08:00:00Z","executions":[]}]
        """;

    private readonly RecordingHandler _handler = new();

    private static PrdConversionRequestDto Conversion() => new(
        ProjectId,
        [new PrdItemForConversionDto("Cart", "A cart", "Holds widgets", Priority.High, Complexity.Medium, [], "Implementation", 1)]);

    /// <summary>
    ///     A generated PRD binds from the API's camelCase, so the review step has its goals and features.
    ///     Red: read the body as the page once did, <c>JsonSerializer.Deserialize</c> without the web defaults; Goals is
    ///     null.
    ///     Red: post to "/api/prd/generated"; the request is unrouted, a 404, and the call fails.
    ///     Red: post an empty object in place of the request; the body has no userRequirements.
    /// </summary>
    [Fact]
    public async Task A_camel_case_prd_response_binds()
    {
        _handler.OnJson(GeneratePath, PrdJson);

        var result = await _handler.Client().GeneratePrdAsync(new PrdRequestDto(ProjectId, "Sell widgets"));

        result.IsSuccess.Should().BeTrue();
        result.Value.ProductOverview.Should().Be("A widget shop.");
        result.Value.Goals.Should().Equal("Sell widgets");
        result.Value.Features.Should().ContainSingle().Which.Title.Should().Be("Cart");
        _handler.Requests.Should().ContainSingle().Which.Body.Should().Contain("\"userRequirements\":\"Sell widgets\"");
    }

    /// <summary>
    ///     The controller's 400 <c>{ "error": ... }</c> is the failure, not the JSON it came in.
    ///     Red: fail with the raw body, as the page once did; the error is the JSON text.
    /// </summary>
    [Fact]
    public async Task A_400_error_on_generate_becomes_the_failure()
    {
        _handler.OnJson(GeneratePath, """{"error":"User requirements cannot be empty"}""", HttpStatusCode.BadRequest);

        var result = await _handler.Client().GeneratePrdAsync(new PrdRequestDto(ProjectId, ""));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("User requirements cannot be empty");
    }

    /// <summary>
    ///     The created tasks bind from the API's camelCase.
    ///     Red: read the body without the web defaults; the task's Title is null.
    ///     Red: post to "/api/prd/generate" in ConvertPrdToTasksAsync; the request is unrouted, a 404, and the call fails.
    ///     Red: post an empty object in place of the request; the body has no prdItems.
    /// </summary>
    [Fact]
    public async Task A_camel_case_task_list_binds()
    {
        _handler.OnJson(ConvertPath, TasksJson);

        var result = await _handler.Client().ConvertPrdToTasksAsync(Conversion());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Title.Should().Be("Cart");
        _handler.Requests.Should().ContainSingle().Which.Body.Should().Contain("\"prdItems\":[{\"title\":\"Cart\"");
    }

    /// <summary>
    ///     A refused conversion carries the controller's <c>error</c>.
    ///     Red: fail with the raw body; the error is the JSON text.
    /// </summary>
    [Fact]
    public async Task A_400_error_on_convert_becomes_the_failure()
    {
        _handler.OnJson(ConvertPath, """{"error":"No PRD items to convert"}""", HttpStatusCode.BadRequest);

        var result = await _handler.Client().ConvertPrdToTasksAsync(Conversion());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("No PRD items to convert");
    }
}
