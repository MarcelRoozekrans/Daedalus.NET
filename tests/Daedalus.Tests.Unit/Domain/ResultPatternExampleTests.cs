using Daedalus.Tests.Unit.Abstractions;

namespace Daedalus.Tests.Unit.Domain;

/// <summary>
///     Example test class demonstrating Railway-Oriented Programming testing patterns.
///     Replace with your actual domain tests.
/// </summary>
public class ResultPatternExampleTests : UnitTestBase
{
    [Fact]
    public void Result_Success_ShouldHaveCorrectValue()
    {
        // Arrange
        const string expected = "test value";

        // Act
        var result = Result.Success(expected);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(expected);
    }

    [Fact]
    public void Result_Failure_ShouldHaveCorrectError()
    {
        // Arrange
        const string expectedError = "Something went wrong";

        // Act
        var result = Result.Failure<string>(expectedError);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(expectedError);
    }

    [Fact]
    public void Result_Bind_WithSuccess_ShouldChainOperations()
    {
        // Arrange
        var initialResult = Result.Success(5);

        // Act
        var finalResult = initialResult
            .Bind(x => Result.Success(x * 2))
            .Bind(x => Result.Success(x + 1));

        // Assert
        finalResult.IsSuccess.Should().BeTrue();
        finalResult.Value.Should().Be(11); // (5 * 2) + 1
    }

    [Fact]
    public void Result_Bind_WithFailure_ShouldShortCircuit()
    {
        // Arrange
        var initialResult = Result.Failure<int>("Initial failure");
        var secondOperationCalled = false;

        // Act
        var finalResult = initialResult
            .Bind(x =>
            {
                secondOperationCalled = true;
                return Result.Success(x * 2);
            });

        // Assert
        finalResult.IsFailure.Should().BeTrue();
        finalResult.Error.Should().Be("Initial failure");
        secondOperationCalled.Should().BeFalse();
    }

    [Fact]
    public void Result_Map_ShouldTransformValue()
    {
        // Arrange
        var result = Result.Success("hello");

        // Act
        var mapped = result.Map(s => s.ToUpperInvariant());

        // Assert
        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be("HELLO");
    }

    [Fact]
    public void Result_Ensure_WithValidCondition_ShouldSucceed()
    {
        // Arrange
        var result = Result.Success(10);

        // Act
        var ensured = result.Ensure(x => x > 0, "Value must be positive");

        // Assert
        ensured.IsSuccess.Should().BeTrue();
        ensured.Value.Should().Be(10);
    }

    [Fact]
    public void Result_Ensure_WithInvalidCondition_ShouldFail()
    {
        // Arrange
        var result = Result.Success(-5);

        // Act
        var ensured = result.Ensure(x => x > 0, "Value must be positive");

        // Assert
        ensured.IsFailure.Should().BeTrue();
        ensured.Error.Should().Be("Value must be positive");
    }

    [Fact]
    public void Result_Match_ShouldHandleBothCases()
    {
        // Arrange
        var successResult = Result.Success("data");
        var failureResult = Result.Failure<string>("error");

        // Act
        var successOutput = successResult.Match(
            value => $"Got: {value}",
            error => $"Failed: {error}");

        var failureOutput = failureResult.Match(
            value => $"Got: {value}",
            error => $"Failed: {error}");

        // Assert
        successOutput.Should().Be("Got: data");
        failureOutput.Should().Be("Failed: error");
    }
}
