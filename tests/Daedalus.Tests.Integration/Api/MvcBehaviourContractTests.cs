using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Tests.Integration.Fixtures;
using Daedalus.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Api;

/// <summary>
///     Pins the answers MVC writes on its own, before or around an action: the client-error problem a bodiless
///     <c>NotFound()</c> or <c>Unauthorized()</c> becomes, and the validation problem a body or query value that fails
///     binding becomes. Phase 3.4's minimal-API helpers, <c>ApiProblem</c>, <c>ApiBody&lt;T&gt;</c> and
///     <c>ApiQuery&lt;T&gt;</c> (ruling R7), must reproduce every value here. Every assertion reads the raw JSON through
///     <see cref="JsonDocument"/>, never a deserialized <c>ProblemDetails</c>, so an extra or missing member fails.
/// </summary>
/// <remarks>
///     <para>
///     What the brief assumed and MVC does not do is in rulings R13 to R19 of
///     <c>docs/plans/2026-10-31-phase-3.4-rulings.md</c>: <c>GET /api/projects/{unknown}</c> is not bodiless, no
///     action answers a bodiless 409, the 401 branch is reached through a subject equal to
///     <see cref="AnonymousSecurityContext.AnonymousId"/>, a top-level query parameter's message has no
///     <c>for {name}</c>, and the malformed-JSON key is the reader's path, <c>$</c> only at the root.
///     </para>
///     <para>
///     The workflow-runs cases boot <see cref="ScratchWorkflowHost"/>, because <c>WorkflowRunsEndpoints</c> answers
///     only with the engine on and the factory forbids the engine over the shared fixture database (ruling R11). Every
///     other case runs on <see cref="ApiWebApplicationFactory"/> over the shared database.
///     </para>
///     <para>
///     The <c>Content-Type</c> is pinned whole, charset included, because that is what MVC sends. The order of members
///     and of <c>errors</c> keys is not pinned: MVC writes <c>errors</c> in its model-state dictionary's order, which no
///     client can rely on (ruling R18).
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class MvcBehaviourContractTests(PostgresFixture fixture)
{
    private const string Json = "application/json; charset=utf-8";
    private const string ProblemJson = "application/problem+json; charset=utf-8";

    private const string NotFoundType = "https://tools.ietf.org/html/rfc9110#section-15.5.5";
    private const string UnauthorizedType = "https://tools.ietf.org/html/rfc9110#section-15.5.2";
    private const string BadRequestType = "https://tools.ietf.org/html/rfc9110#section-15.5.1";

    /// <summary>
    ///     The ZeroAlloc validation filter's type, MVC's and now <c>ZeroAllocValidationEndpointFilter</c>'s, which
    ///     differs from MVC's own 400 type.
    /// </summary>
    private const string FilterBadRequestType = "https://tools.ietf.org/html/rfc7231#section-6.5.1";

    private const string ValidationTitle = "One or more validation errors occurred.";
    private const string NonEmptyBody = "A non-empty request body is required.";
    private const string RequestRequired = "The request field is required.";

    private static readonly Guid Unknown = Guid.NewGuid();

    /// <summary>
    ///     How MVC's <c>JsonOptions</c> escape text: relaxed, so an apostrophe goes out as <c>'</c>, not <c>\u0027</c>.
    ///     Expected values are serialized with it and compared with the raw JSON, so the escaping is pinned too (R19).
    /// </summary>
    private static readonly JsonSerializerOptions MvcEscaping = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---------------------------------------------------------------------------------------------------------------
    // Bodiless client errors. MVC's ClientErrorResultFilter turns a bodiless 4xx into its client-error problem.
    // No action answers a bodiless 409, so there is no 409 case: every 409 in the controllers carries a body,
    // Problem(detail:...), Conflict(new { error }) or Conflict(new ProblemDetails), ruling R14.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     <c>GET /api/workflow-runs/{unknown}</c> returns a bare <c>NotFound()</c>. <c>GET /api/projects/{unknown}</c>,
    ///     the brief's example, answers <c>{ "error": ... }</c> instead and is pinned in
    ///     <see cref="Problem_content_type_is_per_controller"/>; ruling R13.
    /// </summary>
    [Fact]
    public async Task Bodiless_not_found_answers_mvc_client_error_problem()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: true);
        using var client = host.Client("alice", "developer");

        using var response = await client.GetAsync(new Uri($"/api/workflow-runs/{Unknown}", UriKind.Relative));

        // Red: StatusCodes.Status410Gone in place of Status404NotFound for an unknown run in WorkflowRunsEndpoints.Get.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        // Red: ProblemContentType.ProblemJson in place of ProblemContentType.Json on the group in
        //      MapWorkflowRunsEndpoints.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        await AssertProblemAsync(response, NotFoundType, "Not Found", 404);
    }

    /// <summary>
    ///     The 401 an action returns itself when <c>HttpSecurityContextFactory.TryCreate</c> refuses a signed-in
    ///     principal. <see cref="HeaderTestAuthHandler"/> always sets <c>sub</c>, so no principal without a subject
    ///     reaches the branch; a subject equal to <see cref="AnonymousSecurityContext.AnonymousId"/> takes the same
    ///     branch, ruling R15. Tasks has no <c>[Produces]</c>, so its problem is <c>application/problem+json</c>.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/agent-memories", Json)]
    [InlineData("POST", "/api/tasks/4252e093-c33b-47ec-b31b-84f6ba69089f/manufacture", ProblemJson)]
    public async Task Bodiless_unauthorized_answers_mvc_client_error_problem(string method, string path, string contentType)
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client(AnonymousSecurityContext.AnonymousId, "admin");

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative)));

        // Red: return ApiProblem.Of(context, StatusCodes.Status403Forbidden) instead of the 401 in
        //      AgentMemoriesEndpoints.List, and in TasksEndpoints.Manufacture.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, await response.Content.ReadAsStringAsync());
        // Red: .WithMetadata(ProblemContentType.Json) dropped from the group in
        //      AgentMemoriesEndpoints.MapAgentMemoriesEndpoints, and added to the group in
        //      TasksEndpoints.MapTasksEndpoints.
        response.Content.Headers.ContentType!.ToString().Should().Be(contentType);
        await AssertProblemAsync(response, UnauthorizedType, "Unauthorized", 401);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Body binding. [ApiController]'s ModelStateInvalidFilter answered before the action and before
    // ZeroAllocValidationFilter, with ProblemDetailsFactory's validation problem; ZeroAllocValidationEndpointFilter
    // keeps that order.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     The input formatter's error is keyed by the JSON path where the reader stopped, <c>$</c> only when it stops
    ///     at the root, ruling R17; the parameter, <c>request</c>, then fails its implicit required check.
    /// </summary>
    [Theory]
    [InlineData("{\"name\":", "$.name",
        "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be"
        + " closed. Path: $.name | LineNumber: 0 | BytePositionInLine: 8.")]
    [InlineData("[1]", "$",
        "The JSON value could not be converted to Daedalus.Application.DTOs.CreateProjectDto. Path: $ | LineNumber: 0"
        + " | BytePositionInLine: 1.")]
    public async Task Malformed_json_answers_validation_problem(string body, string pathKey, string message)
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "project-manager");

        using var response = await client.PostAsync(new Uri("/api/projects", UriKind.Relative), JsonBody(body));

        // Red: ApiProblem.BindingProblem's Status set to 422 in ApiProblem.cs.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        // Red: ProblemContentType.ProblemJson in place of ProblemContentType.Json on the group in MapProjectsEndpoints.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [pathKey] = [message],
                ["request"] = [RequestRequired],
            });
    }

    /// <summary>
    ///     No body and the JSON literal <c>null</c> answer the same: the empty-key "non-empty body" error and the
    ///     parameter's implicit required error. The request carries <c>Content-Type: application/json</c> in both.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task Empty_body_answers_non_empty_body_required(string body)
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "project-manager");

        using var response = await client.PostAsync(new Uri("/api/projects", UriKind.Relative), JsonBody(body));

        // Red: ApiProblem.BindingProblem's Status set to 422 in ApiProblem.cs.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        // Red: ProblemContentType.ProblemJson in place of ProblemContentType.Json on the group in MapProjectsEndpoints.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [""] = [NonEmptyBody],
                ["request"] = [RequestRequired],
            });
    }

    /// <summary>
    ///     MVC's cancel action bound <c>CancelWorkflowRunRequest?</c>; a nullable body parameter is optional, so an
    ///     empty body binds null and the action answers its own bodiless 404 for an unknown run.
    ///     <c>WorkflowRunsEndpoints.Cancel</c> takes an optional <c>ApiBody&lt;CancelWorkflowRunRequest&gt;?</c> for it.
    /// </summary>
    [Fact]
    public async Task Empty_body_on_a_nullable_body_parameter_binds_null()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: true);
        using var client = host.Client("alice", "developer");

        using var response = await client.PostAsync(
            new Uri($"/api/workflow-runs/{Unknown}/cancel", UriKind.Relative), JsonBody(""));

        // Red: var optional = false in place of parameter.IsOptional in ApiBody.BindAsync, so the optional body of
        //      WorkflowRunsEndpoints.Cancel is read as required; the empty body is then a 400.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        // Red: ProblemContentType.ProblemJson in place of ProblemContentType.Json on the group in
        //      MapWorkflowRunsEndpoints.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        await AssertProblemAsync(response, NotFoundType, "Not Found", 404);
    }

    // The body DTO types the 13 controllers bind, explicitly with [FromBody] or by [ApiController]'s inference, and the
    // non-nullable reference properties MVC treats as implicitly required in each. Nullable reference types are on
    // repo-wide, and SuppressImplicitRequiredAttributeForNonNullableReferenceTypes is left false, so each such property
    // carries an implicit [Required]. It fires only when the bound value is null: a property whose initializer or
    // parameterless constructor supplies a value is not refused when the JSON omits it, and an empty string reaches
    // ZeroAllocValidationEndpointFilter, see Empty_string_is_not_implicitly_required.
    //
    //   CreateProjectDto               POST api/projects                    ProjectName, Description, Version
    //   UpdateProjectDto               PUT  api/projects/{id}               ProjectName, Description, Version
    //   CreateTaskDto                  POST api/tasks                       Title, Description
    //   UpdateTaskDto                  PUT  api/tasks/{id}                  none, every member is nullable
    //   SubmitAnalysisRequest          POST api/codeanalysis                RepositoryUrl, FilePath, Title, Description,
    //                                                                       Requirements
    //   IterateAnalysisRequest         POST api/codeanalysis/{id}/iterate   AiResponse
    //   FinalizeAnalysisRequest        POST api/codeanalysis/{id}/finalize  none, one bool
    //   CreateBrainstormSessionDto     POST api/brainstorm/sessions         none, one Guid
    //   SendBrainstormMessageDto       POST api/brainstorm/sessions/{id}/messages   Content
    //   PrdRequestDto                  POST api/prd/generate                UserRequirements, only when sent as null:
    //                                                                       the parameterless constructor sets ""
    //   PrdConversionRequestDto        POST api/prd/convert-to-tasks        PrdItems
    //     PrdItemForConversionDto      each PrdItems element                Title, Description, AcceptanceCriteria,
    //                                                                       Dependencies, Phase
    //   CreateRepositoryConfigurationDto  POST api/repositories             Name, Url, Platform, DefaultBranch, only
    //                                                                       when sent as null: each has an initializer
    //   UpdateRepositoryConfigurationDto  PUT  api/repositories/{id}        Name, Url, DefaultBranch, likewise
    //   SendTurnRequestDto             POST api/agents/sessions/{id}/turns and turns/stream   Text
    //   StartWorkflowRunRequest        POST api/workflow-runs               WorkIntent, Repository
    //   ResumeWorkflowRunRequest       POST api/workflow-runs/{id}/resume   Signal
    //   CancelWorkflowRunRequest       POST api/workflow-runs/{id}/cancel   none, and the parameter itself is nullable
    //
    // One case per type, plus one for the nested PrdItemForConversionDto, whose keys carry the PrdItems[0]. prefix.
    // A type with no required member pins that its omitted members reach the action: its case expects no errors.
    public static TheoryData<string> RequiredCases => new(RequiredCaseTable.Keys.Order(StringComparer.Ordinal));

    private static readonly Dictionary<string, RequiredCase> RequiredCaseTable = new(StringComparer.Ordinal)
    {
        ["CreateProjectDto"] = Refused("POST", "/api/projects", "{}", "project-manager", Json,
            "ProjectName", "Description", "Version"),
        ["UpdateProjectDto"] = Refused("PUT", $"/api/projects/{Unknown}", "{}", "project-manager", Json,
            "ProjectName", "Description", "Version"),
        ["CreateTaskDto"] = Refused("POST", "/api/tasks", "{}", "task-manager", ProblemJson, "Title", "Description"),
        ["UpdateTaskDto"] = Passed("PUT", $"/api/tasks/{Unknown}", "{}", "task-manager", HttpStatusCode.NotFound),
        ["SubmitAnalysisRequest"] = Refused("POST", "/api/codeanalysis", "{}", "analyst", Json,
            "RepositoryUrl", "FilePath", "Title", "Description", "Requirements"),
        ["IterateAnalysisRequest"] = Refused("POST", $"/api/codeanalysis/{Unknown}/iterate", "{}", "analyst", Json,
            "AiResponse"),
        ["FinalizeAnalysisRequest"] = Passed("POST", $"/api/codeanalysis/{Unknown}/finalize", "{}", "analyst",
            HttpStatusCode.BadRequest),
        ["CreateBrainstormSessionDto"] = Passed("POST", "/api/brainstorm/sessions", "{}", "analyst",
            HttpStatusCode.BadRequest),
        ["SendBrainstormMessageDto"] = Refused("POST", $"/api/brainstorm/sessions/{Unknown}/messages", "{}", "analyst",
            Json, "Content"),
        ["PrdRequestDto"] = Refused("POST", "/api/prd/generate", "{\"userRequirements\":null}", "analyst", Json,
            "UserRequirements"),
        ["PrdConversionRequestDto"] = Refused("POST", "/api/prd/convert-to-tasks", "{}", "task-manager", Json,
            "PrdItems"),
        ["PrdItemForConversionDto"] = Refused("POST", "/api/prd/convert-to-tasks",
            $"{{\"projectId\":\"{Unknown}\",\"prdItems\":[{{}}]}}", "task-manager", Json,
            "PrdItems[0].Title", "PrdItems[0].Description", "PrdItems[0].AcceptanceCriteria",
            "PrdItems[0].Dependencies", "PrdItems[0].Phase"),
        ["CreateRepositoryConfigurationDto"] = Refused("POST", "/api/repositories",
            "{\"name\":null,\"url\":null,\"platform\":null,\"defaultBranch\":null}", "admin", Json,
            "Name", "Url", "Platform", "DefaultBranch"),
        ["UpdateRepositoryConfigurationDto"] = Refused("PUT", $"/api/repositories/{Unknown}",
            "{\"name\":null,\"url\":null,\"defaultBranch\":null}", "admin", Json, "Name", "Url", "DefaultBranch"),
        ["SendTurnRequestDto"] = Refused("POST", "/api/agents/sessions/s1/turns", "{}", "developer", Json, "Text"),
        ["StartWorkflowRunRequest"] = Refused("POST", "/api/workflow-runs", "{}", "developer", Json,
            "WorkIntent", "Repository") with
        { Workflow = true },
        ["ResumeWorkflowRunRequest"] = Refused("POST", $"/api/workflow-runs/{Unknown}/resume", "{}", "developer", Json,
            "Signal") with
        { Workflow = true },
        ["CancelWorkflowRunRequest"] = Passed("POST", $"/api/workflow-runs/{Unknown}/cancel", "{}", "developer",
            HttpStatusCode.NotFound) with
        { Workflow = true },
    };

    [Theory]
    [MemberData(nameof(RequiredCases))]
    public async Task Missing_required_property_answers_implicit_required(string dto)
    {
        var @case = RequiredCaseTable[dto];
        await using var host = await ApiHost.StartAsync(fixture, @case.Workflow);
        using var client = host.Client("alice", @case.Role);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(@case.Method),
            new Uri(@case.Path, UriKind.Relative))
        { Content = JsonBody(@case.Body) });

        if (@case.Required.Length == 0)
        {
            // Red: add a non-nullable string property, initialized to null!, to UpdateTaskDto or
            //      CancelWorkflowRunRequest; the case is then the binding 400.
            response.StatusCode.Should().Be(@case.PassedStatus, await response.Content.ReadAsStringAsync());
            using var passed = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // Red: the same property on FinalizeAnalysisRequest or CreateBrainstormSessionDto; their 400 is then the
            //      binding problem, with an errors member.
            passed.RootElement.TryGetProperty("errors", out _).Should().BeFalse("no member of {0} is required", dto);
            await AssertActionBadRequestAsync(dto, response, passed.RootElement);
            return;
        }

        // Red: ApiProblem.BindingProblem's Status set to 422 in ApiProblem.cs; every refused case answers 422.
        // Red: var required = new Dictionary<string, string[]>(StringComparer.Ordinal) in place of the
        //      ApiBinding.ImplicitRequired call in ApiBody.BindAsync. Each refused case turns red: here, as a 500,
        //      where its handler throws on the null member, and at the member set below where its handler or
        //      ZeroAllocValidationEndpointFilter answers its own 400, as StartWorkflowRunRequest's and
        //      ResumeWorkflowRunRequest's blank checks do.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        // Red: ProblemContentType.ProblemJson in place of ProblemContentType.Json on the case's group, in
        //      MapProjectsEndpoints, MapPrdEndpoints, MapBrainstormEndpoints, MapRepositoriesEndpoints,
        //      MapAgentSessionsEndpoints, MapCodeAnalysisEndpoints or MapWorkflowRunsEndpoints; for CreateTaskDto,
        //      .WithMetadata(ProblemContentType.Json) added to the group in MapTasksEndpoints.
        response.Content.Headers.ContentType!.ToString().Should().Be(@case.ContentType);
        await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
            errors: @case.Required.ToDictionary(key => key, key => new[] { $"The {key.Split('.')[^1]} field is required." },
                StringComparer.Ordinal));
    }

    /// <summary>
    ///     The 400 that FinalizeAnalysis and CreateSession answer themselves once <c>{}</c> binds: an explicit
    ///     <c>ProblemDetails</c> with no <c>type</c> and no <c>traceId</c>, and a plain <c>{ "error" }</c>. The other
    ///     no-required cases answer 404s pinned elsewhere, UpdateTaskDto's as Tasks' <c>{ "error" }</c> shape and
    ///     CancelWorkflowRunRequest's as the bodiless 404.
    /// </summary>
    private static async Task AssertActionBadRequestAsync(string dto, HttpResponseMessage response, JsonElement root)
    {
        var text = root.GetRawText();
        switch (dto)
        {
            case "FinalizeAnalysisRequest":
                // Red: .WithMetadata(ProblemContentType.Json) changed to ProblemContentType.ProblemJson in MapCodeAnalysisEndpoints.
                response.Content.Headers.ContentType!.ToString().Should().Be(Json);
                // Red: Type = "x" set on the ProblemDetails built in CodeAnalysisEndpoints.Problem.
                root.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal).Should()
                    .Equal(["detail", "status", "title"], text);
                // Red: the title "Finalization Error" changed in the Problem call of CodeAnalysisEndpoints.FinalizeAnalysis.
                root.EnumerateObject().Select(member => member.Name + "=" + member.Value.GetRawText())
                    .Order(StringComparer.Ordinal).Should().Equal(
                        "detail=" + JsonSerializer.Serialize($"Analysis request {Unknown} not found", MvcEscaping),
                        "status=400",
                        "title=\"Finalization Error\"");
                break;
            case "CreateBrainstormSessionDto":
                // Red: ApiJsonResult.ContentType changed to text/json; CreateSession's 400 is written through it.
                response.Content.Headers.ContentType!.ToString().Should().Be(Json);
                // Red: an extra member in CreateSession's ErrorBody.
                root.EnumerateObject().Select(member => member.Name).Should().Equal(["error"], text);
                // Red: CreateSession answers BadRequest with a changed error text.
                root.GetProperty("error").GetString().Should()
                    .Be("Project not found: Project 00000000-0000-0000-0000-000000000000 not found");
                break;
            default:
                break;
        }
    }

    /// <summary>
    ///     The implicit required check fires on null only: an empty string is bound, and the ZeroAlloc.Validation filter
    ///     refuses it with its own message and its own problem shape, which has a <c>detail</c> and no <c>traceId</c>.
    /// </summary>
    [Fact]
    public async Task Empty_string_is_not_implicitly_required()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "project-manager");

        using var response = await client.PostAsync(new Uri("/api/projects", UriKind.Relative),
            JsonBody("{\"projectName\":\"\",\"description\":\"d\",\"version\":\"v\"}"));

        // Red: ZeroAllocValidationEndpointFilter.ToProblem sets Status to 422 in place of 400.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        // Red: ProblemContentType.ProblemJson in place of ProblemContentType.Json on the group in MapProjectsEndpoints.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        // Red: ZeroAllocValidationEndpointFilter.ToProblem sets the rfc9110 400 type, in place of the rfc7231 one.
        await AssertProblemAsync(response, FilterBadRequestType, ValidationTitle, 400,
            detail: "Project name is required.",
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["ProjectName"] = ["Project name is required."] },
            traceId: false);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Query binding. A top-level parameter's message is the non-property one, "The value '{raw}' is not valid.", with
    // no "for {name}"; ruling R16.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/schedules/4252e093-c33b-47ec-b31b-84f6ba69089f/runs?take=abc", "agent-user", "take")]
    [InlineData("/api/brainstorm/sessions?projectId=abc", "analyst", "projectId")]
    public async Task Malformed_query_value_answers_validation_problem(string path, string role, string key)
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", role);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        // Red: ApiProblem.BindingProblem's Status set to 422 in ApiProblem.cs.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        // Red: SchedulesEndpoints' or BrainstormEndpoints' ProblemContentType.Json changed to new ProblemContentType("text/json"); the row of that path fails.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { [key] = ["The value 'abc' is not valid."] });
    }

    /// <summary>
    ///     Query values MVC binds that a plain <c>int.TryParse</c> does not: <c>Int32Converter</c> reads <c>0x</c>,
    ///     <c>&amp;h</c> and <c>#</c> as hexadecimal, and a repeated key binds its first value. The bound value is read
    ///     back from the substituted diagnostics; ruling R37.
    /// </summary>
    [Theory]
    [InlineData("?take=0x10", 16)]
    [InlineData("?take=%26H10", 16)]
    [InlineData("?take=%2310", 16)]
    [InlineData("?take=1&take=2", 1)]
    public async Task Query_value_binds_as_mvcs_type_converter_reads_it(string query, int take)
    {
        var diagnostics = Substitute.For<IScheduleDiagnostics>();
        diagnostics.GetRunHistoryAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<RunDiagnosis>>([]));
        await using var host = await ApiHost.StartAsync(fixture, workflow: false, configureServices: services =>
        {
            services.RemoveAll<IScheduleDiagnostics>();
            services.AddScoped(_ => diagnostics);
        });
        using var client = host.Client("alice", "agent-user");

        using var response = await client.GetAsync(new Uri($"/api/schedules/{Unknown}/runs{query}", UriKind.Relative));

        // Red: return TypedResults.StatusCode(410) instead of Ok in SchedulesEndpoints.GetRunHistory.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        // Red: declare take as a string in GetRunHistory and read it with int.TryParse; the three hexadecimal cases
        //      then pass 0.
        await diagnostics.Received(1).GetRunHistoryAsync(Unknown, take, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The query refusals besides the unparsable value: an empty value is <c>ValueMustNotBeNull</c>'s "is invalid",
    ///     a repeated key quotes every value comma-joined, and a hexadecimal prefix with nothing or a sign after it
    ///     fails inside <c>Convert</c> with an <c>ArgumentException</c>, which MVC reports with no text of its own as
    ///     "The input was not valid."; ruling R37.
    /// </summary>
    [Theory]
    [InlineData("?take=", "The value '' is invalid.")]
    [InlineData("?take=abc&take=1", "The value 'abc,1' is not valid.")]
    [InlineData("?take=%23", "The input was not valid.")]
    [InlineData("?take=0x-10", "The input was not valid.")]
    public async Task Query_edge_value_answers_validation_problem(string query, string message)
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "agent-user");

        using var response = await client.GetAsync(new Uri($"/api/schedules/{Unknown}/runs{query}", UriKind.Relative));

        // Red: ApiProblem.BindingProblem's Status set to 422 in ApiProblem.cs.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        // Red: SchedulesEndpoints' ProblemContentType.Json changed to ProblemContentType.ProblemJson.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        // Red: ApiQuery.BindAsync's "is invalid." and "is not valid." texts swapped; the empty and the repeated case
        //      turn red. QueryValue.Parse ignoring HexDigitsAfterPrefix turns the two "input was not valid" cases red.
        await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["take"] = [message] });
    }

    /// <summary>
    ///     A Guid query parameter sent empty is refused by <c>ValueMustNotBeNull</c> as "The value '' is invalid.", as the
    ///     <c>take</c> case above pins for an int; ruling R37.
    /// </summary>
    [Fact]
    public async Task Empty_guid_query_value_answers_is_invalid_validation_problem()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "analyst");

        using var response = await client.GetAsync(new Uri("/api/brainstorm/sessions?projectId=", UriKind.Relative));

        // Red: ApiProblem.BindingProblem's Status set to 422 in ApiProblem.cs.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        // Red: BrainstormEndpoints' ProblemContentType.Json changed to new ProblemContentType("text/json").
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        // Red: ApiQuery.BindAsync binds an empty Guid value as Guid.Empty, or answers it with "The value '' is not valid.".
        await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["projectId"] = ["The value '' is invalid."] });
    }

    /// <summary>
    ///     The run list's query values sent empty or blank bind as if they were absent: MVC's <c>SimpleTypeModelBinder</c>
    ///     binds an empty value null for the nullable <c>int? take</c>, refusing it only for a non-nullable value type
    ///     as the <c>take</c> case above shows, and <c>ConvertEmptyStringToNull</c> binds the strings null. The list then
    ///     reads the store with no filter and the default page of 25; ruling R53.
    /// </summary>
    [Theory]
    [InlineData("?take=")]
    [InlineData("?take=%20")]
    [InlineData("?status=")]
    [InlineData("?status=%20")]
    [InlineData("?status=&process=&after=&take=&signal=&updatedSince=")]
    public async Task Empty_run_list_query_value_binds_as_absent(string query)
    {
        var runs = Substitute.For<IWorkflowRunQuery>();
        WorkflowRunFilter? filter = null;
        runs.ListAsync(Arg.Do<WorkflowRunFilter>(f => filter = f), Arg.Any<CancellationToken>())
            .Returns(new WorkflowRunPage([], null));
        await using var host = await ApiHost.StartAsync(fixture, workflow: true, configureServices: services =>
        {
            services.RemoveAll<IWorkflowRunQuery>();
            services.AddSingleton(runs);
        });
        using var client = host.Client("alice", "developer");

        using var response = await client.GetAsync(new Uri($"/api/workflow-runs{query}", UriKind.Relative));

        // Red: for the take rows, the parameter.IsOptional test dropped from the blank arm of ApiQuery.BindAsync, so
        //      the optional take of WorkflowRunsEndpoints.List refuses an empty value as a non-nullable int does. For
        //      the status rows, the typeof(T) == typeof(string) test dropped from the same arm, so a blank string is
        //      refused as a value type is. Each row then answers 400.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        // Red: DefaultTake set to 20 in WorkflowRunsEndpoints; every row then reads a page of 20.
        filter.Should().BeEquivalentTo(new WorkflowRunFilter { Take = 25 });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Unsupported media type. No input formatter reads the body, so UnsupportedContentTypeFilter answers a bodiless
    // 415 before ModelStateInvalidFilter, and ClientErrorResultFilter turns it into the client-error problem. A charset
    // the formatter does not read, known or not, is refused the same way; R28.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("projects-text-plain", "text/plain")]
    [InlineData("tasks-text-plain", "text/plain")]
    [InlineData("projects-latin1", "application/json; charset=iso-8859-1")]
    [InlineData("projects-unknown-charset", "application/json; charset=no-such-charset")]
    [InlineData("projects-utf8-unhyphenated", "application/json; charset=utf8")]
    [InlineData("projects-no-content-type", null)]
    public async Task Unreadable_body_answers_415_problem(string name, string? contentType)
    {
        var (path, role, expectedContentType) = name.StartsWith("tasks", StringComparison.Ordinal)
            ? ("/api/tasks", "task-manager", ProblemJson)
            : ("/api/projects", "project-manager", Json);
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", role);

        using var response = await client.PostAsync(new Uri(path, UriKind.Relative), RawBody(ProjectBody, contentType));

        // Red: ApiBinding.TrySelectEncoding's fallback arm `_ => null!` changed to Encoding.UTF8; the charset cases of
        //      the Projects rows then answer 201. For both text/plain rows: MediaTypeHeaderValue.Parse("text/plain")
        //      added to ApiBinding.JsonMediaTypes; the tasks row then answers the validation 400.
        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType, await response.Content.ReadAsStringAsync());
        // Red: ProblemContentType.ProblemJson in place of ProblemContentType.Json on the group in MapProjectsEndpoints, and
        //      .WithMetadata(ProblemContentType.Json) added to the group in MapTasksEndpoints.
        response.Content.Headers.ContentType!.ToString().Should().Be(expectedContentType);
        await AssertProblemAsync(response, "https://tools.ietf.org/html/rfc9110#section-15.5.16", "Unsupported Media Type", 415);
    }

    /// <summary>
    ///     Charsets MVC reads: <c>ucs-2</c>, which <c>Encoding.GetEncoding</c> resolves to UTF-16, with a UTF-16 body,
    ///     and a quoted <c>"utf-8"</c>; R28.
    /// </summary>
    [Theory]
    [InlineData("ucs-2")]
    [InlineData("\"utf-8\"")]
    public async Task Charset_mvc_resolves_is_read(string charset)
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "project-manager");
        var encoding = string.Equals(charset, "ucs-2", StringComparison.Ordinal) ? Encoding.Unicode : Encoding.UTF8;
        using var content = new ByteArrayContent(encoding.GetBytes(ProjectBody));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=" + charset);

        using var response = await client.PostAsync(new Uri("/api/projects", UriKind.Relative), content);

        // Red: ApiBinding.TrySelectEncoding without its utf-16 arm; the ucs-2 case is then refused with 415. Clearing the
        //      quote removal of the charset in the same method turns the quoted utf-8 case red.
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Content type. A problem is application/json on the controllers with [Produces("application/json")] (#366) and
    // application/problem+json on Tasks and on the stream action, whose own [Produces] overrides its controller's.
    // A plain { "error" } body is application/json everywhere, Tasks included: the per-controller rule is about
    // ProblemDetails only.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("tasks-problem")]
    [InlineData("tasks-error-body")]
    [InlineData("projects-error-body")]
    [InlineData("stream-problem")]
    public async Task Problem_content_type_is_per_controller(string name)
    {
        var (method, path, role, body, contentType, status) = name switch
        {
            "tasks-problem" => ("POST", $"/api/tasks/{Unknown}/manufacture", "admin", null, ProblemJson, HttpStatusCode.NotFound),
            "tasks-error-body" => ("GET", $"/api/tasks/{Unknown}", "task-manager", null, Json, HttpStatusCode.NotFound),
            "projects-error-body" => ("GET", $"/api/projects/{Unknown}", "project-manager", null, Json, HttpStatusCode.NotFound),
            "stream-problem" => ("POST", "/api/agents/sessions/s1/turns/stream", "developer", "{}", ProblemJson,
                HttpStatusCode.BadRequest),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown case"),
        };
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", role);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonBody(body);
        }

        using var response = await client.SendAsync(request);

        // Red: StatusCodes.Status410Gone in place of Status404NotFound in TasksEndpoints.TaskNotFound, and in
        //      TasksEndpoints.Manufacture's TaskNotFound case, ProjectNotFound answering 410 in
        //      ProjectsEndpoints; for the stream, ApiProblem.BindingProblem's Status set to 422 in ApiProblem.cs.
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        // Red: .WithMetadata(ProblemContentType.Json) added to the group in MapTasksEndpoints turns tasks-problem.
        //      ApiJsonResult.ContentType changed to application/problem+json turns projects-error-body and
        //      tasks-error-body. ProblemContentType.ProblemJson dropped from the stream route's WithMetadata in
        //      MapAgentSessionsEndpoints turns stream-problem application/json.
        response.Content.Headers.ContentType!.ToString().Should().Be(contentType);
        switch (name)
        {
            case "tasks-problem":
                await AssertProblemAsync(response, NotFoundType, "Not Found", 404, detail: $"Task {Unknown} was not found.");
                break;
            case "tasks-error-body":
                await AssertErrorBodyAsync(response, $"Task with ID {Unknown} not found");
                break;
            case "projects-error-body":
                await AssertErrorBodyAsync(response, $"Project with ID {Unknown} not found");
                break;
            default:
                await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
                    errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["Text"] = ["The Text field is required."] });
                break;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Serialization and headers. MVC writes null members, sends an absolute Location built from the action's route,
    // with the class-name case [controller] gives, adds no API-version header and serves JSON whatever the Accept.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     MVC's <c>JsonOptions</c> keep their default <c>DefaultIgnoreCondition</c>, <c>Never</c>, so a null member is
    ///     written. The endpoints take nulls, naming and case from the Http <c>JsonOptions</c>, whose
    ///     <c>DefaultIgnoreCondition</c> is <c>Never</c> too; the context in their chain only resolves types. A port
    ///     that sets <c>WhenWritingNull</c> on those options would drop the member; rulings R20 and R25.
    /// </summary>
    [Fact]
    public async Task Null_properties_are_written()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "project-manager");
        var id = await CreateProjectAsync(client);

        using var response = await client.GetAsync(new Uri($"/api/projects/{id}", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        // Red: return Error("x", 410) instead of TypedResults.Ok for a found project in ProjectsEndpoints.GetProjectById.
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        // Red: DefaultIgnoreCondition set to WhenWritingNull on the Http JsonOptions in AddDaedalusEndpoints.
        text.Should().Contain("\"modifiedAt\":null");
    }

    /// <summary>
    ///     Every 201 the API answers, with the <c>Location</c> MVC writes. <c>CreatedAtAction</c> writes it absolute,
    ///     <c>http://localhost</c> under TestServer, in the route's own case, which for <c>api/[controller]</c> is the
    ///     class name's, <c>/api/Tasks/{id}</c>, not the lower-case path the client posted to. The one 201 written with
    ///     <c>Created</c> and a relative URI, <c>POST /api/tasks/{id}/manufacture</c>, stays relative; ruling R21. The
    ///     two workflow cases boot <see cref="ScratchWorkflowHost"/> (R11).
    /// </summary>
    [Theory]
    [InlineData("projects")]
    [InlineData("tasks")]
    [InlineData("repositories")]
    [InlineData("codeanalysis")]
    [InlineData("agent-sessions")]
    [InlineData("workflow-runs")]
    [InlineData("manufacture")]
    public async Task Created_location_is_exactly_mvcs(string name)
    {
        var workflow = name is "workflow-runs" or "manufacture";
        var sessionId = SessionId.New();
        var runtime = Substitute.For<IAgentRuntime>();
        runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ZeroAlloc.Results.Result<SessionId, AgentError>>(
                ZeroAlloc.Results.Result<SessionId, AgentError>.Success(sessionId)));
        await using var host = await ApiHost.StartAsync(fixture, workflow, runtime);
        using var client = host.Client("alice", "admin", "developer");

        var (path, body, idMember, location) = name switch
        {
            "projects" => ("/api/projects", ProjectBody, "id", "http://localhost/api/Projects/{0}"),
            "tasks" => ("/api/tasks", TaskBody(await CreateProjectAsync(client)), "id", "http://localhost/api/Tasks/{0}"),
            "repositories" => ("/api/repositories",
                "{\"name\":\"r\",\"url\":\"https://example.com/r.git\",\"platform\":\"GitHub\",\"defaultBranch\":\"main\"}",
                "id", "http://localhost/api/Repositories/{0}"),
            // ["r"] on purpose: an empty or omitted requirements list answers 500 today, #428.
            "codeanalysis" => ("/api/codeanalysis",
                "{\"repositoryUrl\":\"https://example.com/r.git\",\"filePath\":\"a.cs\",\"type\":1,\"title\":\"t\","
                + "\"description\":\"d\",\"requirements\":[\"r\"]}",
                "requestId", "http://localhost/api/CodeAnalysis/{0}"),
            "agent-sessions" => ($"/api/agents/{ArchitectAgentId}/sessions", null, "sessionId",
                "http://localhost/api/agents/sessions/{0}"),
            "workflow-runs" => ("/api/workflow-runs",
                $"{{\"workIntent\":\"Tighten a guard.\",\"repository\":\"{ScratchWorkflowHost.Repository}\"}}", "runId",
                "http://localhost/api/workflow-runs/{0}"),
            "manufacture" => ($"/api/tasks/{await SeedTaskAsync(host.Workflow!)}/manufacture", null, "runId",
                "/api/workflow-runs/{0}"),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown case"),
        };

        using var response = await client.PostAsync(new Uri(path, UriKind.Relative), body is null ? null : JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        // Red: for the repositories case, InternalError in place of Created in CreateRepository; for the codeanalysis case,
        //      Problem(context, StatusCodes.Status410Gone, "x", "x") in place of TypedResults.Created in
        //      CodeAnalysisEndpoints.SubmitAnalysis.
        //      For the tasks case, ApiProblem.Of(context, StatusCodes.Status410Gone) in place of TypedResults.Created in
        //      TasksEndpoints.CreateTask, and for the manufacture case the same in place of TypedResults.Created in
        //      TasksEndpoints.Manufacture.
        //      For the agent-sessions case, return AgentError.Validation("x").ToProblem(context) when result.IsSuccess,
        //      ahead of the Created in AgentSessionsEndpoints.CreateSession. For the workflow-runs case,
        //      ApiProblem.Of(context, StatusCodes.Status410Gone) in place of TypedResults.Created in
        //      WorkflowRunsEndpoints.Start.
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        using var document = JsonDocument.Parse(text);
        var id = document.RootElement.GetProperty(idMember).GetString();
        // Red: per case, each changing one thing the Location has. The projects case turns red with the lower-case
        //      "/api/projects/{id}" in place of "/api/Projects/{id}" in ProjectsEndpoints.CreateProject, and the
        //      repositories case with "/api/repositories/{id}" in place of "/api/Repositories/{id}" in
        //      RepositoriesEndpoints.CreateRepository. The tasks case
        //      turns red with "/api/tasks/{id}" in place of "/api/Tasks/{id}" in TasksEndpoints.CreateTask, and the
        //      codeanalysis case with "/api/codeanalysis/{id}" in place of "/api/CodeAnalysis/{id}" in
        //      CodeAnalysisEndpoints.SubmitAnalysis. The agent-sessions case turns red with
        //      "/api/agents/Sessions/{sessionId}" in place of "/api/agents/sessions/{sessionId}" in
        //      AgentSessionsEndpoints.CreateSession, the workflow-runs case with the relative
        //      $"/api/workflow-runs/{result.Value}" in place of ApiLocation.For in WorkflowRunsEndpoints.Start, and the
        //      manufacture case with
        //      ApiLocation.For(context.Request, $"/api/workflow-runs/{result.Value}") in place of the relative path in
        //      TasksEndpoints.Manufacture's Created.
        response.Headers.Location!.OriginalString.Should()
            .Be(string.Format(System.Globalization.CultureInfo.InvariantCulture, location, id));
    }

    /// <summary>
    ///     Versioning was inert on MVC: <c>AddApiVersioning</c> ran without <c>.AddMvc()</c>, so the
    ///     <c>[ApiVersion("1.0")]</c> attributes were never read and <c>ReportApiVersions</c> had nothing to report.
    ///     The owner kept the wire unversioned (2026-10-31), and Asp.Versioning.Mvc left with MVC (R54).
    /// </summary>
    [Fact]
    public async Task Responses_carry_no_version_header()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "project-manager");

        using var response = await client.GetAsync(new Uri("/api/projects", UriKind.Relative));

        // Red: return Error("x", 410) instead of TypedResults.Ok in ProjectsEndpoints.GetAllProjects.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        // Red: an endpoint filter in MapApiGroup that sets the api-supported-versions header to 1.0.
        response.Headers.Contains("api-supported-versions").Should().BeFalse();
    }

    /// <summary>
    ///     A version the API does not have, by query string or by header, is served exactly like the same request
    ///     without one, since nothing checks it (owner, 2026-10-31).
    /// </summary>
    [Theory]
    [InlineData("query")]
    [InlineData("header")]
    public async Task Unsupported_version_answers_as_today(string carrier)
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "project-manager");
        using var plain = await client.GetAsync(new Uri("/api/projects", UriKind.Relative));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(
            string.Equals(carrier, "query", StringComparison.Ordinal) ? "/api/projects?api-version=2.0" : "/api/projects", UriKind.Relative));
        if (string.Equals(carrier, "header", StringComparison.Ordinal))
        {
            request.Headers.Add("x-api-version", "2.0");
        }

        using var versioned = await client.SendAsync(request);

        // Status, Content-Type and body in one line each, so a difference in any of them shows in the message.
        // Red: an endpoint filter in MapApiGroup that answers ApiProblem.Of 400 for api-version 2.0, in the query or
        //      in x-api-version; the versioned answer then differs from the plain one.
        (await Answer(versioned)).Should().Be(await Answer(plain));
        // Red: return Error("x", 410) instead of TypedResults.Ok in ProjectsEndpoints.GetAllProjects; the plain answer, which the
        //      line above compares with, is then no 200.
        plain.StatusCode.Should().Be(HttpStatusCode.OK);

        static async Task<string> Answer(HttpResponseMessage response) =>
            $"{(int)response.StatusCode}\n{response.Content.Headers.ContentType}\n{await response.Content.ReadAsStringAsync()}";
    }

    /// <summary>MVC has no XML output formatter and <c>ReturnHttpNotAcceptable</c> is false: JSON, never 406.</summary>
    [Fact]
    public async Task Xml_accept_still_gets_json()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "project-manager");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/projects", UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await client.SendAsync(request);

        // Red: an endpoint filter in MapApiGroup that answers ApiProblem.Of 406 when Accept names application/xml.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        // Red: GetAllProjects widened to return TypedResults.Content with application/xml when the request accepts it.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Pipeline order. Authentication, the rate limiter and authorization are middleware and answer before MVC binds
    // or validates the body: an anonymous or over-budget request never sees the 400 its body would earn.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The <c>{}</c> body earns the implicit-required 400, but the challenge answers first.</summary>
    [Fact]
    public async Task Unauthenticated_comes_before_validation()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.AnonymousClient();

        using var response = await client.PostAsync(new Uri("/api/projects", UriKind.Relative), JsonBody("{}"));

        // Red: .AllowAnonymous() on the CreateProject route in MapProjectsEndpoints; the binding 400 then answers.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     <c>write-operations</c> is one fixed window of 30 permits a minute for the whole host, not partitioned, and
    ///     the host reads no setting that lowers it, so the test spends 30 permits on requests that each earn the binding
    ///     400, then sends the same request again. Each test boots its own host, so no other test shares the window,
    ///     and the window opens at the first request, so 31 requests fit in it; ruling R23.
    /// </summary>
    [Fact]
    public async Task Rate_limit_comes_before_validation()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "project-manager");

        for (var i = 0; i < WriteOperationPermits; i++)
        {
            using var spent = await client.PostAsync(new Uri("/api/projects", UriKind.Relative), JsonBody("{}"));
            // Red: write-operations' PermitLimit lowered to 29 in Program.cs; request 30 is then a 429.
            spent.StatusCode.Should().Be(HttpStatusCode.BadRequest, "request {0} is within the budget", i + 1);
        }

        using var refused = await client.PostAsync(new Uri("/api/projects", UriKind.Relative), JsonBody("{}"));

        // Red: .RequireRateLimiting("write-operations") removed from the CreateProject route in MapProjectsEndpoints; the
        //      request is then the binding 400.
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, await refused.Content.ReadAsStringAsync());
        // Red: OnRejected sets "application/problem+json; charset=utf-8".
        refused.Content.Headers.ContentType!.ToString().Should().Be("application/problem+json");
        // Red: OnRejected's detail text changed in Program.cs.
        (await refused.Content.ReadAsStringAsync()).Should().Be(
            """{"type":"https://tools.ietf.org/html/rfc6585#section-4","title":"Too Many Requests","status":429,"detail":"Rate limit exceeded. Please retry after a short delay."}""");
    }

    /// <summary>
    ///     One of the 14 <c>catch (Exception)</c> blocks (#426): the service throws, the action logs and answers its
    ///     own 500, so neither the developer exception page nor <c>GlobalExceptionHandler</c> sees the exception.
    /// </summary>
    [Fact]
    public async Task Catch_all_answers_500_error_body()
    {
        var costs = Substitute.For<ICostAnalyticsService>();
        costs.GetSummaryAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        await using var host = await ApiHost.StartAsync(fixture, workflow: false, configureServices: services =>
        {
            services.RemoveAll<ICostAnalyticsService>();
            services.AddScoped(_ => costs);
        });
        using var client = host.Client("alice", "task-manager");

        using var response = await client.GetAsync(new Uri("/api/cost-analytics/summary", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        // Red: statusCode 502 in place of 500 in CostAnalyticsEndpoints.InternalServerError.
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, text);
        // Red: ProblemContentType.Json on the CostAnalytics group changed to ProblemJson, or the group's Json metadata removed.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        // Red: the ErrorBody gains a member, or its text changes, in CostAnalyticsEndpoints.InternalServerError.
        text.Should().Be("""{"error":"Internal server error"}""");
    }

    /// <summary>
    ///     <c>EstimateCost</c> answers a failed estimate with an explicit <c>ProblemDetails</c> carrying a title, a
    ///     detail and a status: MVC writes it as given, with no <c>type</c> and no <c>traceId</c>, under the
    ///     controller's <c>application/json</c> (ruling R43).
    /// </summary>
    [Fact]
    public async Task Estimate_of_an_unpriced_model_answers_the_explicit_not_found_problem()
    {
        var costs = Substitute.For<ICostAnalyticsService>();
        costs.EstimateCostAsync("unpriced", 10, 4000, Arg.Any<CancellationToken>())
            .Returns(Result<CostEstimateDto>.Failure("No pricing configured for model 'unpriced'"));
        await using var host = await ApiHost.StartAsync(fixture, workflow: false, configureServices: services =>
        {
            services.RemoveAll<ICostAnalyticsService>();
            services.AddScoped(_ => costs);
        });
        using var client = host.Client("alice", "task-manager");

        using var response = await client.GetAsync(new Uri("/api/cost-analytics/estimate?modelId=unpriced", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        // Red: StatusCode(410) in place of NotFound in CostAnalyticsEndpoints.EstimateCost.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, text);
        // Red: ProblemContentType.ProblemJson in place of Json on the CostAnalytics group.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        // Red: ApiProblem.Of in place of ApiProblem.AsGiven in EstimateCost; the body gains type and traceId.
        text.Should().Be("""{"title":"Not Found","status":404,"detail":"No pricing configured for model 'unpriced'"}""");
    }

    /// <summary>
    ///     A non-nullable <c>string</c> query parameter is implicitly required under MVC: sent absent or empty it
    ///     is a 400 validation problem with MVC's factory defaults (ruling R43).
    /// </summary>
    [Theory]
    [InlineData("/api/cost-analytics/estimate")]
    [InlineData("/api/cost-analytics/estimate?modelId=")]
    public async Task Estimate_without_a_model_id_answers_the_required_validation_problem(string path)
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "task-manager");

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        // Red: remove [Required] from the modelId parameter of CostAnalyticsEndpoints.EstimateCost; the service is asked for null.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        // Red: ProblemContentType.ProblemJson in place of Json on the CostAnalytics group.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["modelId"] = ["The modelId field is required."] });
    }

    /// <summary>
    ///     The same rule for <c>GET api/codeanalysis/by-repository</c>: a missing, empty or whitespace
    ///     <c>repositoryUrl</c> never reaches the action and is the 400 validation problem (ruling R52).
    /// </summary>
    [Theory]
    [InlineData("/api/codeanalysis/by-repository")]
    [InlineData("/api/codeanalysis/by-repository?repositoryUrl=")]
    [InlineData("/api/codeanalysis/by-repository?repositoryUrl=%20%20")]
    public async Task By_repository_without_a_url_answers_the_required_validation_problem(string path)
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "analyst");

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        // Red: ApiQuery<string> repositoryUrl in place of [Required] ApiQuery<string> repositoryUrl in GetByRepository.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        // Red: ProblemContentType.ProblemJson in place of Json in MapCodeAnalysisEndpoints.
        response.Content.Headers.ContentType!.ToString().Should().Be(Json);
        // Red: $"The {name} is required." in place of $"The {name} field is required." in ApiBinding.Required.
        await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["repositoryUrl"] = ["The repositoryUrl field is required."],
            });
    }

    /// <summary>
    ///     MVC holds every parameter's binding errors in one model state, so a missing <c>modelId</c> and a malformed
    ///     <c>maxIterations</c> answer together, in parameter order (ruling R43).
    /// </summary>
    [Fact]
    public async Task Estimate_without_a_model_id_and_with_a_malformed_value_answers_both_errors_in_parameter_order()
    {
        await using var host = await ApiHost.StartAsync(fixture, workflow: false);
        using var client = host.Client("alice", "task-manager");

        using var response = await client.GetAsync(new Uri("/api/cost-analytics/estimate?maxIterations=abc", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        // Red: remove [Required] from the modelId parameter of EstimateCost; only maxIterations is reported.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("errors").EnumerateObject()
            .Select(error => error.Name + "=" + error.Value.GetRawText())
            .Should().Equal(
                "modelId=[\"The modelId field is required.\"]",
                "maxIterations=[\"The value 'abc' is not valid.\"]");
        await AssertProblemAsync(response, BadRequestType, ValidationTitle, 400,
            errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["modelId"] = ["The modelId field is required."],
                ["maxIterations"] = ["The value 'abc' is not valid."],
            });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The agent declared in <c>src/Daedalus.Api/appsettings.json</c>.</summary>
    private const string ArchitectAgentId = "01M05YCM7DPKRG9X04870B2JYH";

    /// <summary><c>write-operations</c>' permits per window, as <c>Program.cs</c> configures it.</summary>
    private const int WriteOperationPermits = 30;

    private const string ProjectBody = "{\"projectName\":\"p\",\"description\":\"d\",\"version\":\"1.0\"}";

    private static string TaskBody(Guid projectId) =>
        $"{{\"projectId\":\"{projectId}\",\"title\":\"t\",\"description\":\"d\",\"priority\":1,\"estimatedComplexity\":1}}";

    /// <summary>Seeds a project on the scratch remote with one task, as the manufacture tests do, and answers its id.</summary>
    private static async Task<Guid> SeedTaskAsync(ScratchWorkflowHost host)
    {
        var project = Project.Create(Guid.NewGuid(), "Sandbox", "The scratch remote", repositoryUrl: host.Remote.Url).Value;
        var task = IntegrationTestFactory.CreateTask(projectId: project.Id);
        if (project.AddTask(task).IsFailure)
        {
            throw new InvalidOperationException("the seeded project refused its task");
        }

        await StoreSeed.AddProjectAsync(host.Factory.Services.GetRequiredService<DaedalusDb>(), project);
        return task.Id;
    }

    /// <summary>Creates a project through the API and answers its id.</summary>
    private static async Task<Guid> CreateProjectAsync(HttpClient client)
    {
        using var response = await client.PostAsync(new Uri("/api/projects", UriKind.Relative), JsonBody(ProjectBody));
        var text = await response.Content.ReadAsStringAsync();
        // Red: return TypedResults.Ok instead of TypedResults.Created in ProjectsEndpoints.CreateProject.
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    ///     Asserts a problem body: the exact member set, then <c>type</c>, <c>title</c>, <c>status</c> and
    ///     <c>detail</c> as raw JSON, then <c>errors</c> key for key, then <c>traceId</c> as a non-empty string.
    /// </summary>
    private static async Task AssertProblemAsync(
        HttpResponseMessage response, string type, string title, int status, string? detail = null,
        IReadOnlyDictionary<string, string[]>? errors = null, bool traceId = true)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        var expectedMembers = new List<string> { "status", "title", "type" };
        if (detail is not null)
        {
            expectedMembers.Add("detail");
        }

        if (errors is not null)
        {
            expectedMembers.Add("errors");
        }

        if (traceId)
        {
            expectedMembers.Add("traceId");
        }

        // Red: AddProblemDetails' CustomizeProblemDetails adds an "extra" extension to every problem.
        root.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal).Should()
            .Equal(expectedMembers.Order(StringComparer.Ordinal), text);

        var expectedValues = new List<string>
        {
            "status=" + status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "title=" + JsonSerializer.Serialize(title, MvcEscaping),
            "type=" + JsonSerializer.Serialize(type, MvcEscaping),
        };
        if (detail is not null)
        {
            expectedValues.Add("detail=" + JsonSerializer.Serialize(detail, MvcEscaping));
        }

        // Red: CustomizeProblemDetails sets every problem's Title to "x"; for the filter's shape,
        //      ZeroAllocValidationEndpointFilter.ToProblem's Title changed.
        root.EnumerateObject()
            .Where(member => member.Name is not ("errors" or "traceId"))
            .Select(member => member.Name + "=" + member.Value.GetRawText())
            .Order(StringComparer.Ordinal).Should()
            .Equal(expectedValues.Order(StringComparer.Ordinal), text);

        if (errors is not null)
        {
            // Each key with its messages as raw JSON, so a message that is not a string fails too; keys sorted, since
            // their order is not pinned (R18), messages in the order MVC wrote them.
            var actualErrors = root.GetProperty("errors").EnumerateObject()
                .Select(member => member.Name + "=" + member.Value.GetRawText())
                .Order(StringComparer.Ordinal);
            var expectedErrors = errors
                .Select(error => error.Key + "=" + JsonSerializer.Serialize(error.Value, MvcEscaping))
                .Order(StringComparer.Ordinal);
            // Red: CustomizeProblemDetails adds an "x" key to every ValidationProblemDetails' Errors; for the
            //      filter's shape, ZeroAllocValidationEndpointFilter.ToProblem adds the same key to its errors.
            actualErrors.Should().Equal(expectedErrors, text);
        }

        if (traceId)
        {
            var value = root.GetProperty("traceId");
            // Red: CustomizeProblemDetails sets the traceId extension to "".
            (value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())).Should()
                .BeTrue("traceId must be a non-empty string: {0}", text);
        }
    }

    /// <summary>Asserts a controller's own <c>{ "error": ... }</c> body: one member, <c>error</c>, with the message.</summary>
    private static async Task AssertErrorBodyAsync(HttpResponseMessage response, string message)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        // Red: a member added to ErrorBody, public string? Extra { get; init; } = "x", for the Tasks and Projects rows.
        document.RootElement.EnumerateObject().Select(member => member.Name).Should().Equal(["error"], text);
        // Red: the not-found message changed in TasksEndpoints.TaskNotFound, or in ProjectsEndpoints.ProjectNotFound.
        document.RootElement.GetProperty("error").GetString().Should().Be(message);
    }

    /// <summary>A UTF-8 body with exactly <paramref name="contentType"/>, or no Content-Type header at all.</summary>
    private static ByteArrayContent RawBody(string body, string? contentType)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return content;
    }

    private static StringContent JsonBody(string body)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static RequiredCase Refused(
        string method, string path, string body, string role, string contentType, params string[] required) =>
        new(method, path, body, role, contentType, required, HttpStatusCode.BadRequest);

    private static RequiredCase Passed(string method, string path, string body, string role, HttpStatusCode status) =>
        new(method, path, body, role, Json, [], status);

    private sealed record RequiredCase(
        string Method, string Path, string Body, string Role, string ContentType, string[] Required,
        HttpStatusCode PassedStatus)
    {
        public bool Workflow { get; init; }
    }

    /// <summary>
    ///     The shared factory over the reset fixture database, or <see cref="ScratchWorkflowHost"/> when the case needs
    ///     <c>WorkflowRunsEndpoints</c>, which answers only with the engine on.
    /// </summary>
    private sealed class ApiHost : IAsyncDisposable
    {
        private readonly ApiWebApplicationFactory? _factory;
        private readonly ScratchWorkflowHost? _workflow;

        private ApiHost(ApiWebApplicationFactory? factory, ScratchWorkflowHost? workflow)
        {
            _factory = factory;
            _workflow = workflow;
        }

        public static async Task<ApiHost> StartAsync(
            PostgresFixture fixture, bool workflow, IAgentRuntime? runtime = null,
            Action<IServiceCollection>? configureServices = null)
        {
            if (workflow)
            {
                return new ApiHost(null, await ScratchWorkflowHost.StartAsync(fixture,
                    runtime ?? Substitute.For<IAgentRuntime>(), configureServices: configureServices));
            }

            await fixture.DatabaseResetter.ResetAsync();
            return new ApiHost(new ApiWebApplicationFactory(fixture.ConnectionString,
                runtime ?? Substitute.For<IAgentRuntime>(), configureServices: configureServices), null);
        }

        /// <summary>The scratch workflow host, when the case asked for one.</summary>
        public ScratchWorkflowHost? Workflow => _workflow;

        /// <summary>A client with no test-auth headers: <see cref="HeaderTestAuthHandler"/> gives no result.</summary>
        public HttpClient AnonymousClient() => _factory!.CreateClient();

        /// <summary>A client authenticated through <see cref="HeaderTestAuthHandler"/>: <paramref name="user"/> becomes <c>sub</c>.</summary>
        public HttpClient Client(string user, params string[] roles)
        {
            if (_workflow is not null)
            {
                return _workflow.Client(user, roles);
            }

            var client = _factory!.CreateClient();
            client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, user);
            client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, string.Join(',', roles));
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            if (_workflow is not null)
            {
                await _workflow.DisposeAsync();
            }

            if (_factory is not null)
            {
                await _factory.DisposeAsync();
            }
        }
    }
}
