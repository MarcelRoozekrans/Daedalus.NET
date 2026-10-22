using System.Net;
using System.Text;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Services;
using Daedalus.Web.State.Runs;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     Request bytes and response reads of every call of the four console clients, captured through
///     <see cref="RecordingHandler"/> from today's reflective <c>System.Net.Http.Json</c> calls before phase 3.2 moves them
///     to a JSON context. A request body is pinned byte for byte. A response is a camelCase body as the API sends it, and
///     what the client read is written back out with the web defaults: the same text means every field was read.
/// </summary>
/// <remarks>
///     The sample bodies fill every property, nested ones included, and every string carries <c>&lt;&amp;'é🚀&gt;</c>, so
///     a dropped property, a case-sensitive read or a change of encoder shows. Each is a literal captured from today's
///     output, never computed.
/// </remarks>
public sealed class WebClientGoldenTests
{
    private const string Awkward = "<a & 'é' 🚀>";

    private static readonly Guid Id = new Guid(0x11111111, 0x2222, 0x4333, 0x84, 0x44, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55) /* 11111111-2222-4333-8444-555555555555 */;
    private static readonly Guid Other = new Guid(0x66666666, 0x7777, 0x4888, 0x99, 0x99, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0) /* 66666666-7777-4888-9999-000000000000 */;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> Calls()
    {
        var calls = new TheoryData<string>();
        foreach (var call in Cases.Keys)
        {
            calls.Add(call);
        }

        return calls;
    }

    /// <summary>
    ///     Red, requests: pass <c>JsonSerializerOptions.Default</c> to <c>PostAsJsonAsync</c> in <c>ApiClient.PostAsync&lt;T&gt;</c>;
    ///     every body written through it turns PascalCase. Red, reads: pass <c>JsonSerializerOptions.Default</c> to
    ///     <c>ReadFromJsonAsync</c> in <c>ApiClient.TryReadJsonAsync</c>; the camelCase bodies no longer match and every
    ///     read through it, writes, runs and refusals, comes back empty. Red, the agent client: build its <c>Json</c>
    ///     options from <c>JsonSerializerDefaults.General</c>; its reads come back empty and the streamed turn's body
    ///     turns PascalCase. Red, the project client: pass <c>JsonSerializerOptions.Default</c> to its
    ///     <c>PostAsJsonAsync</c>; <c>project.create</c>'s body turns PascalCase. Red, the repositories client: pass it to
    ///     <c>GetAllAsync</c>'s <c>ReadFromJsonAsync</c>; <c>repos.list</c> reads empty entries.
    /// </summary>
    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Each_call_sends_and_reads_as_captured(string call)
    {
        var c = Cases[call];
        var handler = new RecordingHandler();
        handler.On(c.Path, () => c.Respond());

        var read = await c.Invoke(handler);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Method.Should().Be(c.Method);
        request.PathAndQuery.Should().Be(c.Path);
        request.Body.Should().Be(c.Body);
        read.Should().Be(c.Reads);
    }

    private sealed record Case(string Method, string Path, Func<HttpResponseMessage> Respond, Func<RecordingHandler, Task<string>> Invoke, string? Body, string Reads);

    private static HttpResponseMessage Ok(string json) => RecordingHandler.Json(HttpStatusCode.OK, json);

    private static HttpResponseMessage Status(HttpStatusCode code, string? json = null) =>
        json is null ? new HttpResponseMessage(code) : RecordingHandler.Json(code, json);

    private static T In<T>(string json) => JsonSerializer.Deserialize<T>(json, Web)!;

    private static string Dump<T>(T value) => JsonSerializer.Serialize(value, Web);

    private static string Read<T>(Result<T> result) => result.IsSuccess ? Dump(result.Value) : "failure: " + result.Error;

    private static string Read(Result result) => result.IsSuccess ? "success" : "failure: " + result.Error;

    private static async Task<string> Streamed(IAsyncEnumerable<AgentEventDto> events)
    {
        var read = new List<AgentEventDto>();
        await foreach (var e in events)
        {
            read.Add(e);
        }

        return Dump(read);
    }

    private static HttpResponseMessage Sse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"data: {json}\n\n", Encoding.UTF8, "text/event-stream") };

    private static readonly Dictionary<string, Case> Cases = new(StringComparer.Ordinal)
    {
        // ApiClient: reads
        ["tasks.list"] = new("GET", "/api/tasks?page=2&pageSize=5", () => Ok(PagedTaskDto),
            async h => Read(await h.Client().GetTasksAsync(2, 5)), null, PagedTaskDto),
        ["tasks.get"] = new("GET", $"/api/tasks/{Id}", () => Ok(TaskDto),
            async h => Read(await h.Client().GetTaskAsync(Id)), null, TaskDto),
        ["projects.list"] = new("GET", "/api/projects?page=1&pageSize=10", () => Ok(PagedProjectDto),
            async h => Read(await h.Client().GetProjectsAsync()), null, PagedProjectDto),
        ["projects.get"] = new("GET", $"/api/projects/{Id}", () => Ok(ProjectDto),
            async h => Read(await h.Client().GetProjectAsync(Id)), null, ProjectDto),
        ["projects.with-tasks"] = new("GET", $"/api/projects/{Id}/with-tasks", () => Ok(ProjectDto),
            async h => Read(await h.Client().GetProjectWithTasksAsync(Id)), null, ProjectDto),
        ["schedules.overview"] = new("GET", "/api/schedules", () => Ok(RunDiagnosisList),
            async h => Read(await h.Client().GetScheduleOverviewAsync()), null, RunDiagnosisList),
        ["schedules.history"] = new("GET", $"/api/schedules/{Id}/runs?take=3", () => Ok(RunDiagnosisList),
            async h => Read(await h.Client().GetScheduleRunHistoryAsync(Id, 3)), null, RunDiagnosisList),
        ["costs.summary"] = new("GET", "/api/cost-analytics/summary", () => Ok(CostSummaryDto),
            async h => Read(await h.Client().GetCostSummaryAsync()), null, CostSummaryDto),
        ["costs.by-project"] = new("GET", "/api/cost-analytics/by-project", () => Ok(ProjectCostList),
            async h => Read(await h.Client().GetCostsByProjectAsync()), null, ProjectCostList),
        ["costs.by-project-id"] = new("GET", $"/api/cost-analytics/by-project/{Id}", () => Ok(TaskCostList),
            async h => Read(await h.Client().GetCostsByProjectIdAsync(Id)), null, TaskCostList),
        ["costs.estimate"] = new("GET", "/api/cost-analytics/estimate?modelId=claude%20%3C%26%3E&maxIterations=3&estimatedPromptTokens=100",
            () => Ok(CostEstimateDto),
            async h => Read(await h.Client().EstimateCostAsync("claude <&>", 3, 100)), null, CostEstimateDto),
        ["costs.pricing"] = new("GET", "/api/cost-analytics/pricing", () => Ok(ModelPricingList),
            async h => Read(await h.Client().GetModelPricingAsync()), null, ModelPricingList),
        ["brainstorm.get"] = new("GET", $"/api/brainstorm/sessions/{Id}", () => Ok(BrainstormSessionDto),
            async h => Read(await h.Client().GetBrainstormSessionAsync(Id)), null, BrainstormSessionDto),
        ["brainstorm.list"] = new("GET", $"/api/brainstorm/sessions?projectId={Id}", () => Ok(BrainstormSessionSummaryList),
            async h => Read(await h.Client().GetBrainstormSessionsAsync(Id)), null, BrainstormSessionSummaryList),

        // ApiClient: runs
        ["runs.list"] = new("GET", """/api/workflow-runs?take=5&status=Running&process=manufacture&after=c%3C%26%3E&signal=human-approval&updatedSince=2026-10-22T10%3A00%3A00.0000000Z""", () => Ok(WorkflowRunList),
            async h => Read(await h.Client().GetRunsAsync(new RunListQuery("Running", "manufacture", "c<&>", 5, "human-approval", new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero)))),
            null, WorkflowRunList),
        ["runs.get"] = new("GET", $"/api/workflow-runs/{Id}", () => Ok(WorkflowRunView),
            async h => Read(await h.Client().GetRunAsync(Id)), null, "{\"run\":" + WorkflowRunView + ",\"forbidden\":false}"),
        ["runs.manufacture"] = new("POST", $"/api/tasks/{Id}/manufacture", () => Ok(StartWorkflowRunResponse),
            async h => Read(await h.Client().ManufactureTaskAsync(Id)), null, StartWorkflowRunResponse),
        ["runs.approve"] = new("POST", $"/api/workflow-runs/{Id}/resume", () => Status(HttpStatusCode.Accepted),
            async h => Read(await h.Client().ApproveRunAsync(Id, ["correctness-1", Awkward], applyStandingInstructions: true)), """{"signal":"human_approval","payload":null,"applyStandingInstructions":true,"dropFindings":["correctness-1","\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"]}""", "success"),
        ["runs.cancel"] = new("POST", $"/api/workflow-runs/{Id}/cancel", () => Status(HttpStatusCode.Accepted),
            async h => Read(await h.Client().CancelRunAsync(Id, Awkward)), """{"reason":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"}""", "success"),
        ["runs.cancel-blank"] = new("POST", $"/api/workflow-runs/{Id}/cancel", () => Status(HttpStatusCode.Accepted),
            async h => Read(await h.Client().CancelRunAsync(Id, " ")), """{"reason":null}""", "success"),
        ["runs.cancel-refused-detail"] = new("POST", $"/api/workflow-runs/{Id}/cancel",
            () => Status(HttpStatusCode.Conflict, """{"title":"Conflict","detail":"the run <&> has ended"}"""),
            async h => Read(await h.Client().CancelRunAsync(Id, null)), """{"reason":null}""", """failure: the run <&> has ended"""),
        ["runs.cancel-refused-error"] = new("POST", $"/api/workflow-runs/{Id}/cancel",
            () => Status(HttpStatusCode.BadRequest, """{"error":"no such run <&>"}"""),
            async h => Read(await h.Client().CancelRunAsync(Id, null)), """{"reason":null}""", """failure: no such run <&>"""),
        ["runs.retry"] = new("POST", $"/api/workflow-runs/{Id}/retry", () => Status(HttpStatusCode.Accepted),
            async h => Read(await h.Client().RetryRunAsync(Id)), null, "success"),

        // ApiClient: writes
        ["tasks.create"] = new("POST", "/api/tasks", () => Ok(TaskDto),
            async h => Read(await h.Client().CreateTaskAsync(In<CreateTaskDto>(CreateTaskDto))), CreateTaskDto, TaskDto),
        ["tasks.update"] = new("PUT", $"/api/tasks/{Id}", () => Ok(TaskDto),
            async h => Read(await h.Client().UpdateTaskAsync(Id, In<UpdateTaskDto>(UpdateTaskDto))), UpdateTaskDto, TaskDto),
        ["tasks.delete"] = new("DELETE", $"/api/tasks/{Id}", () => Status(HttpStatusCode.NoContent),
            async h => Read(await h.Client().DeleteTaskAsync(Id)), null, "success"),
        ["brainstorm.create"] = new("POST", "/api/brainstorm/sessions", () => Ok(BrainstormSessionDto),
            async h => Read(await h.Client().CreateBrainstormSessionAsync(In<CreateBrainstormSessionDto>(CreateBrainstormSessionDto))),
            CreateBrainstormSessionDto, BrainstormSessionDto),
        ["brainstorm.message"] = new("POST", $"/api/brainstorm/sessions/{Id}/messages", () => Ok(BrainstormMessageDto),
            async h => Read(await h.Client().SendBrainstormMessageAsync(Id, In<SendBrainstormMessageDto>(SendBrainstormMessageDto))),
            SendBrainstormMessageDto, BrainstormMessageDto),
        ["brainstorm.advance"] = new("POST", $"/api/brainstorm/sessions/{Id}/advance", () => Ok(BrainstormSessionDto),
            async h => Read(await h.Client().AdvanceBrainstormPhaseAsync(Id)), """{}""", BrainstormSessionDto),
        ["brainstorm.abandon"] = new("POST", $"/api/brainstorm/sessions/{Id}/abandon", () => Status(HttpStatusCode.NoContent),
            async h => Read(await h.Client().AbandonBrainstormSessionAsync(Id)), """{}""", "success"),
        ["brainstorm.generate"] = new("POST", $"/api/brainstorm/sessions/{Id}/generate-tasks", () => Ok(TaskList),
            async h => Read(await h.Client().GenerateBrainstormTasksAsync(Id)), """{}""", TaskList),
        ["prd.generate"] = new("POST", "/api/prd/generate", () => Ok(PrdResponseDto),
            async h => Read(await h.Client().GeneratePrdAsync(In<PrdRequestDto>(PrdRequestDto))), PrdRequestDto, PrdResponseDto),
        ["prd.convert"] = new("POST", "/api/prd/convert-to-tasks", () => Ok(TaskList),
            async h => Read(await h.Client().ConvertPrdToTasksAsync(In<PrdConversionRequestDto>(PrdConversionRequestDto))),
            PrdConversionRequestDto, TaskList),
        ["schedules.resend"] = new("POST", $"/api/schedules/{Id}/runs/{Other}/resend", () => Status(HttpStatusCode.Accepted),
            async h => Read(await h.Client().ResendScheduleRunAsync(Id, Other)), """{}""", "success"),

        // AgentApiClient
        ["agents.list"] = new("GET", "/api/agents", () => Ok(AgentSummaryList),
            async h => Read(await h.AgentClient().GetAgentsAsync()), null, AgentSummaryList),
        ["agents.session-create"] = new("POST", "/api/agents/agent%20%3C1%3E/sessions", () => Ok(CreateAgentSessionResponseDto),
            async h => (await h.AgentClient().CreateSessionAsync("agent <1>")) is { IsSuccess: true } created ? created.Value : "failure",
            null, "sessionId <&'é🚀>"),
        ["agents.sessions"] = new("GET", "/api/agents/sessions?skip=2&take=5", () => Ok(AgentSessionList),
            async h => Read(await h.AgentClient().ListSessionsAsync(2, 5)), null, AgentSessionList),
        ["agents.session-get"] = new("GET", "/api/agents/sessions/s-1", () => Ok(AgentSessionDetailDto),
            async h => Read(await h.AgentClient().GetSessionAsync("s-1")), null, AgentSessionDetailDto),
        ["agents.session-close"] = new("DELETE", "/api/agents/sessions/s-1", () => Status(HttpStatusCode.NoContent),
            async h => Read(await h.AgentClient().CloseSessionAsync("s-1")), null, "success"),
        ["agents.memories"] = new("GET", "/api/agent-memories?page=1&pageSize=20&includeArchived=true&kind=fact&agentId=a-1", () => Ok(PagedMemoryDto),
            async h => Read(await h.AgentClient().ListMemoriesAsync("fact", "a-1", includeArchived: true)), null, PagedMemoryDto),
        ["agents.memory"] = new("GET", "/api/agent-memories/m-1?agentId=a-1", () => Ok(MemoryDto),
            async h => Read(await h.AgentClient().GetMemoryAsync("m-1", "a-1")), null, MemoryDto),
        ["agents.memory-forget"] = new("DELETE", "/api/agent-memories/m-1?hard=true", () => Status(HttpStatusCode.NoContent),
            async h => Read(await h.AgentClient().ForgetMemoryAsync("m-1", hard: true)), null, "success"),
        ["agents.turn"] = new("POST", "/api/agents/sessions/s-1/turns/stream", () => Sse(AgentEventDto),
            async h => await Streamed(h.AgentClient().StreamTurnAsync("s-1", Awkward)), """{"text":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"}""", "[" + AgentEventDto + "]"),

        // ProjectApiClient
        ["project.create"] = new("POST", "/api/projects", () => Ok(ProjectDto),
            async h => Read(await h.ProjectClient().CreateProjectAsync(In<CreateProjectDto>(CreateProjectDto))), CreateProjectDto, ProjectDto),
        ["project.update"] = new("PUT", $"/api/projects/{Id}", () => Ok(ProjectDto),
            async h => Read(await h.ProjectClient().UpdateProjectAsync(Id, In<UpdateProjectDto>(UpdateProjectDto))), UpdateProjectDto, ProjectDto),
        ["project.delete"] = new("DELETE", $"/api/projects/{Id}", () => Status(HttpStatusCode.NoContent),
            async h => Read(await h.ProjectClient().DeleteProjectAsync(Id)), null, "true"),

        // RepositoriesApiClient
        ["repos.list"] = new("GET", "/api/repositories", () => Ok(RepositoryConfigurationList),
            async h => Read(await h.RepositoriesClient().GetAllAsync(CancellationToken.None)), null, "{\"repositories\":" + RepositoryConfigurationList + ",\"forbidden\":false}"),
        ["repos.create"] = new("POST", "/api/repositories", () => Status(HttpStatusCode.Created),
            async h => Read(await h.RepositoriesClient().CreateAsync(In<CreateRepositoryConfigurationDto>(CreateRepositoryConfigurationDto))),
            CreateRepositoryConfigurationDto, "success"),
        ["repos.update"] = new("PUT", $"/api/repositories/{Id}", () => Status(HttpStatusCode.NoContent),
            async h => Read(await h.RepositoriesClient().UpdateAsync(Id, In<UpdateRepositoryConfigurationDto>(UpdateRepositoryConfigurationDto))),
            UpdateRepositoryConfigurationDto, "success"),
        ["repos.delete"] = new("DELETE", $"/api/repositories/{Id}", () => Status(HttpStatusCode.NoContent),
            async h => Read(await h.RepositoriesClient().DeleteAsync(Id)), null, "success"),
        ["repos.test"] = new("POST", $"/api/repositories/{Id}/test-connection", () => Status(HttpStatusCode.OK),
            async h => Read(await h.RepositoriesClient().TestConnectionAsync(Id)), null, "success"),
    };

    // Sample bodies, captured from today's web-defaults output of a fully populated instance of each type.

    private const string PagedTaskDto = """{"items":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"taskId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":7,"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"filesToModify":["filesToModifyItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"estimatedComplexity":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromise":"completionPromise \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","maxIterations":7,"status":7,"currentSessionId":"316f4bcd-0ee9-8da5-7c72-a350e80e1bc3","result":"result \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","iterationCount":7,"createdAt":"2026-10-22T10:11:12Z","completedAt":"2026-10-22T10:11:12Z","learnings":"learnings \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","learningsUpdatedAt":"2026-10-22T10:11:12Z","executions":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"9740fc52-a9b1-4b1d-693a-9a5f4eae7b3c","sessionId":"6e6e6fd0-2891-39a2-3b73-58ff70124550","iterationNumber":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","llmResponse":"llmResponse \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromiseFound":true,"executedAt":"2026-10-22T10:11:12Z","executionDuration":"01:02:03","error":"error \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"workflowRunId":"0d0d5caf-3746-e015-e9c9-f47f8fb0f67c","pullRequestUrl":"https://example.test/pullRequestUrl","runStatus":"runStatus \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","awaitingSignal":"awaitingSignal \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"total":7,"page":7,"pageSize":7}""";

    private const string TaskDto = """{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"taskId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":7,"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"filesToModify":["filesToModifyItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"estimatedComplexity":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromise":"completionPromise \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","maxIterations":7,"status":7,"currentSessionId":"316f4bcd-0ee9-8da5-7c72-a350e80e1bc3","result":"result \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","iterationCount":7,"createdAt":"2026-10-22T10:11:12Z","completedAt":"2026-10-22T10:11:12Z","learnings":"learnings \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","learningsUpdatedAt":"2026-10-22T10:11:12Z","executions":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"9740fc52-a9b1-4b1d-693a-9a5f4eae7b3c","sessionId":"6e6e6fd0-2891-39a2-3b73-58ff70124550","iterationNumber":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","llmResponse":"llmResponse \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromiseFound":true,"executedAt":"2026-10-22T10:11:12Z","executionDuration":"01:02:03","error":"error \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"workflowRunId":"0d0d5caf-3746-e015-e9c9-f47f8fb0f67c","pullRequestUrl":"https://example.test/pullRequestUrl","runStatus":"runStatus \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","awaitingSignal":"awaitingSignal \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string PagedProjectDto = """{"items":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","projectName":"projectName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","version":"version \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","repositoryUrl":"repositoryUrl \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","defaultBranch":"defaultBranch \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","createdAt":"2026-10-22T10:11:12Z","modifiedAt":"2026-10-22T10:11:12Z","tasks":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"taskId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":7,"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"filesToModify":["filesToModifyItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"estimatedComplexity":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromise":"completionPromise \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","maxIterations":7,"status":7,"currentSessionId":"316f4bcd-0ee9-8da5-7c72-a350e80e1bc3","result":"result \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","iterationCount":7,"createdAt":"2026-10-22T10:11:12Z","completedAt":"2026-10-22T10:11:12Z","learnings":"learnings \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","learningsUpdatedAt":"2026-10-22T10:11:12Z","executions":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"9740fc52-a9b1-4b1d-693a-9a5f4eae7b3c","sessionId":"6e6e6fd0-2891-39a2-3b73-58ff70124550","iterationNumber":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","llmResponse":"llmResponse \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromiseFound":true,"executedAt":"2026-10-22T10:11:12Z","executionDuration":"01:02:03","error":"error \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"workflowRunId":"0d0d5caf-3746-e015-e9c9-f47f8fb0f67c","pullRequestUrl":"https://example.test/pullRequestUrl","runStatus":"runStatus \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","awaitingSignal":"awaitingSignal \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}]}],"total":7,"page":7,"pageSize":7}""";

    private const string ProjectDto = """{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","projectName":"projectName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","version":"version \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","repositoryUrl":"repositoryUrl \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","defaultBranch":"defaultBranch \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","createdAt":"2026-10-22T10:11:12Z","modifiedAt":"2026-10-22T10:11:12Z","tasks":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"taskId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":7,"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"filesToModify":["filesToModifyItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"estimatedComplexity":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromise":"completionPromise \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","maxIterations":7,"status":7,"currentSessionId":"316f4bcd-0ee9-8da5-7c72-a350e80e1bc3","result":"result \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","iterationCount":7,"createdAt":"2026-10-22T10:11:12Z","completedAt":"2026-10-22T10:11:12Z","learnings":"learnings \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","learningsUpdatedAt":"2026-10-22T10:11:12Z","executions":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"9740fc52-a9b1-4b1d-693a-9a5f4eae7b3c","sessionId":"6e6e6fd0-2891-39a2-3b73-58ff70124550","iterationNumber":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","llmResponse":"llmResponse \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromiseFound":true,"executedAt":"2026-10-22T10:11:12Z","executionDuration":"01:02:03","error":"error \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"workflowRunId":"0d0d5caf-3746-e015-e9c9-f47f8fb0f67c","pullRequestUrl":"https://example.test/pullRequestUrl","runStatus":"runStatus \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","awaitingSignal":"awaitingSignal \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}]}""";

    private const string RunDiagnosisList = """[{"verdict":9,"scheduleId":"36d492d6-f2d1-77a9-cf06-9944d0ef887c","scheduleName":"scheduleName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","occurrenceAtUtc":"2026-10-22T10:11:12Z","stepReached":7,"failedAtStep":7,"lastError":"lastError \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","attempts":7,"deadLetterError":"deadLetterError \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","retryCount":7,"createdAtUtc":"2026-10-22T10:11:12Z","updatedAtUtc":"2026-10-22T10:11:12Z","executionId":"ee311c53-1531-cfef-a283-2787e8c1f027","enabled":true,"nextRunAtUtc":"2026-10-22T10:11:12Z","findings":"findings \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","digest":"digest \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}]""";

    private const string StartWorkflowRunResponse = """{"runId":"9b66f945-c101-c25c-7963-58116c647592"}""";

    private const string WorkflowRunList = """{"runs":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","process":"process \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","processVersion":7,"status":"status \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","currentNode":"currentNode \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","awaitingSignal":"awaitingSignal \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","startedBy":"startedBy \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","createdAt":"2026-10-22T10:11:12+00:00","updatedAt":"2026-10-22T10:11:12+00:00","taskId":"9740fc52-a9b1-4b1d-693a-9a5f4eae7b3c","taskCode":"taskCode \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","taskTitle":"taskTitle \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","startedByName":"startedByName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","failureReason":"failureReason \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","prUrl":"https://example.test/prUrl"}],"next":"next \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string WorkflowRunView = """{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","process":"process \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","processVersion":7,"status":"status \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","currentNode":"currentNode \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","awaitingSignal":"awaitingSignal \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","lastError":"lastError \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","pins":{"pinsKey":{"agentName":"agentName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","agentId":"agentId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","agentRevision":"agentRevision \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","skillName":"skillName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","skillHash":"skillHash \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}},"standingInstructionsDiff":"standingInstructionsDiff \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","nodeUsage":[{"seq":7,"node":"node \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"cacheReadTokens":7,"cacheWriteTokens":7,"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"prUrl":"https://example.test/prUrl","unreadablePullRequestLink":"unreadablePullRequestLink \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","startedBy":"startedBy \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","writeAudit":[{"seq":7,"node":"node \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","tool":"tool \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","path":"path \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","startedBy":"startedBy \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"deferredFindings":[{"id":"id \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","lens":"lens \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","file":"file \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","line":7,"title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","scenario":"scenario \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","reason":"reason \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","existingIssue":7,"dropped":true,"existingIssueUrl":"https://example.test/existingIssueUrl"}],"deferredFindingsError":"deferredFindingsError \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","filedFindings":[{"findingId":"findingId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","mode":"mode \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","issue":7,"url":"https://example.test/url"}],"steps":[{"seq":7,"kind":"kind \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","fromNode":"fromNode \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","toNode":"toNode \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","status":"status \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","outcome":"outcome \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","error":"error \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"cacheReadTokens":7,"at":"2026-10-22T10:11:12+00:00","actor":"actor \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"startedByName":"startedByName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","nodeSelections":[{"seq":7,"node":"node \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","model":"model \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","level":"level \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","keptFamilies":7,"fallback":"fallback \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","confidence":1.25}],"nodeSelectionsError":"nodeSelectionsError \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","failureReason":"failureReason \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","reviewVisits":[{"seq":7,"verdict":"verdict \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","rejectingLens":"rejectingLens \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","lenses":[{"lens":"lens \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","checked":["checkedItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"findings":[{"file":"file \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","line":7,"scenario":"scenario \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}]}],"reviewResponse":"reviewResponse \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"reviewVisitsError":"reviewVisitsError \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string CreateTaskDto = """{"projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","taskId":"taskId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":7,"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7,"estimatedComplexity":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"filesToModify":["filesToModifyItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"]}""";

    private const string UpdateTaskDto = """{"title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":7,"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7,"estimatedComplexity":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string CostSummaryDto = """{"totalInputTokens":7,"totalOutputTokens":7,"totalCacheReadTokens":7,"totalCacheWriteTokens":7,"totalCost":1.25,"totalEntries":7,"unreadableRecords":7,"excluded":{"unattributedInputTokens":7,"unattributedOutputTokens":7,"unattributedExecutionCount":7,"unpricedInputTokens":7,"unpricedOutputTokens":7,"unpricedExecutionCount":7,"unpricedModelIds":["unpricedModelIdsItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"]},"bySource":[{"key":"key \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"cacheReadTokens":7,"cacheWriteTokens":7,"cost":1.25,"entries":7}],"byModel":[{"key":"key \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"cacheReadTokens":7,"cacheWriteTokens":7,"cost":1.25,"entries":7}],"byDay":[{"day":"2026-10-22","source":"source \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"cost":1.25,"entries":7}],"scope":"scope \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string ProjectCostList = """[{"projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","projectName":"projectName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"estimatedCost":1.25,"executionCount":7,"excluded":{"unattributedInputTokens":7,"unattributedOutputTokens":7,"unattributedExecutionCount":7,"unpricedInputTokens":7,"unpricedOutputTokens":7,"unpricedExecutionCount":7,"unpricedModelIds":["unpricedModelIdsItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"]},"scope":"scope \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}]""";

    private const string TaskCostList = """[{"taskId":"9740fc52-a9b1-4b1d-693a-9a5f4eae7b3c","taskTitle":"taskTitle \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"estimatedCost":1.25,"iterationCount":7,"excluded":{"unattributedInputTokens":7,"unattributedOutputTokens":7,"unattributedExecutionCount":7,"unpricedInputTokens":7,"unpricedOutputTokens":7,"unpricedExecutionCount":7,"unpricedModelIds":["unpricedModelIdsItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"]},"scope":"scope \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}]""";

    private const string CostEstimateDto = """{"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","modelDisplayName":"modelDisplayName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","maxIterations":7,"estimatedPromptTokens":7,"estimatedResponseTokens":7,"estimatedMinCost":1.25,"estimatedMaxCost":1.25}""";

    private const string ModelPricingList = """[{"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","displayName":"displayName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputPricePerMillion":1.25,"outputPricePerMillion":1.25,"cacheReadPricePerMillion":1.25,"cacheWritePricePerMillion":1.25}]""";

    private const string CreateBrainstormSessionDto = """{"projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0"}""";

    private const string BrainstormSessionDto = """{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","phase":7,"messages":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","role":"role \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","content":"content \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","phase":7,"createdAt":"2026-10-22T10:11:12Z"}],"designDocument":"designDocument \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","implementationPlan":"implementationPlan \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","phaseCompleteSignaled":true,"createdAt":"2026-10-22T10:11:12Z","completedAt":"2026-10-22T10:11:12Z"}""";

    private const string BrainstormSessionSummaryList = """[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","phase":7,"messageCount":7,"createdAt":"2026-10-22T10:11:12Z","completedAt":"2026-10-22T10:11:12Z"}]""";

    private const string SendBrainstormMessageDto = """{"content":"content \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string BrainstormMessageDto = """{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","role":"role \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","content":"content \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","phase":7,"createdAt":"2026-10-22T10:11:12Z"}""";

    private const string TaskList = """[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"taskId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":7,"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"filesToModify":["filesToModifyItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"estimatedComplexity":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromise":"completionPromise \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","maxIterations":7,"status":7,"currentSessionId":"316f4bcd-0ee9-8da5-7c72-a350e80e1bc3","result":"result \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","iterationCount":7,"createdAt":"2026-10-22T10:11:12Z","completedAt":"2026-10-22T10:11:12Z","learnings":"learnings \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","learningsUpdatedAt":"2026-10-22T10:11:12Z","executions":[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","taskId":"9740fc52-a9b1-4b1d-693a-9a5f4eae7b3c","sessionId":"6e6e6fd0-2891-39a2-3b73-58ff70124550","iterationNumber":7,"prompt":"prompt \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","llmResponse":"llmResponse \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","completionPromiseFound":true,"executedAt":"2026-10-22T10:11:12Z","executionDuration":"01:02:03","error":"error \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","inputTokens":7,"outputTokens":7,"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}],"workflowRunId":"0d0d5caf-3746-e015-e9c9-f47f8fb0f67c","pullRequestUrl":"https://example.test/pullRequestUrl","runStatus":"runStatus \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","awaitingSignal":"awaitingSignal \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}]""";

    private const string PrdRequestDto = """{"projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","userRequirements":"userRequirements \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","context":"context \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","maxItems":7}""";

    private const string PrdResponseDto = """{"id":"id \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","productOverview":"productOverview \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","goals":["goalsItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"assumptions":["assumptionsItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"features":[{"title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","acceptanceCriteria":"acceptanceCriteria \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":3,"estimatedComplexity":2,"dependenciesCount":7,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7}],"tasks":[{"title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","acceptanceCriteria":"acceptanceCriteria \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":3,"estimatedComplexity":2,"dependenciesCount":7,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7}],"generatedAt":"2026-10-22T10:11:12Z","rawResponse":"rawResponse \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","allItems":[{"title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","acceptanceCriteria":"acceptanceCriteria \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":3,"estimatedComplexity":2,"dependenciesCount":7,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7},{"title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","acceptanceCriteria":"acceptanceCriteria \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":3,"estimatedComplexity":2,"dependenciesCount":7,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7}]}""";

    private const string PrdConversionRequestDto = """{"projectId":"84e30b95-f6d2-3553-391f-f2747a3017e0","prdItems":[{"title":"title \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","acceptanceCriteria":"acceptanceCriteria \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","priority":3,"complexity":2,"dependencies":["dependenciesItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"phase":"phase \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","parallelGroup":7}]}""";

    private const string AgentSummaryList = """[{"id":"id \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","name":"name \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","tools":["toolsItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"]}]""";

    private const string CreateAgentSessionResponseDto = """{"sessionId":"sessionId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string AgentSessionList = """[{"id":"id \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","agentId":"agentId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","ownerId":"ownerId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","state":"state \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","createdAt":"2026-10-22T10:11:12+00:00","lastActivityAt":"2026-10-22T10:11:12+00:00","turnCount":7,"totalInputTokens":7,"totalOutputTokens":7}]""";

    private const string AgentSessionDetailDto = """{"session":{"id":"id \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","agentId":"agentId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","ownerId":"ownerId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","state":"state \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","createdAt":"2026-10-22T10:11:12+00:00","lastActivityAt":"2026-10-22T10:11:12+00:00","turnCount":7,"totalInputTokens":7,"totalOutputTokens":7},"messages":[{"role":"role \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","text":"text \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","toolCalls":[{"callId":"callId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","toolName":"toolName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","argumentsJson":"argumentsJson \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","resultPreview":"resultPreview \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","succeeded":true}],"createdAt":"2026-10-22T10:11:12+00:00"}]}""";

    private const string PagedMemoryDto = """{"items":[{"id":"id \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","ownerId":"ownerId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","agentId":"agentId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","kind":"kind \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","text":"text \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","tags":["tagsItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"source":"source \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","importance":1.25,"createdAt":"2026-10-22T10:11:12+00:00","updatedAt":"2026-10-22T10:11:12+00:00","lastRecalledAt":"2026-10-22T10:11:12+00:00","recallCount":7,"isArchived":true,"indexPending":true,"isShared":true}],"total":7,"page":7,"pageSize":7}""";

    private const string MemoryDto = """{"id":"id \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","ownerId":"ownerId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","agentId":"agentId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","kind":"kind \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","text":"text \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","tags":["tagsItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"source":"source \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","importance":1.25,"createdAt":"2026-10-22T10:11:12+00:00","updatedAt":"2026-10-22T10:11:12+00:00","lastRecalledAt":"2026-10-22T10:11:12+00:00","recallCount":7,"isArchived":true,"indexPending":true,"isShared":true}""";

    private const string AgentEventDto = """{"kind":"kind \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","text":"text \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","toolCall":{"callId":"callId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","toolName":"toolName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","argumentsJson":"argumentsJson \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","resultPreview":"resultPreview \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","succeeded":true},"usage":{"inputTokens":7,"outputTokens":7,"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"},"result":{"turnId":"turnId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","text":"text \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","usage":{"inputTokens":7,"outputTokens":7,"modelId":"modelId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"},"toolCalls":[{"callId":"callId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","toolName":"toolName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","argumentsJson":"argumentsJson \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","resultPreview":"resultPreview \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","succeeded":true}],"elapsedMs":1.25},"errorCode":"errorCode \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","errorMessage":"errorMessage \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","errorDetail":"errorDetail \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","memory":{"count":7,"ids":["idsItem \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"],"chars":7,"memoryId":"memoryId \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","kind":"kind \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","deduped":true,"code":"code \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","detail":"detail \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}}""";

    private const string CreateProjectDto = """{"projectName":"projectName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","version":"version \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","repositoryUrl":"repositoryUrl \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","defaultBranch":"defaultBranch \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string UpdateProjectDto = """{"projectName":"projectName \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","version":"version \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string RepositoryConfigurationList = """[{"id":"74b70bb8-8802-a1fd-f201-890375a60c8f","name":"name \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","url":"url \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","platform":"platform \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","defaultBranch":"defaultBranch \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","authenticationMethod":"authenticationMethod \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","credentialIdentifier":"credentialIdentifier \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","isActive":true,"createdAt":"2026-10-22T10:11:12Z","lastUsedAt":"2026-10-22T10:11:12Z","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}]""";

    private const string CreateRepositoryConfigurationDto = """{"name":"name \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","url":"url \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","platform":"platform \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","defaultBranch":"defaultBranch \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","authenticationMethod":"authenticationMethod \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","credentialIdentifier":"credentialIdentifier \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";

    private const string UpdateRepositoryConfigurationDto = """{"name":"name \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","url":"url \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","defaultBranch":"defaultBranch \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","authenticationMethod":"authenticationMethod \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","credentialIdentifier":"credentialIdentifier \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E","isActive":true,"description":"description \u003C\u0026\u0027\u00E9\uD83D\uDE80\u003E"}""";
}
