using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;
using Thalos.Memory;
using LearningCategory = Daedalus.Domain.Entities.LearningCategory;
using LearningSeverity = Daedalus.Domain.Entities.LearningSeverity;

namespace Daedalus.Tests.Unit.Application.Agents;

/// <summary>Tests for the <see cref="ILearningsMemory"/> adapter over Thalos' <see cref="IMemoryService"/>.</summary>
public sealed class ThalosLearningsMemoryTests
{
    private readonly IMemoryService _service = Substitute.For<IMemoryService>();
    private readonly MemoryConfig _config = new() { SharedOwnerId = "daedalus" };

    private ThalosLearningsMemory Sut() => new(_service, _config, NullLogger<ThalosLearningsMemory>.Instance);

    private static MemoryRecord Record(string text, params string[] tags) => new()
    {
        Id = MemoryId.New(),
        OwnerId = "daedalus",
        Kind = MemoryKind.Learning,
        Text = text,
        Tags = tags,
        Source = "ralph:task/x",
        Importance = 0.8,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Remember_writes_under_the_shared_owner_as_a_learning()
    {
        var taskId = Guid.NewGuid();
        RememberRequest? seen = null;
        _service.RememberAsync(Arg.Do<RememberRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryRecord, AgentError>.Success(Record("x")));

        var result = await Sut().RememberAsync(
            new ParsedLearning(LearningCategory.ErrorPattern, "CS1061", "add using", ["ef core"], LearningSeverity.High),
            taskId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        seen.Should().NotBeNull();
        seen!.OwnerId.Should().Be("daedalus");
        seen.AgentId.Should().BeNull();
        seen.Kind.Should().Be(MemoryKind.Learning);
        seen.Text.Should().Be("CS1061\nadd using");
        seen.Tags.Should().Equal("errorpattern", "high", "ef core");
        seen.Importance.Should().Be(0.8);
        seen.Source.Should().Be($"ralph:task/{taskId}");
    }

    [Fact]
    public async Task Remember_maps_thalos_errors_to_a_failure_without_throwing()
    {
        _service.RememberAsync(Arg.Any<RememberRequest>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryStoreFailed("down")));

        var result = await Sut().RememberAsync(
            new ParsedLearning(LearningCategory.CodeConvention, "p", "r", [], LearningSeverity.Low),
            Guid.NewGuid(),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("MemoryStoreFailed");
    }

    [Fact]
    public async Task Recall_queries_the_shared_scope_and_projects_hits()
    {
        MemoryScope scope = default;
        RecallOptions? options = null;
        _service.RecallAsync("npgsql timeout", Arg.Do<MemoryScope>(s => scope = s), Arg.Do<RecallOptions>(o => options = o), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<IReadOnlyList<RecalledMemory>, AgentError>.Success(
                [new RecalledMemory(Record("Timeouts: raise CommandTimeout", "errorpattern"), 0.87)]));

        var result = await Sut().RecallAsync("npgsql timeout", 3, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Text = "Timeouts: raise CommandTimeout", Score = 0.87 });
        scope.OwnerId.Should().Be("daedalus");
        scope.AgentId.Should().BeNull();
        options!.TopK.Should().Be(3);
        options.MinScore.Should().Be(_config.RalphRecall.MinScore);
    }

    [Fact]
    public async Task Recall_maps_thalos_errors_to_a_failure()
    {
        _service.RecallAsync(Arg.Any<string>(), Arg.Any<MemoryScope>(), Arg.Any<RecallOptions>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<IReadOnlyList<RecalledMemory>, AgentError>.Failure(AgentError.MemoryIndexUnavailable("no generator")));

        var result = await Sut().RecallAsync("anything", 5, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("MemoryIndexUnavailable");
    }

    [Fact]
    public async Task Recall_short_circuits_a_blank_query()
    {
        var result = await Sut().RecallAsync("   ", 5, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        await _service.DidNotReceive().RecallAsync(Arg.Any<string>(), Arg.Any<MemoryScope>(), Arg.Any<RecallOptions>(), Arg.Any<CancellationToken>());
    }
}
