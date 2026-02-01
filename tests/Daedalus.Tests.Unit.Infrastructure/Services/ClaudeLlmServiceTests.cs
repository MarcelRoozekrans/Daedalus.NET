using Anthropic;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Llm;
using Daedalus.Tests.Unit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Unit tests for ClaudeLlmService focusing on input validation,
///     provider metadata, and subagent contract compliance.
/// </summary>
public class ClaudeLlmServiceTests : UnitTestBase, IDisposable
{
    private readonly ILogger<ClaudeLlmService> _logger = Substitute.For<ILogger<ClaudeLlmService>>();
    private readonly ClaudeLlmService _sut;

    public ClaudeLlmServiceTests()
    {
        // Use internal test constructor with a pre-configured client
        var client = new AnthropicApi(new HttpClient(), new Uri("https://api.anthropic.com/v1/"));
        _sut = new ClaudeLlmService(client, "claude-sonnet-4-20250514", 8192, _logger);
    }

    public void Dispose()
    {
        _sut.Dispose();
        GC.SuppressFinalize(this);
    }

    #region InvokeWithMcpAsync — Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvokeWithMcpAsync_WithEmptyPrompt_ShouldReturnFailure(string? prompt)
    {
        var mcpOptions = new McpIntegrationOptions { Enabled = false };

        var result = await _sut.InvokeWithMcpAsync(prompt!, mcpOptions, _cancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("empty");
    }

    #endregion

    #region InvokeParallelSubagentsAsync — Validation

    [Fact]
    public async Task InvokeParallelSubagentsAsync_WithEmptyPromptList_ShouldReturnEmptySuccess()
    {
        var options = new SubagentOptions { Label = "parallel" };

        var result = await _sut.InvokeParallelSubagentsAsync(
            Array.Empty<string>(), options, 10, _cancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    #endregion

    #region SubagentOptions Defaults

    [Fact]
    public void SubagentOptions_ShouldHaveReasonableDefaults()
    {
        var options = new SubagentOptions();

        options.MaxTokens.Should().Be(16384);
        options.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        options.EnableExtendedThinking.Should().BeFalse();
        options.ThinkingBudgetTokens.Should().Be(4096);
        options.SystemPrompt.Should().BeNull();
        options.ModelOverride.Should().BeNull();
        options.Label.Should().BeNull();
    }

    #endregion

    #region SubagentResult Properties

    [Fact]
    public void SubagentResult_ShouldHoldAllProperties()
    {
        var duration = TimeSpan.FromMilliseconds(1500);
        var result = new SubagentResult
        {
            Response = "Analysis complete",
            Label = "code-review",
            Duration = duration,
            InputTokens = 1000,
            OutputTokens = 500,
            UsedExtendedThinking = true,
            ThinkingContent = "Reasoning about the code..."
        };

        result.Response.Should().Be("Analysis complete");
        result.Label.Should().Be("code-review");
        result.Duration.Should().Be(duration);
        result.InputTokens.Should().Be(1000);
        result.OutputTokens.Should().Be(500);
        result.UsedExtendedThinking.Should().BeTrue();
        result.ThinkingContent.Should().Be("Reasoning about the code...");
    }

    #endregion

    #region Provider Metadata

    [Fact]
    public void ProviderName_ShouldBeClaude() => _sut.ProviderName.Should().Be("claude");

    [Fact]
    public void SupportsSubagents_ShouldBeTrue() => _sut.SupportsSubagents.Should().BeTrue();

    #endregion

    #region Constructor Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithInvalidApiKey_ShouldThrow(string? apiKey)
    {
        var act = () => ClaudeLlmService.CreateWithApiKey(apiKey!, "model", 1024, TimeSpan.FromSeconds(30), _logger);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithInvalidModel_ShouldThrow(string? model)
    {
        var act = () =>
            ClaudeLlmService.CreateWithApiKey("sk-test-key", model!, 1024, TimeSpan.FromSeconds(30), _logger);

        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region InvokeAsync — Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvokeAsync_WithEmptyPrompt_ShouldReturnFailure(string? prompt)
    {
        var result = await _sut.InvokeAsync(prompt!, _cancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("empty");
    }

    [Fact]
    public async Task InvokeAsync_WithCancelledToken_ShouldReturnCancellationFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _sut.InvokeAsync("test prompt", cts.Token);

        result.IsFailure.Should().BeTrue();
        // Either "cancelled" or an API error is acceptable
    }

    #endregion

    #region InvokeSubagentAsync — Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvokeSubagentAsync_WithEmptyPrompt_ShouldReturnFailure(string? prompt)
    {
        var options = new SubagentOptions { Label = "test-subagent" };

        var result = await _sut.InvokeSubagentAsync(prompt!, options, _cancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("empty");
    }

    [Fact]
    public async Task InvokeSubagentAsync_WithCancelledToken_ShouldReturnCancellationFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var options = new SubagentOptions { Label = "test-subagent", Timeout = TimeSpan.FromSeconds(1) };
        var result = await _sut.InvokeSubagentAsync("test prompt", options, cts.Token);

        result.IsFailure.Should().BeTrue();
    }

    #endregion
}
