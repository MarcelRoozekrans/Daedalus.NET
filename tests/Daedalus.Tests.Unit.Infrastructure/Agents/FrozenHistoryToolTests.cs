using System.ComponentModel;
using System.Reflection;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Infrastructure.Agents;

/// <summary>
///     Phase 2.8, D3 and A5: nothing writes failure patterns or shared learnings any more, so both tools tell the model
///     that their data predates the retirement. Without that, an empty answer reads as "no such pattern exists".
/// </summary>
public sealed class FrozenHistoryToolTests
{
    private const string Marker = "retired in phase 2.8";

    private static string DescriptionOf<T>(string method) =>
        typeof(T).GetMethod(method)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

    /// <summary>Red: drop <c>FrozenHistory.Notice</c> from the attribute; the description lacks the marker.</summary>
    [Fact]
    public void The_failure_patterns_description_says_its_data_predates_the_retirement() =>
        DescriptionOf<DaedalusFailurePatternsTools>(nameof(DaedalusFailurePatternsTools.SearchFailurePatterns)).Should().Contain(Marker);

    /// <summary>Red: drop <c>FrozenHistory.Notice</c> from the attribute.</summary>
    [Fact]
    public void The_learnings_description_says_its_data_predates_the_retirement() =>
        DescriptionOf<DaedalusLearningsTools>(nameof(DaedalusLearningsTools.SearchLearnings)).Should().Contain(Marker);

    /// <summary>Red: return the bare "No matching failure patterns found." text.</summary>
    [Fact]
    public async Task An_empty_failure_pattern_answer_carries_the_notice()
    {
        var database = Substitute.For<IFailurePatternDatabase>();
        database.SearchByErrorAsync(default!, default, default).ReturnsForAnyArgs(
            Task.FromResult(Result<IReadOnlyList<FailurePatternRecord>>.Success([])));

        var answer = await new DaedalusFailurePatternsTools(database, NullLogger<DaedalusFailurePatternsTools>.Instance)
            .SearchFailurePatterns("CS0103");

        answer.Should().Contain(Marker);
    }

    /// <summary>Red: return the bare "No matching learnings found." text.</summary>
    [Fact]
    public async Task An_empty_learnings_answer_carries_the_notice()
    {
        var memory = Substitute.For<ILearningsMemory>();
        memory.RecallAsync(default!, default, default).ReturnsForAnyArgs(
            Task.FromResult(Result<IReadOnlyList<RecalledLearning>>.Success([])));

        var answer = await new DaedalusLearningsTools(memory, NullLogger<DaedalusLearningsTools>.Instance).SearchLearnings("retry");

        answer.Should().Contain(Marker);
    }
}
