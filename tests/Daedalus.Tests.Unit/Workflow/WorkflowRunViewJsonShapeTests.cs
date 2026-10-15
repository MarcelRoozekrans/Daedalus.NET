using System.Text.Json;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs.WorkflowRuns;
using Thalos;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>The run view's JSON names, pinned before its types move to Application, so the move changes no byte.</summary>
public sealed class WorkflowRunViewJsonShapeTests
{
    /// <summary>
    ///     Every property name the API serves today, top level and nested.
    ///     Red: rename a NodePinView property, or a WorkflowRunView parameter, after the move.
    ///     Red: rename a NodeSelectionView property, or NodeSelectionsError. Red: rename FailureReason.
    /// </summary>
    [Fact]
    public void The_view_serializes_with_todays_names()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Sample(), JsonSerializerOptions.Web));
        var root = json.RootElement;

        Names(root).Should().Equal(
            "id", "process", "processVersion", "status", "currentNode", "awaitingSignal", "lastError", "pins",
            "standingInstructionsDiff", "nodeUsage", "prUrl", "unreadablePullRequestLink", "startedBy", "writeAudit",
            "deferredFindings", "deferredFindingsError", "filedFindings", "steps", "checked", "startedByName", "nodeSelections", "nodeSelectionsError", "failureReason");
        Names(root.GetProperty("steps")[0]).Should().Equal(
            "seq", "kind", "fromNode", "toNode", "status", "outcome", "error", "inputTokens", "outputTokens",
            "cacheReadTokens", "at", "actor");
        Names(root.GetProperty("checked")[0]).Should().Equal("lens", "checked");
        Names(root.GetProperty("pins").GetProperty("implement")).Should().Equal(
            "agentName", "agentId", "agentRevision", "skillName", "skillHash");
        Names(root.GetProperty("nodeUsage")[0]).Should().Equal(
            "seq", "node", "inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens", "modelId");
        Names(root.GetProperty("writeAudit")[0]).Should().Equal("seq", "node", "tool", "path", "startedBy");
        Names(root.GetProperty("deferredFindings")[0]).Should().Equal(
            "id", "lens", "file", "line", "title", "scenario", "reason", "existingIssue", "dropped", "existingIssueUrl");
        Names(root.GetProperty("filedFindings")[0]).Should().Equal("findingId", "mode", "issue", "url");
        Names(root.GetProperty("nodeSelections")[0]).Should().Equal("seq", "node", "model", "level", "keptFamilies", "fallback", "confidence");
    }

    /// <summary>
    ///     The list item serializes with the reason and the pull request link last.
    ///     Red: rename FailureReason or PrUrl on WorkflowRunListItem, or move one before StartedByName.
    /// </summary>
    [Fact]
    public void The_list_item_serializes_the_reason_and_the_pull_request_link_last()
    {
        var item = new WorkflowRunListItem(
            Guid.NewGuid(), "manufacture", 9, "Failed", "review", null, "u", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            null, null, null, "Dev", "why", new Uri("https://example.test/pr/1"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(item, JsonSerializerOptions.Web));

        Names(json.RootElement).Should().Equal(
            "id", "process", "processVersion", "status", "currentNode", "awaitingSignal", "startedBy", "createdAt", "updatedAt",
            "taskId", "taskCode", "taskTitle", "startedByName", "failureReason", "prUrl");
    }

    /// <summary>
    ///     The pin view serializes byte for byte as the Thalos pin did, all five members, the agent id value included.
    ///     Red: change <c>.ToString()</c> in <c>ToPinView</c> to <c>.Value.ToString()</c> or a lowercase form.
    /// </summary>
    [Fact]
    public void The_pin_view_serializes_byte_for_byte_as_the_thalos_pin()
    {
        var pin = new NodePin("Implementer", AgentId.New(), "3", "implement-skill", "hash");

        JsonSerializer.Serialize(WorkflowRunsController.ToPinView(pin), JsonSerializerOptions.Web)
            .Should().Be(JsonSerializer.Serialize(pin, JsonSerializerOptions.Web));
    }

    private static IEnumerable<string> Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name);

    private static WorkflowRunView Sample() => new(
        Guid.NewGuid(), "manufacture", 9, "Awaiting", "gate", "human_approval", null,
        new Dictionary<string, NodePinView>(StringComparer.Ordinal) { ["implement"] = new NodePinView("Implementer", "01M4RQEQYETH43BEQ1H20BQ25X", "3", "implement-skill", "hash") },
        "diff", [new NodeUsageView(1, "implement", 10, 2, 5, 1, "m")], new Uri("https://example.test/pr/1"), null, "u",
        [new WriteAuditView(1, "implement", "roslyn__apply", "a.cs", "u")],
        [new DeferredFindingView("mechanism-1", "mechanism", "a.cs", 3, "t", "s", "different-area", 7, false, new Uri("https://github.com/o/r/issues/7"))],
        null, [new FiledFindingView("mechanism-1", "created", 12, new Uri("https://example.test/issues/12"))],
        [new WorkflowRunStepView(0, "Entered", null, "implement", "Running", null, null, null, null, null, DateTimeOffset.UnixEpoch, null)],
        [new LensCheckedView("mechanism", ["a.cs:3"])], "Dev",
        [new NodeSelectionView(1, "implement", "m", "low", 3, null, 0.8)], "e", "Review rejected the change 3 times, the review limit.");
}
