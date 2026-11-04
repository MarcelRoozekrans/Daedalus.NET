using Daedalus.Api.Agents;
using Daedalus.Api.Endpoints;
using Daedalus.Tests.Unit.Endpoints;
using Microsoft.AspNetCore.Http;
using Thalos;

namespace Daedalus.Tests.Unit.Controllers;

public sealed class AgentErrorResultsTests
{
    [Theory]
    [InlineData(AgentErrorCode.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(AgentErrorCode.Unauthorized, StatusCodes.Status403Forbidden)]
    [InlineData(AgentErrorCode.AgentNotFound, StatusCodes.Status404NotFound)]
    [InlineData(AgentErrorCode.SessionNotFound, StatusCodes.Status404NotFound)]
    [InlineData(AgentErrorCode.ToolNotFound, StatusCodes.Status404NotFound)]
    [InlineData(AgentErrorCode.SessionBusy, StatusCodes.Status409Conflict)]
    [InlineData(AgentErrorCode.SessionClosed, StatusCodes.Status409Conflict)]
    [InlineData(AgentErrorCode.Quarantined, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(AgentErrorCode.ToolDenied, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(AgentErrorCode.Cancelled, AgentErrorResults.ClientClosedRequest)]
    [InlineData(AgentErrorCode.ProviderError, StatusCodes.Status502BadGateway)]
    [InlineData(AgentErrorCode.StoreError, StatusCodes.Status502BadGateway)]
    [InlineData(AgentErrorCode.MemoryValidationFailed, StatusCodes.Status400BadRequest)]
    [InlineData(AgentErrorCode.MemoryForbidden, StatusCodes.Status403Forbidden)]
    [InlineData(AgentErrorCode.MemoryNotFound, StatusCodes.Status404NotFound)]
    [InlineData(AgentErrorCode.MemoryIndexUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(AgentErrorCode.MemoryStoreFailed, StatusCodes.Status502BadGateway)]
    [InlineData(AgentErrorCode.MemoryIndexFailed, StatusCodes.Status502BadGateway)]
    [InlineData(AgentErrorCode.SkillValidationFailed, StatusCodes.Status400BadRequest)]
    [InlineData(AgentErrorCode.SkillNotFound, StatusCodes.Status404NotFound)]
    [InlineData(AgentErrorCode.SkillSearchUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(AgentErrorCode.SkillStoreFailed, StatusCodes.Status502BadGateway)]
    [InlineData(AgentErrorCode.SubagentBudgetExceeded, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(AgentErrorCode.SubagentDeadlineExceeded, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(AgentErrorCode.SubagentDepthExceeded, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(AgentErrorCode.GitBranchAlreadyExists, StatusCodes.Status409Conflict)]
    [InlineData(AgentErrorCode.GitRepositoryNotFound, StatusCodes.Status404NotFound)]
    [InlineData(AgentErrorCode.GitAuthenticationFailed, StatusCodes.Status401Unauthorized)]
    [InlineData(AgentErrorCode.GitOperationFailed, StatusCodes.Status502BadGateway)]
    public void ToStatusCode_maps_every_code(AgentErrorCode code, int expected)
    {
        AgentErrorResults.ToStatusCode(code).Should().Be(expected);
    }

    [Fact]
    public void Every_AgentErrorCode_value_has_an_explicit_mapping_test()
    {
        // Guards the InlineData list above against Thalos adding codes: new values fall through to 502 silently otherwise.
        // Thalos.NET 0.2.0 added the six Memory* codes; 0.3.0 added the four Skill* codes; 0.5.0 added the three
        // Subagent* codes; 0.6.0 added the four Git* codes.
        Enum.GetValues<AgentErrorCode>().Should().HaveCount(29);
    }

    [Fact]
    public async Task ToProblem_builds_problem_with_code_extension()
    {
        await using var services = EndpointTestContext.Services();
        var context = EndpointTestContext.Create(services, ProblemContentType.Json);
        var error = AgentError.Quarantined("Blocked by Sentinel.", "Critical: PromptInjectionDetector");

        var text = await EndpointTestContext.ExecuteAsync(error.ToProblem(context), context);

        // Red: ToProblem maps through a status other than ToStatusCode's.
        context.Response.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        // Red: the code extension dropped or renamed; the detail without the parenthesised Detail; the title the
        //      table's "Unprocessable Entity" rather than the code.
        EndpointTestContext.AssertProblem(text, "https://tools.ietf.org/html/rfc4918#section-11.2", "Quarantined", 422,
            detail: "Blocked by Sentinel. (Critical: PromptInjectionDetector)",
            extra: new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = "\"Quarantined\"" });
        // Red: ToProblem ignores the group's ProblemContentType.
        context.Response.ContentType.Should().Be(EndpointTestContext.Json);
    }

    [Fact]
    public async Task ToProblem_without_detail_uses_the_message_alone()
    {
        await using var services = EndpointTestContext.Services();
        var context = EndpointTestContext.Create(services);
        var error = AgentError.SessionNotFound(SessionId.New());

        var text = await EndpointTestContext.ExecuteAsync(error.ToProblem(context), context);

        // Red: format the detail as "{Message} ()" when Detail is null.
        EndpointTestContext.AssertProblem(text, EndpointTestContext.NotFoundType, "SessionNotFound", 404, detail: error.Message,
            extra: new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = "\"SessionNotFound\"" });
    }

    public static TheoryData<AgentErrorCode> EveryCode => new(Enum.GetValues<AgentErrorCode>());

    [Theory]
    [MemberData(nameof(EveryCode))]
    public async Task ToProblem_maps_every_code_to_its_status_title_and_code_extension(AgentErrorCode code)
    {
        await using var services = EndpointTestContext.Services();
        var context = EndpointTestContext.Create(services);
        var error = new AgentError(code, "message", code == AgentErrorCode.Cancelled ? null : "detail");

        var problem = error.ToProblem(context);

        // Red: ToProblem maps through StatusCodes.Status500InternalServerError in place of ToStatusCode(error.Code).
        problem.StatusCode.Should().Be(AgentErrorResults.ToStatusCode(code));
        problem.ProblemDetails.Status.Should().Be(AgentErrorResults.ToStatusCode(code));
        // Red: title: null in place of title: code in ToProblem; the table's title is used.
        problem.ProblemDetails.Title.Should().Be(code.ToString());
        // Red: detail: error.Message in place of the detail with the parenthesised Detail in ToProblem.
        problem.ProblemDetails.Detail.Should().Be(code == AgentErrorCode.Cancelled ? "message" : "message (detail)");
        // Red: the code extension renamed to "errorCode" in ToProblem.
        problem.ProblemDetails.Extensions.Should().ContainKey("code").WhoseValue.Should().Be(code.ToString());
    }
}
