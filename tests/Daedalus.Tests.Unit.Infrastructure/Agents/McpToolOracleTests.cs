using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Agents;
using Daedalus.Tests.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Infrastructure.Agents;

/// <summary>
///     The oracle for the in-process MCP tools, captured from the reflective registration in
///     <see cref="McpToolBuilder"/> before phase 3.2 built them from an explicit list. Names, descriptions, schemas,
///     order and result bytes are literals; <see cref="McpToolRegistryGuardTests"/> also holds the explicit list to
///     the reflective registration, kept there as test code.
/// </summary>
public sealed class McpToolOracleTests
{
    /// <summary>
    ///     Every character Review Focus 3 names: HTML-sensitive, an apostrophe, a non-ASCII letter and an emoji, so a
    ///     change of encoder shows in the result bytes.
    /// </summary>
    private const string Awkward = "<a & 'é' 🚀>";

    private const string ExpectedDump = """
        search_failure_patterns | Search known failure patterns and their solutions. Use this when you encounter a build error, test failure, or runtime exception to find previously discovered fixes. This data was written only by the autonomous task loop that Daedalus retired in phase 2.8 (October 2026), and nothing adds to it any more: an empty answer does not mean that no such pattern or learning exists. | {"type":"object","properties":{"errorMessage":{"description":"The error message or pattern to search for","type":"string"},"maxResults":{"description":"Maximum number of results (default: 3)","type":"integer","default":3}},"required":["errorMessage"]} | {"type":"string"}
        search_learnings | Search past learnings from previous task executions using semantic similarity. Use this when you encounter errors, need context about the codebase, or want to learn from previous approaches that worked or failed. This data was written only by the autonomous task loop that Daedalus retired in phase 2.8 (October 2026), and nothing adds to it any more: an empty answer does not mean that no such pattern or learning exists. | {"type":"object","properties":{"query":{"description":"Natural language description of what you\u0027re looking for","type":"string"},"maxResults":{"description":"Maximum number of results (default: 5)","type":"integer","default":5}},"required":["query"]} | {"type":"string"}
        """;

    private const string ExpectedLearningsResult = """
        "[\n  {\n    \"id\": \"m1\",\n    \"text\": \"\\u003Ca \\u0026 \\u0027\\u00E9\\u0027 \\uD83D\\uDE80\\u003E\",\n    \"tags\": [\n      \"\\u003Ca \\u0026 \\u0027\\u00E9\\u0027 \\uD83D\\uDE80\\u003E\"\n    ],\n    \"score\": 0.123,\n    \"createdAt\": \"2026-10-22\"\n  }\n]"
        """;

    private const string ExpectedFailurePatternsResult = """
        "[\n  {\n    \"error\": \"\\u003Ca \\u0026 \\u0027\\u00E9\\u0027 \\uD83D\\uDE80\\u003E\",\n    \"solution\": \"\\u003Ca \\u0026 \\u0027\\u00E9\\u0027 \\uD83D\\uDE80\\u003E\",\n    \"sourceTaskId\": \"0b7f3c1e-5a2d-4e8f-9c6b-1d2e3f4a5b6c\",\n    \"errorIteration\": 2,\n    \"resolutionIteration\": 3,\n    \"observedAt\": \"2026-10-22\"\n  }\n]"
        """;

    private const string ExpectedUnavailableResult = """
        "Learnings memory unavailable (<a & 'é' \uD83D\uDE80>). Proceed with available context."
        """;

    private readonly ILearningsMemory _memory = Substitute.For<ILearningsMemory>();
    private readonly IFailurePatternDatabase _failures = Substitute.For<IFailurePatternDatabase>();

    /// <summary>
    ///     Red: swap the two entries of <c>McpToolBuilder.LocalTools</c>; the first line is then
    ///     <c>search_learnings</c>, not <c>search_failure_patterns</c>.
    /// </summary>
    [Fact]
    public async Task The_local_tools_dump_as_captured()
    {
        await using var provider = Provider();
        await using var builder = new McpToolBuilder(provider, NullLogger<McpToolBuilder>.Instance);

        var tools = await LocalToolsAsync(builder);

        ToolDump.Dump(tools).Should().Be(ExpectedDump);
    }

    /// <summary>
    ///     Red: drop <c>WriteIndented = true</c> from <c>McpToolsJsonContext</c>'s generation options; the result
    ///     loses its line breaks and indentation.
    /// </summary>
    [Fact]
    public async Task Search_learnings_returns_the_captured_bytes()
    {
        _memory.RecallAsync(Awkward, 5, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<RecalledLearning>>.Success(
            [new RecalledLearning("m1", Awkward, [Awkward], 0.12345, new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero))]));

        var result = await InvokeAsync("search_learnings", new(StringComparer.Ordinal) { ["query"] = Awkward });

        ToolDump.Result(result).Should().Be(ExpectedLearningsResult);
    }

    /// <summary>
    ///     Red: serialise <c>DaedalusFailurePatternsTools</c>' results with a copy of
    ///     <c>McpToolsJsonContext.Default.Options</c> given <c>Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c>;
    ///     the escaped <c>&lt;</c>, <c>&amp;</c>, <c>'</c> and <c>é</c> turn into the raw characters.
    /// </summary>
    [Fact]
    public async Task Search_failure_patterns_returns_the_captured_bytes()
    {
        _failures.SearchByErrorAsync(Awkward, 3, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<FailurePatternRecord>>.Success(
            [new FailurePatternRecord
            {
                ErrorText = Awkward,
                Resolution = Awkward,
                SourceTaskId = new Guid(0xb7f3c1e, 0x5a2d, 0x4e8f, 0x9c, 0x6b, 0x1d, 0x2e, 0x3f, 0x4a, 0x5b, 0x6c) /* 0b7f3c1e-5a2d-4e8f-9c6b-1d2e3f4a5b6c */,
                ErrorIteration = 2,
                ResolutionIteration = 3,
                ObservedAt = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc),
            }]));

        var result = await InvokeAsync("search_failure_patterns", new(StringComparer.Ordinal) { ["errorMessage"] = Awkward });

        ToolDump.Result(result).Should().Be(ExpectedFailurePatternsResult);
    }

    /// <summary>
    ///     The unavailable answer carries the memory's error as it is, so the awkward characters reach the tool result raw
    ///     and the bytes show the encoder the function marshals its result with.
    ///     Red: give <c>McpToolBuilder</c>'s tool serializer options <c>Encoder = JavaScriptEncoder.Default</c> instead
    ///     of M.E.AI's; the raw characters are escaped.
    /// </summary>
    [Fact]
    public async Task An_unavailable_answer_carries_raw_characters_in_the_captured_bytes()
    {
        _memory.RecallAsync(Awkward, 5, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RecalledLearning>>.Failure(Awkward));

        var result = await InvokeAsync("search_learnings", new(StringComparer.Ordinal) { ["query"] = Awkward });

        ToolDump.Result(result).Should().Be(ExpectedUnavailableResult);
    }

    private async Task<object?> InvokeAsync(string name, Dictionary<string, object?> arguments)
    {
        await using var provider = Provider();
        await using var builder = new McpToolBuilder(provider, NullLogger<McpToolBuilder>.Instance);
        var tool = (AIFunction)(await LocalToolsAsync(builder)).Single(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        return await tool.InvokeAsync(new AIFunctionArguments(arguments, StringComparer.Ordinal), CancellationToken.None);
    }

    private static async Task<IReadOnlyList<AITool>> LocalToolsAsync(McpToolBuilder builder)
    {
        var tools = await builder.BuildToolsAsync(
            new Dictionary<string, McpServerConfig>(StringComparer.Ordinal) { ["daedalus"] = new() { Type = "local" } });

        // Not defensive: a scan that finds nothing would dump an empty string and the oracle would hold nothing.
        tools.Should().HaveCount(2, "both [McpServerToolType] classes carry one tool each");
        return tools;
    }

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_memory);
        services.AddSingleton(_failures);
        return services.BuildServiceProvider();
    }
}
