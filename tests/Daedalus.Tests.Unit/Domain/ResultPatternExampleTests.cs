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
        var result = Result<string>.Success(expected);

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
        var result = Result<string>.Failure(expectedError);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(expectedError);
    }

    [Fact]
    public void Result_Bind_WithSuccess_ShouldChainOperations()
    {
        // Arrange
        var initialResult = Result<int>.Success(5);

        // Act
        var finalResult = initialResult
            .Bind(x => Result<int>.Success(x * 2))
            .Bind(x => Result<int>.Success(x + 1));

        // Assert
        finalResult.IsSuccess.Should().BeTrue();
        finalResult.Value.Should().Be(11); // (5 * 2) + 1
    }

    [Fact]
    public void Result_Bind_WithFailure_ShouldShortCircuit()
    {
        // Arrange
        var initialResult = Result<int>.Failure("Initial failure");
        var secondOperationCalled = false;

        // Act
        var finalResult = initialResult
            .Bind(x =>
            {
                secondOperationCalled = true;
                return Result<int>.Success(x * 2);
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
        var result = Result<string>.Success("hello");

        // Act
        var mapped = result.Map(s => s.ToUpperInvariant());

        // Assert
        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be("HELLO");
    }

    // ZeroAlloc.Results has no Ensure(predicate, error) combinator for the single-generic
    // Result<T> — only Bind, Map, Match, Tap, TapError and Combine exist as extension
    // methods (see ZeroAlloc.Results.Extensions), and Ensure itself only has an overload
    // for the two-generic Result<T, E>. The same "validate, then either carry the value
    // forward or fail" intent is expressed directly below with an if/return, which is
    // exactly what a hand-rolled Ensure would do internally.
    private static Result<int> GuardPositive(Result<int> result, int minimum)
    {
        if (result.IsFailure)
        {
            return Result<int>.Failure(result.Error);
        }

        return result.Value > minimum ? result : Result<int>.Failure("Value must be positive");
    }

    [Fact]
    public void Result_GuardCondition_WithValidCondition_ShouldSucceed()
    {
        // Arrange
        var result = Result<int>.Success(10);

        // Act
        var guarded = GuardPositive(result, 0);

        // Assert
        guarded.IsSuccess.Should().BeTrue();
        guarded.Value.Should().Be(10);
    }

    [Fact]
    public void Result_GuardCondition_WithInvalidCondition_ShouldFail()
    {
        // Arrange
        var result = Result<int>.Success(-5);

        // Act
        var guarded = GuardPositive(result, 0);

        // Assert
        guarded.IsFailure.Should().BeTrue();
        guarded.Error.Should().Be("Value must be positive");
    }

    [Fact]
    public void Result_Match_ShouldHandleBothCases()
    {
        // Arrange
        var successResult = Result<string>.Success("data");
        var failureResult = Result<string>.Failure("error");

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
