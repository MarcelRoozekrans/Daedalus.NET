namespace Daedalus.Tests.Integration.Helpers;

/// <summary>
///     Extension methods for <see cref="Result{T}" /> assertions in tests.
///     Provides safe value extraction with clear error messaging.
/// </summary>
public static class ResultTestExtensions
{
    /// <summary>
    ///     Extracts the value from a <see cref="Result{T}" />, ensuring success.
    ///     Throws <see cref="InvalidOperationException" /> if the result is a failure.
    /// </summary>
    /// <typeparam name="T">The value type</typeparam>
    /// <param name="result">The result to extract from</param>
    /// <param name="contextMessage">Optional context message for the exception</param>
    /// <returns>The successful value</returns>
    /// <exception cref="InvalidOperationException">Thrown if result is a failure</exception>
    public static T MustSucceed<T>(this Result<T> result, string? contextMessage = null)
    {
        return result.IsFailure
            ? throw new InvalidOperationException(
                $"{contextMessage ?? "Operation failed"}: {result.Error}")
            : result.Value;
    }

    /// <summary>
    ///     Extracts the error message, ensuring failure.
    ///     Throws <see cref="InvalidOperationException" /> if the result is a success.
    /// </summary>
    /// <typeparam name="T">The value type</typeparam>
    /// <param name="result">The result to extract error from</param>
    /// <param name="contextMessage">Optional context message for the exception</param>
    /// <returns>The error message</returns>
    /// <exception cref="InvalidOperationException">Thrown if result is a success</exception>
    public static string MustFail<T>(this Result<T> result, string? contextMessage = null)
    {
        return result.IsSuccess
            ? throw new InvalidOperationException(
                $"{contextMessage ?? "Operation succeeded unexpectedly"}")
            : result.Error;
    }

    /// <summary>
    ///     Ensures a <see cref="Result" /> (non-generic) is successful.
    ///     Throws <see cref="InvalidOperationException" /> if the result is a failure.
    /// </summary>
    /// <param name="result">The result to check</param>
    /// <param name="contextMessage">Optional context message for the exception</param>
    /// <exception cref="InvalidOperationException">Thrown if result is a failure</exception>
    public static void EnsureSuccess(this Result result, string? contextMessage = null)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"{contextMessage ?? "Operation failed"}: {result.Error}");
        }
    }

    /// <summary>
    ///     Ensures a <see cref="Result" /> (non-generic) fails, and returns the error.
    ///     Throws <see cref="InvalidOperationException" /> if the result succeeds.
    /// </summary>
    /// <param name="result">The result to check</param>
    /// <param name="contextMessage">Optional context message for the exception</param>
    /// <returns>The error message</returns>
    /// <exception cref="InvalidOperationException">Thrown if result is a success</exception>
    public static string EnsureFailure(this Result result, string? contextMessage = null)
    {
        return result.IsSuccess
            ? throw new InvalidOperationException(
                $"{contextMessage ?? "Operation succeeded unexpectedly"}")
            : result.Error;
    }
}
