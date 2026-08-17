using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Tests for the persistence/enrichment halves of <see cref="LearningsService"/> now that both run through the
///     <see cref="ILearningsMemory"/> port instead of the hand-rolled learnings repository + embedding service.
/// </summary>
public sealed class LearningsServicePersistenceTests
{
    private readonly ILearningsMemory _memory = Substitute.For<ILearningsMemory>();
    private readonly IFailurePatternDatabase _failures = Substitute.For<IFailurePatternDatabase>();

    private LearningsService Sut() => new(_memory, _failures, NullLogger<LearningsService>.Instance);

    [Fact]
    public async Task ParseAndPersist_remembers_every_parsed_entry_under_the_task_source()
    {
        var taskId = Guid.NewGuid();
        _memory.RememberAsync(Arg.Any<ParsedLearning>(), taskId, Arg.Any<CancellationToken>()).Returns(Result.Success("id"));

        var result = await Sut().ParseAndPersistLearningsAsync(
            "⚠ 3 errors encountered:\n  - CS1061: Type does not contain definition\n✓ Success achieved at iteration 5",
            taskId,
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);
        await _memory.Received(2).RememberAsync(Arg.Any<ParsedLearning>(), taskId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ParseAndPersist_counts_only_successful_remembers_and_never_throws()
    {
        var taskId = Guid.NewGuid();
        _memory.RememberAsync(Arg.Any<ParsedLearning>(), taskId, Arg.Any<CancellationToken>()).Returns(Result.Failure<string>("index down"));

        var result = await Sut().ParseAndPersistLearningsAsync(
            "Error: Missing using directive for ZLinq\n- Use AsValueEnumerable() from ZLinq namespace",
            taskId,
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
    }

    [Fact]
    public async Task ParseAndPersist_returns_zero_for_blank_input_without_touching_memory()
    {
        var result = await Sut().ParseAndPersistLearningsAsync("   ", Guid.NewGuid(), null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
        await _memory.DidNotReceive().RememberAsync(Arg.Any<ParsedLearning>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enrichment_recalls_learnings_for_the_prompt_and_appends_failure_patterns()
    {
        _memory.RecallAsync("implement the EF migration", 10, Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<RecalledLearning>>(
        [
            new RecalledLearning("m1", "Migration needs pgvector\nUse pgvector/pgvector:pg16", ["errorpattern", "high", "migration"], 0.91, DateTimeOffset.UtcNow),
        ]));
        _failures.SearchByPromptContextAsync("implement the EF migration", 5, Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<FailurePatternRecord>>(
        [
            new FailurePatternRecord
            {
                ErrorText = "NU1903 vulnerable",
                Resolution = "bump the package",
                SourceTaskId = Guid.NewGuid(),
                ErrorIteration = 1,
                ResolutionIteration = 2,
                ObservedAt = DateTime.UtcNow,
            },
        ]));

        var result = await Sut().GetEnrichmentContextAsync("implement the EF migration", null, Guid.NewGuid(), 10, 5, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("=== CROSS-TASK LEARNINGS ===").And.Contain("Migration needs pgvector").And.Contain("[errorpattern, high, migration]");
        result.Value.Should().Contain("=== KNOWN FAILURE PATTERNS ===").And.Contain("NU1903 vulnerable");
    }

    [Fact]
    public async Task Enrichment_is_empty_when_nothing_is_recalled_and_no_patterns_match()
    {
        _memory.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<IReadOnlyList<RecalledLearning>>("unavailable"));
        _failures.SearchByPromptContextAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<FailurePatternRecord>>([]));

        var result = await Sut().GetEnrichmentContextAsync("x", null, Guid.NewGuid(), 10, 5, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
