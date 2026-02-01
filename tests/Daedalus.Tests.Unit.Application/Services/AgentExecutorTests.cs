using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Unit tests for AgentExecutor.
/// </summary>
public class AgentExecutorTests : UnitTestBase
{
    private readonly AgentExecutor _executor;
    private readonly ILogger<AgentExecutor> _mockLogger;

    public AgentExecutorTests()
    {
        _mockLogger = Substitute.For<ILogger<AgentExecutor>>();
        _executor = new AgentExecutor(_mockLogger);
    }

    #region Execution Metadata Tests

    [Fact]
    public async Task ExecuteAgentAsync_CapturesExecutionMetadata()
    {
        // Arrange
        var agent = CreateTestAgent("test-agent", "Test Agent");
        var prompt = "Test prompt for metadata";

        // Act
        var result = await _executor.ExecuteAgentAsync(agent, prompt, _cancellationToken);

        // Assert
        result.Value.Metadata.Should().ContainKey("handler_type");
        result.Value.Metadata.Should().ContainKey("prompt_length");
        result.Value.Metadata.Should().ContainKey("response_length");
        result.Value.Metadata.Should().ContainKey("token_estimate");

        result.Value.Metadata["prompt_length"].Should().Be(prompt.Length);
        result.Value.Metadata["response_length"].Should().Be(result.Value.OutputResponse.Length);
    }

    #endregion

    #region Simulated Response Tests

    [Fact]
    public async Task ExecuteAgentAsync_GeneratesVariedSimulatedResponses()
    {
        // Arrange
        var agent = CreateTestAgent("sim-agent", "Simulated Agent");
        var prompt = "Test prompt";

        // Act - execute multiple times to test variability
        var responses = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var result = await _executor.ExecuteAgentAsync(agent, prompt, _cancellationToken);
            responses.Add(result.Value.OutputResponse);
        }

        // Assert - responses should contain agent metadata
        responses.Should().AllSatisfy(r =>
        {
            r.Should().Contain(agent.Name);
            r.Should().Contain(agent.Organization);
        });

        // All responses should be valid
        responses.Should().AllSatisfy(r => r.Should().NotBeNullOrEmpty());
    }

    #endregion

    #region Helper Methods

    private static AgentMetadata CreateTestAgent(string id, string name, List<string>? tags = null)
    {
        return new AgentMetadata
        {
            Id = id,
            Name = name,
            Description = $"Test agent {name}",
            Tags = tags ?? new List<string> { "testing", "test-agent" },
            Organization = "TestOrg",
            RelevanceScore = 85,
            SourceUrl = new Uri("https://test.example.com")
        };
    }

    #endregion

    #region Agent Execution Tests

    [Fact]
    public async Task ExecuteAgentAsync_WithValidAgent_ReturnsSuccessfulContext()
    {
        // Arrange
        var agent = CreateTestAgent("test-agent", "Test Agent");
        var prompt = "Test prompt";

        // Act
        var result = await _executor.ExecuteAgentAsync(agent, prompt, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Agent.Should().Be(agent);
        result.Value.InputPrompt.Should().Be(prompt);
        result.Value.OutputResponse.Should().NotBeNullOrEmpty();
        result.Value.IsSuccessful.Should().BeTrue();
        result.Value.ExecutionDuration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAgentAsync_WithNullAgent_ReturnsFailure()
    {
        // Act
        var result = await _executor.ExecuteAgentAsync(null!, "prompt", _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Agent cannot be null");
    }

    [Fact]
    public async Task ExecuteAgentAsync_WithEmptyPrompt_ReturnsFailure()
    {
        // Arrange
        var agent = CreateTestAgent("test-agent", "Test Agent");

        // Act
        var result = await _executor.ExecuteAgentAsync(agent, "", _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Prompt cannot be empty");
    }

    #endregion

    #region Local Handler Tests

    [Fact]
    public async Task ExecuteAgentAsync_WithRegisteredHandler_UsesHandler()
    {
        // Arrange
        var agent = CreateTestAgent("custom-agent", "Custom Agent");
        var prompt = "Test prompt";
        var expectedResponse = "Custom handler response";

        // Register handler
        _executor.RegisterAgentHandler("custom-agent", async (p, ct) =>
        {
            await Task.Delay(10, ct);
            return expectedResponse;
        });

        // Act
        var result = await _executor.ExecuteAgentAsync(agent, prompt, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.OutputResponse.Should().Be(expectedResponse);
        result.Value.Metadata["handler_type"].Should().Be("local");
    }

    [Fact]
    public async Task ExecuteAgentAsync_WithoutRegisteredHandler_UsesSimulatedResponse()
    {
        // Arrange
        var agent = CreateTestAgent("unknown-agent", "Unknown Agent");
        var prompt = "Test prompt";

        // Act
        var result = await _executor.ExecuteAgentAsync(agent, prompt, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.OutputResponse.Should().Contain("Agent:");
        result.Value.OutputResponse.Should().Contain(agent.Name);
        result.Value.Metadata["handler_type"].Should().Be("simulated");
    }

    [Fact]
    public void RegisterAgentHandler_WithNullHandler_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Record.Exception(() =>
            _executor.RegisterAgentHandler("agent-id", null!)
        );

        ex.Should().BeOfType<ArgumentNullException>();
    }

    #endregion

    #region Agent Chaining Tests

    [Fact]
    public async Task ExecuteAgentChainAsync_WithValidAgents_ReturnsChainResult()
    {
        // Arrange
        var agents = new List<AgentMetadata>
        {
            CreateTestAgent("agent-1", "Agent One"),
            CreateTestAgent("agent-2", "Agent Two"),
            CreateTestAgent("agent-3", "Agent Three")
        };
        var initialPrompt = "Initial prompt";

        // Act
        var result = await _executor.ExecuteAgentChainAsync(agents, initialPrompt, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.ExecutionHistory.Count.Should().Be(3);
        result.Value.OriginalPrompt.Should().Be(initialPrompt);
        result.Value.IsCompleted.Should().BeTrue();
        result.Value.TotalDuration.Should().BeGreaterThan(TimeSpan.Zero);
        result.Value.FinalOutput.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAgentChainAsync_WithEmptyAgentList_ReturnsFailure()
    {
        // Act
        var result = await _executor.ExecuteAgentChainAsync(new List<AgentMetadata>(), "prompt", _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("At least one agent is required");
    }

    [Fact]
    public async Task ExecuteAgentChainAsync_WithNullAgents_ReturnsFailure()
    {
        // Act
        var result = await _executor.ExecuteAgentChainAsync(null!, "prompt", _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAgentChainAsync_WithEmptyPrompt_ReturnsFailure()
    {
        // Arrange
        var agents = new List<AgentMetadata> { CreateTestAgent("agent-1", "Agent One") };

        // Act
        var result = await _executor.ExecuteAgentChainAsync(agents, "", _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Initial prompt cannot be empty");
    }

    #endregion

    #region Execution History Tests

    [Fact]
    public async Task GetAgentHistoryAsync_ReturnsExecutionHistory()
    {
        // Arrange
        var agent = CreateTestAgent("test-agent", "Test Agent");

        // Execute agent multiple times
        await _executor.ExecuteAgentAsync(agent, "Prompt 1", _cancellationToken);
        await _executor.ExecuteAgentAsync(agent, "Prompt 2", _cancellationToken);

        // Act
        var result = await _executor.GetAgentHistoryAsync("test-agent", _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Count.Should().Be(2);
    }

    [Fact]
    public async Task GetAgentHistoryAsync_WithNoHistory_ReturnsEmptyList()
    {
        // Act
        var result = await _executor.GetAgentHistoryAsync("never-executed-agent", _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Count.Should().Be(0);
    }

    #endregion
}
