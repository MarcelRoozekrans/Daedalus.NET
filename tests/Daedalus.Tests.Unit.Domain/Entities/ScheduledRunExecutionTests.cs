using AwesomeAssertions;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain;

public class ScheduledRunExecutionTests
{
    private static readonly Guid ScheduleId = Guid.Parse("0f1d8a2c-5e6b-4a71-9c3d-8b2f4e6a1c07");
    private static readonly DateTime Occurrence = new(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 17, 7, 0, 5, DateTimeKind.Utc);

    private static ScheduledRunExecution NewExecution() =>
        ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus", ["reader"], Now).Value;

    [Fact]
    public void Create_starts_Pending_with_no_output_and_no_attempts()
    {
        var result = ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus", ["reader"], Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.Step.Should().Be(RunStep.Pending);
        result.Value.Findings.Should().BeNull();
        result.Value.Digest.Should().BeNull();
        result.Value.Attempts.Should().Be(0);
        result.Value.LastError.Should().BeNull();
        result.Value.CreatedAt.Should().Be(Now);
        result.Value.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void Create_rejects_an_empty_conversation_id()
    {
        // a detached run has no live turn to fall back on: with no delivery target the digest
        // would be produced, paid for, and have nowhere to go
        var result = ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "", "schedule:daedalus", ["reader"], Now);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_an_empty_role_set()
    {
        // a principal with no roles can do nothing; that is a configuration error, not a narrow principal
        var result = ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus", [], Now);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_a_non_utc_occurrence()
    {
        // the unique key is (ScheduleId, OccurrenceAt); a local-kind value would make the same
        // instant collide or not depending on the host's timezone
        var result = ScheduledRunExecution.Create(
            ScheduleId, new DateTime(2026, 9, 17, 7, 0, 0, DateTimeKind.Local),
            "telegram", "123456", "schedule:daedalus", ["reader"], Now);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void The_happy_path_walks_Pending_to_Done_carrying_both_outputs()
    {
        var execution = NewExecution();

        execution.BeginScout(Now);
        execution.Step.Should().Be(RunStep.Scout);

        execution.RecordFindings("three open PRs", Now.AddSeconds(10));
        execution.Step.Should().Be(RunStep.Writer);
        execution.Findings.Should().Be("three open PRs");

        execution.RecordDigest("Here is your digest.", Now.AddSeconds(20));
        execution.Step.Should().Be(RunStep.Deliver);
        execution.Digest.Should().Be("Here is your digest.");

        execution.Complete(Now.AddSeconds(30));
        execution.Step.Should().Be(RunStep.Done);
        execution.UpdatedAt.Should().Be(Now.AddSeconds(30));
    }

    [Fact]
    public void Persisted_output_survives_the_transitions_that_follow_it()
    {
        // this is the whole point of resume: a crash in the writer stage must not re-pay for the scout
        var execution = NewExecution();
        execution.BeginScout(Now);
        execution.RecordFindings("three open PRs", Now);
        execution.RecordDigest("Here is your digest.", Now);
        execution.Complete(Now);

        execution.Findings.Should().Be("three open PRs");
    }

    [Fact]
    public void Fail_records_the_error_and_counts_the_attempt()
    {
        var execution = NewExecution();
        execution.BeginScout(Now);

        execution.Fail("provider returned 529", Now.AddSeconds(3));

        execution.Step.Should().Be(RunStep.Failed);
        execution.LastError.Should().Be("provider returned 529");
        execution.Attempts.Should().Be(1);
        execution.UpdatedAt.Should().Be(Now.AddSeconds(3));
    }

    [Fact]
    public void Fail_twice_accumulates_attempts_and_keeps_the_latest_error()
    {
        var execution = NewExecution();
        execution.BeginScout(Now);

        execution.Fail("first", Now);
        execution.Fail("second", Now.AddSeconds(1));

        execution.Attempts.Should().Be(2);
        execution.LastError.Should().Be("second");
    }

    [Fact]
    public void Fail_throws_once_the_execution_is_Done()
    {
        // Done means delivered; turning a successfully delivered execution back into Failed would
        // destroy the (unrecoverable) record that the digest went out
        var execution = NewExecution();
        execution.BeginScout(Now);
        execution.RecordFindings("seed", Now);
        execution.RecordDigest("seed", Now);
        execution.Complete(Now);

        var act = () => execution.Fail("too late", Now);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_default_RunStep_is_Pending_and_therefore_never_a_real_step()
    {
        // AgentErrorCode.Validation being member 0 produced false-passing tests three times on one
        // branch. RunStep's zero value is deliberately the state no dispatcher acts on, so a
        // default(RunStep) can never be mistaken for "the scout should run".
        default(RunStep).Should().Be(RunStep.Pending);
    }

    [Theory]
    [InlineData(RunStep.Pending)]
    [InlineData(RunStep.Writer)]
    [InlineData(RunStep.Deliver)]
    [InlineData(RunStep.Done)]
    [InlineData(RunStep.Failed)]
    public void RecordFindings_throws_unless_the_row_is_in_the_Scout_step(RunStep step)
    {
        var execution = NewExecution();
        Drive(execution, step);

        var act = () => execution.RecordFindings("x", Now);

        act.Should().Throw<InvalidOperationException>(
            "the dispatcher guards the step before calling; reaching here means the guard is gone");
    }

    private static void Drive(ScheduledRunExecution execution, RunStep target)
    {
        if (target is RunStep.Pending) return;
        execution.BeginScout(Now);
        if (target is RunStep.Scout) return;
        execution.RecordFindings("seed", Now);
        if (target is RunStep.Writer) return;
        execution.RecordDigest("seed", Now);
        if (target is RunStep.Deliver) return;
        if (target is RunStep.Done) { execution.Complete(Now); return; }
        execution.Fail("seed", Now);
    }
}
