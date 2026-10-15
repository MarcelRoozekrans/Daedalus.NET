using System.Text.Json;
using Daedalus.Application.DTOs.WorkflowRuns;

namespace Daedalus.Tests.Unit.Web;

/// <summary>Builds the run DTOs the Web tests answer with, and their JSON as the API sends it.</summary>
internal static class RunViews
{
    /// <summary>A full run view for <paramref name="id"/>, parked at the human gate unless told otherwise.</summary>
    public static WorkflowRunView Sample(Guid id, string status = "Awaiting", string? awaitingSignal = "human_approval") =>
        new(
            id,
            "manufacture",
            1,
            status,
            "approve",
            awaitingSignal,
            LastError: null,
            Pins: null,
            StandingInstructionsDiff: null,
            NodeUsage: [],
            PrUrl: null,
            UnreadablePullRequestLink: null,
            StartedBy: "dev",
            WriteAudit: [],
            DeferredFindings: [],
            DeferredFindingsError: null,
            FiledFindings: [],
            Steps: [],
            Checked: [],
            StartedByName: "Dev Eloper",
            NodeSelections: [],
            NodeSelectionsError: null,
            FailureReason: null,
            ReviewVisits: [],
            ReviewVisitsError: null);

    /// <summary>One list row for <paramref name="id"/>.</summary>
    public static WorkflowRunListItem Item(Guid id, string status = "Running", string? awaitingSignal = null) =>
        new(id, "manufacture", 1, status, "build", awaitingSignal, "dev", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            TaskId: null, TaskCode: null, TaskTitle: null, StartedByName: "Dev Eloper",
            FailureReason: null, PrUrl: null);

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
