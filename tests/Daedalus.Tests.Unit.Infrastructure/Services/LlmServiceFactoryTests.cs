using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Llm;
using Daedalus.Tests.Unit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Unit tests for the LlmServiceFactory that resolves LLM providers by name.
/// </summary>
public class LlmServiceFactoryTests : UnitTestBase
{
    private readonly ILogger<LlmServiceFactory> _logger = Substitute.For<ILogger<LlmServiceFactory>>();

    private static ILlmService CreateStubService(string providerName, bool supportsSubagents = false)
    {
        var service = Substitute.For<ILlmService>();
        service.ProviderName.Returns(providerName);
        service.SupportsSubagents.Returns(supportsSubagents);
        return service;
    }

    #region GetDefault

    [Fact]
    public void GetDefault_WithMatchingDefaultProvider_ShouldReturnCorrectService()
    {
        // Arrange
        var copilot = CreateStubService("copilot");
        var claude = CreateStubService("claude", true);
        var factory = new LlmServiceFactory([copilot, claude], "claude", _logger);

        // Act
        var result = factory.GetDefault();

        // Assert
        result.Should().BeSameAs(claude);
    }

    [Fact]
    public void GetDefault_WithNonExistentDefaultProvider_ShouldFallBackToFirstAvailable()
    {
        // Arrange
        var copilot = CreateStubService("copilot");
        var factory = new LlmServiceFactory([copilot], "claude", _logger);

        // Act
        var result = factory.GetDefault();

        // Assert
        result.Should().BeSameAs(copilot);
    }

    [Fact]
    public void GetDefault_WithNoProviders_ShouldThrowInvalidOperation()
    {
        // Arrange
        var factory = new LlmServiceFactory([], "copilot", _logger);

        // Act
        var act = () => factory.GetDefault();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No LLM providers registered*");
    }

    #endregion

    #region GetByProvider

    [Fact]
    public void GetByProvider_WithExistingProvider_ShouldReturnSuccess()
    {
        // Arrange
        var claude = CreateStubService("claude", true);
        var factory = new LlmServiceFactory([claude], "claude", _logger);

        // Act
        var result = factory.GetByProvider("claude");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(claude);
    }

    [Fact]
    public void GetByProvider_IsCaseInsensitive()
    {
        // Arrange
        var claude = CreateStubService("claude");
        var factory = new LlmServiceFactory([claude], "claude", _logger);

        // Act
        var result = factory.GetByProvider("CLAUDE");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(claude);
    }

    [Fact]
    public void GetByProvider_WithNonExistentProvider_ShouldReturnFailure()
    {
        // Arrange
        var copilot = CreateStubService("copilot");
        var factory = new LlmServiceFactory([copilot], "copilot", _logger);

        // Act
        var result = factory.GetByProvider("claude");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("claude").And.Contain("not found");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetByProvider_WithEmptyProviderName_ShouldReturnFailure(string? providerName)
    {
        // Arrange
        var factory = new LlmServiceFactory([CreateStubService("copilot")], "copilot", _logger);

        // Act
        var result = factory.GetByProvider(providerName!);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("cannot be empty");
    }

    #endregion

    #region GetWithSubagentSupport

    [Fact]
    public void GetWithSubagentSupport_WithSubagentCapableProvider_ShouldReturnSuccess()
    {
        // Arrange
        var copilot = CreateStubService("copilot", false);
        var claude = CreateStubService("claude", true);
        var factory = new LlmServiceFactory([copilot, claude], "copilot", _logger);

        // Act
        var result = factory.GetWithSubagentSupport();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(claude);
    }

    [Fact]
    public void GetWithSubagentSupport_WithNoSubagentProviders_ShouldReturnFailure()
    {
        // Arrange
        var copilot = CreateStubService("copilot", false);
        var factory = new LlmServiceFactory([copilot], "copilot", _logger);

        // Act
        var result = factory.GetWithSubagentSupport();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("subagent");
    }

    #endregion

    #region GetAvailableProviders

    [Fact]
    public void GetAvailableProviders_ShouldReturnAllRegisteredProviderNames()
    {
        // Arrange
        var copilot = CreateStubService("copilot");
        var claude = CreateStubService("claude");
        var factory = new LlmServiceFactory([copilot, claude], "copilot", _logger);

        // Act
        var providers = factory.GetAvailableProviders();

        // Assert
        providers.Should().HaveCount(2);
        providers.Should().Contain("copilot");
        providers.Should().Contain("claude");
    }

    [Fact]
    public void GetAvailableProviders_WithNoProviders_ShouldReturnEmptyList()
    {
        // Arrange
        var factory = new LlmServiceFactory([], "copilot", _logger);

        // Act
        var providers = factory.GetAvailableProviders();

        // Assert
        providers.Should().BeEmpty();
    }

    #endregion
}
