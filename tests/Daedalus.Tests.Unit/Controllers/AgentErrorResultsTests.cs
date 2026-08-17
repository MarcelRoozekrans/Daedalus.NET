using System.Security.Claims;
using Daedalus.Api.Agents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
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
    public void ToStatusCode_maps_every_code(AgentErrorCode code, int expected)
    {
        AgentErrorResults.ToStatusCode(code).Should().Be(expected);
    }

    [Fact]
    public void Every_AgentErrorCode_value_has_an_explicit_mapping_test()
    {
        // Guards the InlineData list above against Thalos adding codes: new values fall through to 502 silently otherwise.
        Enum.GetValues<AgentErrorCode>().Should().HaveCount(12);
    }

    [Fact]
    public void ToActionResult_builds_problem_details_with_code_extension_and_detail()
    {
        var controller = CreateController();
        var error = AgentError.Quarantined("Blocked by Sentinel.", "Critical: PromptInjectionDetector");

        var result = error.ToActionResult(controller);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be("Quarantined");
        problem.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        problem.Detail.Should().Be("Blocked by Sentinel. (Critical: PromptInjectionDetector)");
        problem.Extensions.Should().ContainKey("code").WhoseValue.Should().Be("Quarantined");
    }

    [Fact]
    public void ToActionResult_without_detail_uses_the_message_alone()
    {
        var controller = CreateController();
        var sessionId = SessionId.New();

        var result = AgentError.SessionNotFound(sessionId).ToActionResult(controller);

        var problem = result.Should().BeOfType<ObjectResult>().Subject.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status404NotFound);
        problem.Title.Should().Be("SessionNotFound");
        problem.Detail.Should().NotBeNullOrEmpty().And.NotContain("(");
    }

    private static TestController CreateController()
    {
        // ControllerBase.Problem resolves ProblemDetailsFactory from request services.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddApiExplorer();
        var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };

        return new TestController { ControllerContext = new ControllerContext { HttpContext = httpContext } };
    }

    private sealed class TestController : ControllerBase;
}
