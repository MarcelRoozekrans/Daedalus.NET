using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Infrastructure.Agents;

/// <summary>Tests for the <c>search_learnings</c> MCP tool now that it is a thin call into <see cref="ILearningsMemory"/>.</summary>
public sealed class DaedalusLearningsToolsTests
{
    private readonly ILearningsMemory _memory = Substitute.For<ILearningsMemory>();

    private DaedalusLearningsTools Sut() => new(_memory, NullLogger<DaedalusLearningsTools>.Instance);

    [Fact]
    public async Task SearchLearnings_recalls_and_formats_json()
    {
        _memory.RecallAsync("npgsql timeout", 5, Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<RecalledLearning>>(
            [new RecalledLearning("m1", "Timeouts\nRaise CommandTimeout", ["errorpattern", "high"], 0.9, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero))]));

        var json = await Sut().SearchLearnings("npgsql timeout");

        json.Should().Contain("\"text\": \"Timeouts").And.Contain("\"score\": 0.9").And.Contain("errorpattern").And.Contain("2026-08-17");
    }

    [Fact]
    public async Task SearchLearnings_reports_no_matches_and_never_throws()
    {
        _memory.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<RecalledLearning>>([]));
        (await Sut().SearchLearnings("x", 3)).Should().Be("No matching learnings found.");

        _memory.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<IReadOnlyList<RecalledLearning>>("MemoryIndexUnavailable: down"));
        (await Sut().SearchLearnings("x", 3)).Should().StartWith("Learnings memory unavailable");
    }

    [Fact]
    public async Task SearchLearnings_clamps_max_results_into_the_supported_range()
    {
        _memory.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<RecalledLearning>>([]));

        await Sut().SearchLearnings("x", 500);
        await Sut().SearchLearnings("x", 0);

        await _memory.Received(1).RecallAsync("x", 20, Arg.Any<CancellationToken>());
        await _memory.Received(1).RecallAsync("x", 1, Arg.Any<CancellationToken>());
    }
}
