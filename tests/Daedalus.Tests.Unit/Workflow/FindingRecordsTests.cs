using Daedalus.Agents.Workflow;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>What <see cref="FindingRecords.ReadDropped"/> makes of the drop and void records a run holds.</summary>
public sealed class FindingRecordsTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static WorkflowRunRecord Drop(string attempt, params string[] ids) =>
        Record(WorkflowRunRecord.FindingsDroppedKind, FindingRecords.DroppedPayload(ids, "admin", attempt));

    private static WorkflowRunRecord Void(string attempt) =>
        Record(WorkflowRunRecord.FindingsDropVoidedKind, FindingRecords.DropVoidedPayload(attempt));

    private static WorkflowRunRecord Record(string kind, string payload) =>
        WorkflowRunRecord.Create(RunId, 3, "gate", kind, "u-admin", null, payload, DateTime.UtcNow).Value;

    /// <summary>The loser of a race must not stay latest. Red: ignore void records; the loser's empty list wins.</summary>
    [Fact]
    public void A_voided_record_is_ignored_and_the_previous_unvoided_one_applies()
    {
        var result = FindingRecords.ReadDropped([Drop("a", "correctness-1"), Drop("b"), Void("b")]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Ids.Should().BeEquivalentTo(["correctness-1"]);
    }

    /// <summary>Red: return the latest drop record whatever voided it; the ids are not empty.</summary>
    [Fact]
    public void Only_voided_records_leave_nothing_dropped()
    {
        var result = FindingRecords.ReadDropped([Drop("a", "correctness-1"), Void("a")]);

        result.Value.Should().Be(DroppedFindings.None);
    }

    /// <summary>Red: treat a missing attempt as the empty string and accept the record; the read succeeds.</summary>
    [Fact]
    public void A_drop_record_without_an_attempt_is_unreadable()
    {
        var result = FindingRecords.ReadDropped([Record(WorkflowRunRecord.FindingsDroppedKind, """{ "dropped": ["correctness-1"], "by": "x" }""")]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("attempt");
    }

    /// <summary>Red: read the oldest record instead of the latest; the ids are the first record's.</summary>
    [Fact]
    public void The_latest_unvoided_record_wins()
    {
        var result = FindingRecords.ReadDropped([Drop("a", "correctness-1"), Drop("b", "correctness-2")]);

        result.Value.Ids.Should().BeEquivalentTo(["correctness-2"]);
        result.Value.By.Should().Be("admin");
    }
}
