using System.Globalization;
using System.Reflection;
using Daedalus.Api.Controllers;
using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Application.DTOs.WorkflowRuns;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="ApiBody{T}"/> and <see cref="ApiQuery{T}"/> never fail binding; they carry the validation problem MVC's
///     model binding writes, as <c>MvcBehaviourContractTests</c> pins it (rulings R16, R17, R18). The problem is
///     written here through <see cref="ApiProblem.Validation"/>, the way the endpoint filter answers it.
/// </summary>
public sealed class ApiBindingTests
{
    // -----------------------------------------------------------------------------------------------------------------
    // Body
    // -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"name\":", "$.name",
        "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be"
        + " closed. Path: $.name | LineNumber: 0 | BytePositionInLine: 8.")]
    [InlineData("[1]", "$",
        "The JSON value could not be converted to Daedalus.Application.DTOs.CreateProjectDto. Path: $ | LineNumber: 0"
        + " | BytePositionInLine: 1.")]
    public async Task Body_malformed_json_matches_mvc(string body, string pathKey, string message)
    {
        await using var services = Services();
        var context = WithJsonBody(services, body);

        var bound = await BindBody<CreateProjectDto>(context);

        // Red: key the reader's error "$" always, or "" as for FormatException; drop the parameter's required error.
        await AssertBindingProblemAsync(bound, context, new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [pathKey] = [message],
            ["request"] = [RequestRequired],
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task Body_empty_matches_mvc(string body)
    {
        await using var services = Services();
        var context = WithJsonBody(services, body);

        var bound = await BindBody<CreateProjectDto>(context);

        // Red: for "", read the empty stream, which throws "The input does not contain any JSON tokens" at "$"; for
        //      "null", treat the literal as a bound null with no error.
        await AssertBindingProblemAsync(bound, context, new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [""] = [NonEmptyBody],
            ["request"] = [RequestRequired],
        });
    }

    [Fact]
    public async Task Body_empty_detected_through_the_body_detection_feature()
    {
        await using var services = Services();
        var context = WithJsonBody(services, "");
        context.Request.ContentLength = null;
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new NoBody());

        var bound = await BindBody<CreateProjectDto>(context);

        // Red: decide on ContentLength alone, which is null here, and read the empty stream.
        bound.Problem!.Errors.Keys.Should().BeEquivalentTo(["", "request"]);
    }

    /// <summary>
    ///     Task 2's table of implicitly required members, one case per DTO the controllers bind, keyed and worded as
    ///     MVC writes them: the C# property name, "The {name} field is required.", the collection path for nested
    ///     elements. An initializer or a parameterless constructor that supplies a value keeps an omitted member from
    ///     being refused.
    /// </summary>
    public static TheoryData<string, string, string[]> RequiredCases => new()
    {
        { nameof(CreateProjectDto), "{}", ["ProjectName", "Description", "Version"] },
        { nameof(UpdateProjectDto), "{}", ["ProjectName", "Description", "Version"] },
        { nameof(CreateTaskDto), "{}", ["Title", "Description"] },
        { nameof(UpdateTaskDto), "{}", [] },
        { nameof(SubmitAnalysisRequest), "{}", ["RepositoryUrl", "FilePath", "Title", "Description", "Requirements"] },
        { nameof(IterateAnalysisRequest), "{}", ["AiResponse"] },
        { nameof(FinalizeAnalysisRequest), "{}", [] },
        { nameof(CreateBrainstormSessionDto), "{}", [] },
        { nameof(SendBrainstormMessageDto), "{}", ["Content"] },
        { nameof(PrdRequestDto), "{\"userRequirements\":null}", ["UserRequirements"] },
        { nameof(PrdRequestDto), "{}", [] },
        { nameof(PrdConversionRequestDto), "{}", ["PrdItems"] },
        {
            nameof(PrdConversionRequestDto), $"{{\"projectId\":\"{Guid.Empty}\",\"prdItems\":[{{}}]}}",
            ["PrdItems[0].Title", "PrdItems[0].Description", "PrdItems[0].AcceptanceCriteria", "PrdItems[0].Dependencies",
                "PrdItems[0].Phase"]
        },
        {
            nameof(CreateRepositoryConfigurationDto), "{\"name\":null,\"url\":null,\"platform\":null,\"defaultBranch\":null}",
            ["Name", "Url", "Platform", "DefaultBranch"]
        },
        { nameof(CreateRepositoryConfigurationDto), "{}", [] },
        { nameof(UpdateRepositoryConfigurationDto), "{\"name\":null,\"url\":null,\"defaultBranch\":null}", ["Name", "Url", "DefaultBranch"] },
        { nameof(SendTurnRequestDto), "{}", ["Text"] },
        { nameof(StartWorkflowRunRequest), "{}", ["WorkIntent", "Repository"] },
        { nameof(ResumeWorkflowRunRequest), "{}", ["Signal"] },
        { nameof(CancelWorkflowRunRequest), "{}", [] },
    };

    [Theory]
    [MemberData(nameof(RequiredCases))]
    public async Task Body_missing_required_property_matches_mvc(string dto, string body, string[] required)
    {
        await using var services = Services();
        var context = WithJsonBody(services, body);

        var bound = await (dto switch
        {
            nameof(CreateProjectDto) => Bind<CreateProjectDto>(context),
            nameof(UpdateProjectDto) => Bind<UpdateProjectDto>(context),
            nameof(CreateTaskDto) => Bind<CreateTaskDto>(context),
            nameof(UpdateTaskDto) => Bind<UpdateTaskDto>(context),
            nameof(SubmitAnalysisRequest) => Bind<SubmitAnalysisRequest>(context),
            nameof(IterateAnalysisRequest) => Bind<IterateAnalysisRequest>(context),
            nameof(FinalizeAnalysisRequest) => Bind<FinalizeAnalysisRequest>(context),
            nameof(CreateBrainstormSessionDto) => Bind<CreateBrainstormSessionDto>(context),
            nameof(SendBrainstormMessageDto) => Bind<SendBrainstormMessageDto>(context),
            nameof(PrdRequestDto) => Bind<PrdRequestDto>(context),
            nameof(PrdConversionRequestDto) => Bind<PrdConversionRequestDto>(context),
            nameof(CreateRepositoryConfigurationDto) => Bind<CreateRepositoryConfigurationDto>(context),
            nameof(UpdateRepositoryConfigurationDto) => Bind<UpdateRepositoryConfigurationDto>(context),
            nameof(SendTurnRequestDto) => Bind<SendTurnRequestDto>(context),
            nameof(StartWorkflowRunRequest) => Bind<StartWorkflowRunRequest>(context),
            nameof(ResumeWorkflowRunRequest) => Bind<ResumeWorkflowRunRequest>(context),
            nameof(CancelWorkflowRunRequest) => Bind<CancelWorkflowRunRequest>(context),
            _ => throw new ArgumentOutOfRangeException(nameof(dto), dto, "unknown case"),
        });

        if (required.Length == 0)
        {
            // Red: treat every reference-typed member as required, ignoring IsSetNullable; UpdateTaskDto's string?
            //      members and CancelWorkflowRunRequest's Reason are then refused. Or check the member when it is
            //      omitted rather than when its value is null; the initializer and constructor cases then fail.
            bound.Problem.Should().BeNull();
            // Red: ApiBody drops a value that bound without errors.
            bound.BoundValue.Should().NotBeNull();
            return;
        }

        // Red: key the error by the JSON name, "projectName", rather than the C# name; or leave the "PrdItems[0]."
        //      prefix off the nested keys, or stop walking into collection elements.
        await AssertBindingProblemAsync(bound, context,
            required.ToDictionary(key => key, key => new[] { $"The {key.Split('.')[^1]} field is required." }, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Body_valid_binds_the_value_without_a_problem()
    {
        await using var services = Services();
        var context = WithJsonBody(services, "{\"projectName\":\"p\",\"description\":\"d\",\"version\":\"1.0\"}");

        var bound = await BindBody<CreateProjectDto>(context);

        // Red: read names case-sensitively with PascalCase, which leaves every member null and refused.
        bound.Problem.Should().BeNull();
        bound.Value.Should().Be(new CreateProjectDto("p", "d", "1.0"));
    }

    [Theory]
    [InlineData("text/json")]
    [InlineData("application/merge-patch+json")]
    [InlineData("application/json; charset=utf-8")]
    public async Task Body_reads_every_json_media_type_mvcs_formatter_reads(string contentType)
    {
        await using var services = Services();
        var context = WithJsonBody(services, "{\"text\":\"hi\"}", contentType);

        var bound = await BindBody<SendTurnRequestDto>(context);

        // Red: accept application/json alone, as HttpRequest.HasJsonContentType does not for text/json.
        bound.UnsupportedMediaType.Should().BeFalse();
        bound.Value.Should().Be(new SendTurnRequestDto("hi"));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData(null)]
    [InlineData("application/json; charset=iso-8859-1")]
    public async Task Body_with_a_content_type_mvc_cannot_read_is_unsupported(string? contentType)
    {
        await using var services = Services();
        var context = WithJsonBody(services, "{\"text\":\"hi\"}", contentType);

        var bound = await BindBody<SendTurnRequestDto>(context);

        // Red: read the body whatever its Content-Type; MVC answers 415 when no input formatter reads the type or
        //      its charset.
        bound.UnsupportedMediaType.Should().BeTrue();
        bound.Value.Should().BeNull();
    }

    [Fact]
    public async Task Optional_body_binds_null_on_an_empty_body()
    {
        await using var services = Services();
        var context = WithJsonBody(services, "", contentType: null);

        var bound = await BindAsync<ApiBody<CancelWorkflowRunRequest>>(context, Parameter(nameof(OptionalBody)));

        // Red: ignore ParameterInfo.IsOptional; the empty body is then refused as for a required parameter, 415
        //      here since no Content-Type was sent. MVC binds a nullable body parameter to null, ruling R17.
        bound!.UnsupportedMediaType.Should().BeFalse();
        bound.Problem.Should().BeNull();
        bound.Value.Should().BeNull();
    }

    [Fact]
    public async Task Optional_body_binds_null_on_the_null_literal()
    {
        await using var services = Services();
        var context = WithJsonBody(services, "null");

        var bound = await BindAsync<ApiBody<CancelWorkflowRunRequest>>(context, Parameter(nameof(OptionalBody)));

        // Red: treat the literal null as "no value" for an optional parameter too.
        bound!.Problem.Should().BeNull();
        bound.Value.Should().BeNull();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Query
    // -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("?take=abc", nameof(TakeQuery), "take")]
    [InlineData("?projectId=abc", nameof(ProjectIdQuery), "projectId")]
    public async Task Query_unparsable_matches_mvc(string query, string probe, string key)
    {
        await using var services = Services();
        var context = Create(services);
        context.Request.QueryString = new QueryString(query);

        IApiBinding bound = string.Equals(probe, nameof(TakeQuery), StringComparison.Ordinal)
            ? await BindAsync<ApiQuery<int>>(context, Parameter(probe))
            : await BindAsync<ApiQuery<Guid>>(context, Parameter(probe));

        // Red: write MVC's property message, "The value 'abc' is not valid for take.", which R16 rules out.
        await AssertBindingProblemAsync(bound, context, new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [key] = ["The value 'abc' is not valid."],
        });
    }

    [Fact]
    public async Task Query_absent_uses_default()
    {
        await using var services = Services();
        var context = Create(services);

        var take = await BindAsync<ApiQuery<int>>(context, Parameter(nameof(TakeQuery)));
        var projectId = await BindAsync<ApiQuery<Guid>>(context, Parameter(nameof(ProjectIdQuery)));

        // Red: HasValue true for a value that was not sent.
        take.HasValue.Should().BeFalse();
        // Red: answer a problem for an absent value; MVC binds the parameter's default with no error.
        take.Problem.Should().BeNull();
        // Red: ValueOr answers Value whatever HasValue says; take is then 0 where MVC's int take = 20 binds 20.
        take.ValueOr(20).Should().Be(20);
        take.Value.Should().Be(0);
        projectId.Value.Should().Be(Guid.Empty);
        projectId.Problem.Should().BeNull();
    }

    [Fact]
    public async Task Query_present_parses_with_invariant_culture()
    {
        await using var services = Services();
        var context = Create(services);
        context.Request.QueryString = new QueryString("?ratio=1.5&take=7");
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            var ratio = await BindAsync<ApiQuery<double>>(context, Parameter(nameof(RatioQuery)));
            var take = await BindAsync<ApiQuery<int>>(context, Parameter(nameof(TakeQuery)));

            // Red: parse with CultureInfo.CurrentCulture; nl-NL reads "1.5" as 15.
            ratio.Value.Should().Be(1.5);
            take.Value.Should().Be(7);
            // Red: HasValue false for a value that was sent.
            take.HasValue.Should().BeTrue();
            // Red: ValueOr answers the fallback for a value that was sent.
            take.ValueOr(20).Should().Be(7);
            ((IApiBinding)take).BoundValue.Should().Be(7);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("?take=", "The value '' is invalid.")]
    [InlineData("?take=abc&take=1", "The value 'abc,1' is not valid.")]
    public async Task Query_empty_or_repeated_value_matches_mvc(string query, string message)
    {
        await using var services = Services();
        var context = Create(services);
        context.Request.QueryString = new QueryString(query);

        var take = await BindAsync<ApiQuery<int>>(context, Parameter(nameof(TakeQuery)));

        // Red: treat an empty value as absent, so take binds with no problem; or quote only the first of repeated values. MVC
        //      writes ValueMustNotBeNull for an empty value and quotes the values joined with commas.
        take.Problem!.Errors.Should().ContainSingle().Which.Value.Should().Equal(message);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------------------------------

    private static async Task AssertBindingProblemAsync(
        IApiBinding bound, HttpContext context, IReadOnlyDictionary<string, string[]> errors)
    {
        // Red: ApiBody or ApiQuery answer no problem for the case.
        bound.Problem.Should().NotBeNull();
        var text = await ExecuteAsync(ApiProblem.Validation(context, bound.Problem!), context);
        // Red: the binding problem typed rfc7231 like the ZeroAlloc filter's, or without a traceId; the title or the
        //      errors changed.
        AssertProblem(text, BadRequestType, ValidationTitle, 400, errors: errors);
    }

    private static async Task<IApiBinding> Bind<T>(HttpContext context) where T : class =>
        (await BindAsync<ApiBody<T>>(context, BodyParameter<T>()))!;

    private static async Task<ApiBody<T>> BindBody<T>(HttpContext context) where T : class =>
        (await BindAsync<ApiBody<T>>(context, BodyParameter<T>()))!;

    private static ParameterInfo BodyParameter<T>() where T : class =>
        typeof(ApiBindingTests).GetMethod(nameof(RequiredBody), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(T)).GetParameters()[0];

    private static ParameterInfo Parameter(string probe) =>
        typeof(ApiBindingTests).GetMethod(probe, BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters()[0];

    // The handler parameters ApiBody and ApiQuery read their name, optionality and default from.
    private static void RequiredBody<T>(ApiBody<T> request) where T : class => _ = request;

    private static void OptionalBody(ApiBody<CancelWorkflowRunRequest>? request = null) => _ = request;

    private static void TakeQuery(ApiQuery<int> take) => _ = take;

    private static void ProjectIdQuery(ApiQuery<Guid> projectId) => _ = projectId;

    private static void RatioQuery(ApiQuery<double> ratio) => _ = ratio;

    private sealed class NoBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => false;
    }
}
