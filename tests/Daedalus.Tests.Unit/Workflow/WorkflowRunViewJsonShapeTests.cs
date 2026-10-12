using System.Text.Json;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs.WorkflowRuns;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>The run view's JSON names, pinned before its types move to Application, so the move changes no byte.</summary>
public sealed class WorkflowRunViewJsonShapeTests
{
    /// <summary>
    ///     Every property name the API serves today, top level and nested.
    ///     Red: rename a NodePinView property, or a WorkflowRunView parameter, after the move.
    /// </summary>
    [Fact]
    public void The_view_serializes_with_todays_names()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Sample(), JsonSerializerOptions.Web));
        var root = json.RootElement;

        Names(root).Should().Equal(
            "id", "process", "processVersion", "status", "currentNode", "awaitingSignal", "lastError", "pins",
            "standingInstructionsDiff", "nodeUsage", "prUrl", "unreadablePullRequestLink", "startedBy", "writeAudit",
            "deferredFindings", "deferredFindingsError", "filedFindings");
        Names(root.GetProperty("pins").GetProperty("implement")).Should().Equal(
            "agentName", "agentId", "agentRevision", "skillName", "skillHash");
        Names(root.GetProperty("nodeUsage")[0]).Should().Equal(
            "seq", "node", "inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens", "modelId");
        Names(root.GetProperty("writeAudit")[0]).Should().Equal("seq", "node", "tool", "path", "startedBy");
        Names(root.GetProperty("deferredFindings")[0]).Should().Equal(
            "id", "lens", "file", "line", "title", "scenario", "reason", "existingIssue", "dropped");
        Names(root.GetProperty("filedFindings")[0]).Should().Equal("findingId", "mode", "issue", "url");
    }

    private static IEnumerable<string> Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name);

    private static WorkflowRunView Sample() => new(
        Guid.NewGuid(), "manufacture", 9, "Awaiting", "gate", "human_approval", null,
        new Dictionary<string, NodePinView>(StringComparer.Ordinal) { ["implement"] = new NodePinView("Implementer", "01M4RQEQYETH43BEQ1H20BQ25X", "3", "implement-skill", "hash") },
        "diff", [new NodeUsageView(1, "implement", 10, 2, 5, 1, "m")], new Uri("https://example.test/pr/1"), null, "u",
        [new WriteAuditView(1, "implement", "roslyn__apply", "a.cs", "u")],
        [new DeferredFindingView("mechanism-1", "mechanism", "a.cs", 3, "t", "s", "different-area", null, false)],
        null, [new FiledFindingView("mechanism-1", "created", 12, new Uri("https://example.test/issues/12"))]);
}
