using System.ComponentModel;
using System.Reflection;
using Daedalus.Agents.Tools;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Agents.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Tools;

namespace Daedalus.Tests.Unit.Application.Agents;

/// <summary>
///     The inner Ralph tool classes are sealed with non-virtual methods, so they cannot be substituted; the wrapper is
///     tested through a real inner tool over a substituted <see cref="IFailurePatternDatabase"/>. Learnings are no longer
///     exposed here — agents recall them automatically and through the Thalos <c>memory__*</c> tools.
/// </summary>
public sealed class DaedalusKnowledgeToolsTests
{
    private readonly IFailurePatternDatabase _failures = Substitute.For<IFailurePatternDatabase>();

    public DaedalusKnowledgeToolsTests() =>
        _failures.SearchByErrorAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<FailurePatternRecord>>.Success([]));

    private DaedalusKnowledgeTools CreateSut() => new(
        new DaedalusFailurePatternsTools(_failures, NullLogger<DaedalusFailurePatternsTools>.Instance));

    [Fact]
    public async Task SearchFailurePatterns_delegates_error_message_and_max_results_to_the_failure_patterns_tool()
    {
        var sut = CreateSut();

        var result = await sut.SearchFailurePatterns("CS0246 type not found", maxResults: 2, ct: CancellationToken.None);

        result.Should().Be("No matching failure patterns found. " + FrozenHistory.Notice);
        await _failures.Received(1).SearchByErrorAsync("CS0246 type not found", 2, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The Thalos-facing tool is the one agents call, and it carries its own copy of the description. Red: drop
    ///     <c>FrozenHistory.Notice</c> from the description on <c>DaedalusKnowledgeTools.SearchFailurePatterns</c>.
    /// </summary>
    [Fact]
    public void The_agent_facing_description_says_its_data_predates_the_retirement() =>
        typeof(DaedalusKnowledgeTools).GetMethod(nameof(DaedalusKnowledgeTools.SearchFailurePatterns))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description.Should().Contain(FrozenHistory.Notice);

    [Fact]
    public async Task LocalToolSource_exposes_only_search_failure_patterns_and_invokes_it_through_a_di_scope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_failures);
        services.AddScoped<DaedalusFailurePatternsTools>();
        await using var provider = services.BuildServiceProvider();

        var source = new LocalToolSource("daedalus", provider, [typeof(DaedalusKnowledgeTools)]);
        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.IsSuccess.Should().BeTrue();
        tools.Value.Select(t => t.Name).Should().BeEquivalentTo(["search_failure_patterns"]);

        var search = (AIFunction)tools.Value.Single(t => string.Equals(t.Name, "search_failure_patterns", StringComparison.Ordinal));
        var arguments = new AIFunctionArguments(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["errorMessage"] = "NU1902 vulnerable package" },
            StringComparer.Ordinal);
        var result = await search.InvokeAsync(arguments, CancellationToken.None);

        result?.ToString().Should().Contain("No matching failure patterns found.").And.Contain(FrozenHistory.Notice);
        await _failures.Received(1).SearchByErrorAsync("NU1902 vulnerable package", 3, Arg.Any<CancellationToken>());
    }
}
