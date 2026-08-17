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
}
