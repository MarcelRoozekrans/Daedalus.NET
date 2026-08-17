using Daedalus.Application.Configuration;

namespace Daedalus.Tests.Unit.Application.Configuration;

/// <summary>
///     Unit tests for RalphLoopConfiguration.
///     Tests default values, WithWorkspacePath copy behavior, and property settings.
/// </summary>
public class RalphLoopConfigurationTests
{
    #region Default Values

    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        // Act
        var config = new RalphLoopConfiguration();

        // Assert
        config.IterationDelayMs.Should().Be(100);
        config.MaxConsecutiveFailures.Should().Be(5);
        config.MaxIterations.Should().Be(0);
        config.RequestTimeoutSeconds.Should().Be(300);
        config.EnableDetailedLogging.Should().BeFalse();
        config.EnableGitCheckpoints.Should().BeTrue();
        config.EnableLoopbackEvaluation.Should().BeTrue();
        config.EnableSubagentDelegation.Should().BeTrue();
        config.PlanRegenerationThreshold.Should().Be(3);
        config.WorkspacePath.Should().BeEmpty();
        config.UseArticleStylePrompts.Should().BeTrue();
        config.MaxItemsPerIteration.Should().Be(1);
        config.MaxSearchSubagents.Should().Be(500);
        config.MaxBuildTestSubagents.Should().Be(1);
    }

    [Fact]
    public void SectionName_ShouldBeRalphLoop()
    {
        RalphLoopConfiguration.SectionName.Should().Be("RalphLoop");
    }

    #endregion

    #region WithWorkspacePath

    [Fact]
    public void WithWorkspacePath_ShouldReturnNewInstanceWithPath()
    {
        // Arrange
        var original = new RalphLoopConfiguration();

        // Act
        var copy = original.WithWorkspacePath("/new/path");

        // Assert
        copy.WorkspacePath.Should().Be("/new/path");
        original.WorkspacePath.Should().BeEmpty(); // Original unchanged
    }

    [Fact]
    public void WithWorkspacePath_ShouldCopyAllProperties()
    {
        // Arrange
        var original = new RalphLoopConfiguration
        {
            IterationDelayMs = 200,
            MaxConsecutiveFailures = 10,
            MaxIterations = 50,
            RequestTimeoutSeconds = 600,
            EnableDetailedLogging = true,
            EnableGitCheckpoints = false,
            EnableLoopbackEvaluation = false,
            EnableSubagentDelegation = false,
            PlanRegenerationThreshold = 5,
            WorkspacePath = "/old/path",
            UseArticleStylePrompts = false,
            MaxItemsPerIteration = 5,
            MaxSearchSubagents = 100,
            MaxBuildTestSubagents = 3
        };

        // Act
        var copy = original.WithWorkspacePath("/new/path");

        // Assert
        copy.IterationDelayMs.Should().Be(200);
        copy.MaxConsecutiveFailures.Should().Be(10);
        copy.MaxIterations.Should().Be(50);
        copy.RequestTimeoutSeconds.Should().Be(600);
        copy.EnableDetailedLogging.Should().BeTrue();
        copy.EnableGitCheckpoints.Should().BeFalse();
        copy.EnableLoopbackEvaluation.Should().BeFalse();
        copy.EnableSubagentDelegation.Should().BeFalse();
        copy.PlanRegenerationThreshold.Should().Be(5);
        copy.WorkspacePath.Should().Be("/new/path");
        copy.UseArticleStylePrompts.Should().BeFalse();
        copy.MaxItemsPerIteration.Should().Be(5);
        copy.MaxSearchSubagents.Should().Be(100);
        copy.MaxBuildTestSubagents.Should().Be(3);
    }

    [Fact]
    public void WithWorkspacePath_ShouldNotModifyOriginal()
    {
        // Arrange
        var original = new RalphLoopConfiguration
        {
            WorkspacePath = "/original"
        };

        // Act
        _ = original.WithWorkspacePath("/modified");

        // Assert
        original.WorkspacePath.Should().Be("/original");
    }

    [Fact]
    public void WithWorkspacePath_ShouldReturnDifferentInstance()
    {
        // Arrange
        var original = new RalphLoopConfiguration();

        // Act
        var copy = original.WithWorkspacePath("/path");

        // Assert
        copy.Should().NotBeSameAs(original);
    }

    #endregion

    #region Property Setters

    [Fact]
    public void Properties_ShouldBeSettable()
    {
        // Act
        var config = new RalphLoopConfiguration
        {
            IterationDelayMs = 500,
            MaxConsecutiveFailures = 3,
            MaxIterations = 100,
            RequestTimeoutSeconds = 120,
            EnableDetailedLogging = true,
            EnableGitCheckpoints = false,
            EnableLoopbackEvaluation = false,
            EnableSubagentDelegation = false,
            PlanRegenerationThreshold = 7,
            WorkspacePath = "/custom/path",
            UseArticleStylePrompts = false,
            MaxItemsPerIteration = 10,
            MaxSearchSubagents = 250,
            MaxBuildTestSubagents = 2
        };

        // Assert
        config.IterationDelayMs.Should().Be(500);
        config.MaxConsecutiveFailures.Should().Be(3);
        config.MaxIterations.Should().Be(100);
        config.RequestTimeoutSeconds.Should().Be(120);
        config.EnableDetailedLogging.Should().BeTrue();
        config.EnableGitCheckpoints.Should().BeFalse();
        config.EnableLoopbackEvaluation.Should().BeFalse();
        config.EnableSubagentDelegation.Should().BeFalse();
        config.PlanRegenerationThreshold.Should().Be(7);
        config.WorkspacePath.Should().Be("/custom/path");
        config.UseArticleStylePrompts.Should().BeFalse();
        config.MaxItemsPerIteration.Should().Be(10);
        config.MaxSearchSubagents.Should().Be(250);
        config.MaxBuildTestSubagents.Should().Be(2);
    }

    #endregion
}
