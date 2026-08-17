using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using Thalos.Memory;

namespace Daedalus.Tests.Unit.Application.Agents;

public sealed class ReindexPendingMemoriesHostedServiceTests
{
    private readonly IMemoryService _service = Substitute.For<IMemoryService>();
    private readonly IMemoryIndex _index = Substitute.For<IMemoryIndex>();
    private readonly MemoryConfig _config = new();

    private ReindexPendingMemoriesHostedService Sut() =>
        new(_service, _index, _config, new FakeTimeProvider(), NullLogger<ReindexPendingMemoriesHostedService>.Instance);

    [Fact]
    public async Task Unavailable_index_waits_the_retry_interval_and_does_not_reindex()
    {
        _index.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(false, null, "no embedding generator")));

        var next = await Sut().RunOnceAsync(CancellationToken.None);

        next.Should().Be(_config.Reindex.RetryInterval);
        await _service.DidNotReceiveWithAnyArgs().ReindexAsync(default!, default);
    }

    [Fact]
    public async Task Available_index_reindexes_pending_rows_and_sweeps_later_when_nothing_failed()
    {
        _index.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(true, 768, null)));
        _service.ReindexAsync(Arg.Is<ReindexOptions>(o => o.PendingOnly), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<ReindexReport, AgentError>.Success(new ReindexReport(3, 3, 0)));

        var next = await Sut().RunOnceAsync(CancellationToken.None);

        next.Should().Be(_config.Reindex.SweepInterval);
    }

    [Fact]
    public async Task Failed_rows_or_a_failed_reindex_retry_sooner()
    {
        _index.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(true, 768, null)));
        _service.ReindexAsync(Arg.Any<ReindexOptions>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<ReindexReport, AgentError>.Success(new ReindexReport(3, 1, 2)));
        (await Sut().RunOnceAsync(CancellationToken.None)).Should().Be(_config.Reindex.RetryInterval);

        _service.ReindexAsync(Arg.Any<ReindexOptions>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<ReindexReport, AgentError>.Failure(AgentError.MemoryIndexUnavailable("down")));
        (await Sut().RunOnceAsync(CancellationToken.None)).Should().Be(_config.Reindex.RetryInterval);
    }

    [Fact]
    public async Task Exceptions_never_escape()
    {
        _index.ProbeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        (await Sut().RunOnceAsync(CancellationToken.None)).Should().Be(_config.Reindex.RetryInterval);
    }

    /// <summary>
    ///     The loop itself: nothing happens before <c>StartupDelay</c>, each pass waits the interval it returned, and stopping
    ///     the host completes without faulting. Every wait goes through the injected clock — a wall-clock <c>Task.Delay</c>
    ///     would leave the fake clock's advances with no effect and time this test out.
    /// </summary>
    [Fact]
    public async Task Sweep_loop_waits_the_startup_delay_then_probes_once_per_sweep_interval_and_stops_cleanly()
    {
        var probes = 0;
        _index.ProbeAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref probes);
            return ZeroAlloc.Results.Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(true, 768, null));
        });
        _service.ReindexAsync(Arg.Any<ReindexOptions>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<ReindexReport, AgentError>.Success(new ReindexReport(0, 0, 0)));

        var clock = new FakeTimeProvider();
        var sut = new ReindexPendingMemoriesHostedService(
            _service, _index, _config, clock, NullLogger<ReindexPendingMemoriesHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            clock.Advance(_config.Reindex.StartupDelay - TimeSpan.FromSeconds(1));
            await SettleAsync();
            Volatile.Read(ref probes).Should().Be(0, "the first pass waits the whole StartupDelay");

            clock.Advance(TimeSpan.FromSeconds(1));
            await AdvanceUntilAsync(clock, TimeSpan.Zero, () => Volatile.Read(ref probes) >= 1);

            clock.Advance(_config.Reindex.SweepInterval - TimeSpan.FromSeconds(1));
            await SettleAsync();
            Volatile.Read(ref probes).Should().Be(1, "an idle pass sleeps a full SweepInterval before the next one");

            await AdvanceUntilAsync(clock, TimeSpan.FromSeconds(1), () => Volatile.Read(ref probes) >= 2);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        await _service.Received(2).ReindexAsync(Arg.Is<ReindexOptions>(o => o.PendingOnly), Arg.Any<CancellationToken>());
    }

    /// <summary>Gives the loop a moment of real time to react to a clock advance.</summary>
    private static Task SettleAsync() => Task.Delay(50);

    /// <summary>
    ///     Waits for <paramref name="condition"/>, re-advancing the fake clock by <paramref name="interval"/> between attempts:
    ///     the loop registers its next timer asynchronously, so an advance can land while it is still between two awaits.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, TimeSpan interval, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20 && !condition(); attempt++)
        {
            if (attempt > 0)
            {
                clock.Advance(interval == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : interval);
            }

            for (var i = 0; i < 20 && !condition(); i++)
            {
                await Task.Delay(10);
            }
        }

        condition().Should().BeTrue("the sweep loop should have run again within the test's time budget");
    }
}
