using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Memory;

namespace Daedalus.Tests.Unit.Application.Agents;

/// <summary>Tests for the <see cref="ILearningsMemory"/> adapter over Thalos' <see cref="IMemoryService"/>.</summary>
public sealed class ThalosLearningsMemoryTests
{
    private readonly IMemoryService _service = Substitute.For<IMemoryService>();
    private readonly MemoryOptions _memoryOptions = new() { SharedOwnerId = "daedalus" };
    private readonly LearningsRecallConfiguration _recall = new();

    private ThalosLearningsMemory Sut() =>
        new(_service, Options.Create(_memoryOptions), _recall, NullLogger<ThalosLearningsMemory>.Instance);

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
    public async Task Recall_queries_the_shared_scope_and_projects_hits()
    {
        MemoryScope scope = default;
        RecallOptions? options = null;
        _service.RecallAsync("npgsql timeout", Arg.Do<MemoryScope>(s => scope = s), Arg.Do<RecallOptions>(o => options = o), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryRecallResult, AgentError>.Success(
                new MemoryRecallResult([new RecalledMemory(Record("Timeouts: raise CommandTimeout", "errorpattern"), 0.87)], MemoryRecallTier.Semantic)));

        var result = await Sut().RecallAsync("npgsql timeout", 3, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Text = "Timeouts: raise CommandTimeout", Score = 0.87 });
        scope.OwnerId.Should().Be("daedalus");
        scope.AgentId.Should().BeNull();
        options!.TopK.Should().Be(3);
        options.MinScore.Should().Be(_recall.MinScore);
    }

    [Fact]
    public async Task Recall_maps_thalos_errors_to_a_failure()
    {
        _service.RecallAsync(Arg.Any<string>(), Arg.Any<MemoryScope>(), Arg.Any<RecallOptions>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryRecallResult, AgentError>.Failure(AgentError.MemoryIndexUnavailable("no generator")));

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

    [Fact]
    public async Task Recall_without_a_shared_owner_fails_without_calling_thalos()
    {
        _memoryOptions.SharedOwnerId = null;

        var result = await Sut().RecallAsync("anything", 5, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No shared memory owner");
        await _service.DidNotReceive().RecallAsync(Arg.Any<string>(), Arg.Any<MemoryScope>(), Arg.Any<RecallOptions>(), Arg.Any<CancellationToken>());
    }
}
