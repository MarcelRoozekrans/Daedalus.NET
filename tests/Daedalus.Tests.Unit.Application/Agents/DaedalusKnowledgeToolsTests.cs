using Daedalus.Agents.Tools;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Agents.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Tools;
using StructuredLearningEntry = Daedalus.Domain.Entities.StructuredLearningEntry;

namespace Daedalus.Tests.Unit.Application.Agents;

/// <summary>
///     The inner Ralph tool classes are sealed with non-virtual methods, so they cannot be substituted; the wrappers are
///     tested through real inner tools over substituted <see cref="ILearningsRepository"/> /
///     <see cref="IFailurePatternDatabase"/> / <see cref="IEmbeddingService"/>.
/// </summary>
public sealed class DaedalusKnowledgeToolsTests
{
    private readonly ILearningsRepository _learnings = Substitute.For<ILearningsRepository>();
    private readonly IEmbeddingService _embeddings = Substitute.For<IEmbeddingService>();
    private readonly IFailurePatternDatabase _failures = Substitute.For<IFailurePatternDatabase>();

    public DaedalusKnowledgeToolsTests()
    {
        _embeddings.IsAvailable.Returns(false);
        _learnings.SearchByKeywordAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<StructuredLearningEntry>>([]));
        _failures.SearchByErrorAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<FailurePatternRecord>>([]));
    }

    private DaedalusKnowledgeTools CreateSut() => new(
        new DaedalusLearningsTools(_learnings, _embeddings, NullLogger<DaedalusLearningsTools>.Instance),
        new DaedalusFailurePatternsTools(_failures, NullLogger<DaedalusFailurePatternsTools>.Instance));

    [Fact]
    public async Task SearchLearnings_delegates_query_and_max_results_to_the_learnings_tool()
    {
        var sut = CreateSut();

        var result = await sut.SearchLearnings("npgsql timeout", maxResults: 7, ct: CancellationToken.None);

        result.Should().Be("No matching learnings found.");
        await _learnings.Received(1).SearchByKeywordAsync("npgsql timeout", 7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchLearnings_uses_the_inner_defaults_when_only_a_query_is_given()
    {
        var sut = CreateSut();

        await sut.SearchLearnings("build failed", ct: CancellationToken.None);

        await _learnings.Received(1).SearchByKeywordAsync("build failed", 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchFailurePatterns_delegates_error_message_and_max_results_to_the_failure_patterns_tool()
    {
        var sut = CreateSut();

        var result = await sut.SearchFailurePatterns("CS0246 type not found", maxResults: 2, ct: CancellationToken.None);

        result.Should().Be("No matching failure patterns found.");
        await _failures.Received(1).SearchByErrorAsync("CS0246 type not found", 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalToolSource_discovers_both_tools_and_invokes_them_through_a_di_scope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_learnings);
        services.AddSingleton(_embeddings);
        services.AddSingleton(_failures);
        services.AddScoped<DaedalusLearningsTools>();
        services.AddScoped<DaedalusFailurePatternsTools>();
        await using var provider = services.BuildServiceProvider();

        var source = new LocalToolSource("daedalus", provider, [typeof(DaedalusKnowledgeTools)]);
        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.IsSuccess.Should().BeTrue();
        tools.Value.Select(t => t.Name).Should().BeEquivalentTo(["search_learnings", "search_failure_patterns"]);

        var search = (AIFunction)tools.Value.Single(t => string.Equals(t.Name, "search_failure_patterns", StringComparison.Ordinal));
        var arguments = new AIFunctionArguments(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["errorMessage"] = "NU1902 vulnerable package" },
            StringComparer.Ordinal);
        var result = await search.InvokeAsync(arguments, CancellationToken.None);

        result?.ToString().Should().Contain("No matching failure patterns found.");
        await _failures.Received(1).SearchByErrorAsync("NU1902 vulnerable package", 3, Arg.Any<CancellationToken>());
    }
}
