using AwesomeAssertions.Execution;
using Daedalus.Agents.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     <see cref="WorkflowDispatchTiming"/>: the lease must outlast one dispatch, the options must be recordable
///     by the outbox store, and the stranded-run threshold must exceed every term that can hold a healthy run
///     still.
/// </summary>
public sealed class WorkflowDispatchTimingTests
{
    private static readonly TimeSpan TurnDeadline = TimeSpan.FromSeconds(300);

    // The readiness timeout the dispatch gate is planned with. No gate exists yet, so the host passes zero.
    private static readonly TimeSpan PlannedGateWait = TimeSpan.FromMinutes(10);

    [Fact]
    public void The_shipped_defaults_hold_a_five_minute_turn_behind_a_ten_minute_gate()
    {
        var act = () => WorkflowDispatchTiming.Validate(new WorkflowOutboxDispatchOptions(), TurnDeadline, PlannedGateWait);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(15, 5, 10)] // exactly the dispatch: a turn that runs to its deadline outlives the lease
    [InlineData(10, 5, 10)]
    [InlineData(4, 5, 0)]
    public void A_lease_that_does_not_exceed_the_gate_wait_plus_the_turn_deadline_is_rejected(
        int leaseMinutes, int deadlineMinutes, int gateMinutes)
    {
        var options = new WorkflowOutboxDispatchOptions { LeaseDuration = TimeSpan.FromMinutes(leaseMinutes) };

        var act = () => WorkflowDispatchTiming.Validate(
            options, TimeSpan.FromMinutes(deadlineMinutes), TimeSpan.FromMinutes(gateMinutes));

        act.Should().Throw<InvalidOperationException>().WithMessage("*LeaseDuration*must exceed the longest dispatch*");
    }

    [Fact]
    public void A_lease_longer_than_one_day_is_rejected()
    {
        var options = new WorkflowOutboxDispatchOptions { LeaseDuration = TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1) };

        var act = () => WorkflowDispatchTiming.Validate(options, TurnDeadline, TimeSpan.Zero);

        act.Should().Throw<InvalidOperationException>().WithMessage("*must be at most 1.00:00:00*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_host_id_is_rejected(string hostId)
    {
        var options = new WorkflowOutboxDispatchOptions { HostId = hostId };

        var act = () => WorkflowDispatchTiming.Validate(options, TurnDeadline, TimeSpan.Zero);

        act.Should().Throw<InvalidOperationException>().WithMessage("*HostId must not be empty*");
    }

    [Fact]
    public void A_host_id_wider_than_the_LockedBy_column_is_rejected_and_one_that_fits_is_accepted()
    {
        var tooLong = new WorkflowOutboxDispatchOptions { HostId = new string('h', 129) };
        var fits = new WorkflowOutboxDispatchOptions { HostId = new string('h', 128) };

        var rejectTooLong = () => WorkflowDispatchTiming.Validate(tooLong, TurnDeadline, TimeSpan.Zero);
        var acceptFits = () => WorkflowDispatchTiming.Validate(fits, TurnDeadline, TimeSpan.Zero);

        rejectTooLong.Should().Throw<InvalidOperationException>().WithMessage("*HostId must be at most 128 characters*");
        acceptFits.Should().NotThrow();
    }

    [Fact]
    public void The_default_host_id_fits_the_LockedBy_column_and_names_this_machine()
    {
        var hostId = new WorkflowOutboxDispatchOptions().HostId;
        using var scope = new AssertionScope();

        hostId.Length.Should().BeLessThanOrEqualTo(WorkflowOutboxDispatchOptions.MaxHostIdLength);
        hostId.Should().StartWith(Environment.MachineName + ":");
    }

    [Fact]
    public void A_batch_of_more_than_one_is_rejected()
    {
        var options = new WorkflowOutboxDispatchOptions { BatchSize = 2 };

        var act = () => WorkflowDispatchTiming.Validate(options, TurnDeadline, TimeSpan.Zero);

        act.Should().Throw<InvalidOperationException>().WithMessage("*BatchSize (2) must be 1*");
    }

    [Fact]
    public void The_retry_backoff_window_sums_every_wait_before_the_dead_letter()
    {
        // MaxAttempts 8 is seven retries: 2 + 4 + 8 + 16 + 32 + 64 + 128 seconds.
        using var scope = new AssertionScope();
        new WorkflowOutboxDispatchOptions().RetryBackoffWindow.Should().Be(TimeSpan.FromSeconds(254));
        new WorkflowOutboxDispatchOptions { MaxAttempts = 1 }.RetryBackoffWindow.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void The_stranded_threshold_sums_the_lease_the_gate_the_turn_the_backoff_and_the_margin()
    {
        var options = new WorkflowOutboxDispatchOptions();
        using var scope = new AssertionScope();

        // 20 min lease + 10 min gate + 5 min turn + 254 s backoff + 5 min margin.
        WorkflowDispatchTiming.StrandedAfter(options, TurnDeadline, PlannedGateWait)
            .Should().Be(new TimeSpan(0, 44, 14));

        // With no gate, today's host: the gate term drops out and nothing else does.
        WorkflowDispatchTiming.StrandedAfter(options, TurnDeadline, TimeSpan.Zero)
            .Should().Be(new TimeSpan(0, 34, 14));
    }

    [Fact]
    public void The_stranded_threshold_moves_with_the_lease()
    {
        var shortLease = new WorkflowOutboxDispatchOptions { LeaseDuration = TimeSpan.FromMinutes(10) };
        var longLease = new WorkflowOutboxDispatchOptions { LeaseDuration = TimeSpan.FromMinutes(30) };

        var difference = WorkflowDispatchTiming.StrandedAfter(longLease, TurnDeadline, TimeSpan.Zero)
            - WorkflowDispatchTiming.StrandedAfter(shortLease, TurnDeadline, TimeSpan.Zero);

        difference.Should().Be(TimeSpan.FromMinutes(20));
    }
}
