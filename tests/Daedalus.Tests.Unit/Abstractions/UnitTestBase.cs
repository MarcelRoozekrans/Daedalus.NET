#pragma warning disable CA1707, CA1724

namespace Daedalus.Tests.Unit.Abstractions;

/// <summary>
///     Base class for unit tests providing common utilities.
/// </summary>
public abstract class UnitTestBase
{
    protected static readonly CancellationToken _cancellationToken = CancellationToken.None;

    /// <summary>
    ///     Creates a successful result for testing.
    /// </summary>
    protected static Result<T> Success<T>(T value) => Result.Success(value);

    /// <summary>
    ///     Creates a failure result for testing.
    /// </summary>
    protected static Result<T> Failure<T>(string error) => Result.Failure<T>(error);
}
