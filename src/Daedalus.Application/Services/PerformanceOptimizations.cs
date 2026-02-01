using System.Text;

namespace Daedalus.Application.Services;

/// <summary>
///     High-performance utility methods for hot paths using Span&lt;T&gt; and modern C# features.
///     These methods avoid allocations and use stack buffers for frequently executed operations.
/// </summary>
public static class PerformanceOptimizations
{
    /// <summary>
    ///     Validates and trims a string using Span&lt;T&gt; for zero-allocation validation.
    ///     Only allocates if the string has leading/trailing whitespace.
    /// </summary>
    /// <param name="value">The string to validate and trim.</param>
    /// <param name="isValidationError">Output error message if validation fails.</param>
    /// <returns>Trimmed string or null if invalid.</returns>
    public static string? ValidateAndTrimString(string? value, out string? isValidationError)
    {
        isValidationError = null;

        if (value == null)
        {
            isValidationError = "Value cannot be null";
            return null;
        }

        // Use Span<T> for whitespace checking - zero allocation
        var span = value.AsSpan();
        var trimmedSpan = span.Trim();

        if (trimmedSpan.IsEmpty)
        {
            isValidationError = "Value cannot be empty or whitespace";
            return null;
        }

        // Only allocate if we actually trimmed something
        if (trimmedSpan.Length == span.Length)
        {
            // String was already trimmed - return as-is
            return value;
        }

        // String had whitespace - allocate trimmed version
        return trimmedSpan.ToString();
    }

    /// <summary>
    ///     Checks if a string contains a target substring using Span&lt;T&gt;.
    ///     Optimized for frequently executed completion promise checks.
    /// </summary>
    public static bool ContainsTarget(ReadOnlySpan<char> source, ReadOnlySpan<char> target)
    {
        if (target.IsEmpty)
        {
            return true;
        }

        if (source.Length < target.Length)
        {
            return false;
        }

        // Use built-in efficient span method
        return source.Contains(target, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Efficiently searches through multiple strings for a target pattern.
    ///     Used in prompt history scanning to avoid repeated allocations.
    /// </summary>
    public static int CountOccurrences(ReadOnlySpan<char> source, char target)
    {
        var count = 0;
        foreach (var c in source)
        {
            if (c == target)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Builds a string with pre-allocated capacity to reduce reallocations.
    ///     Call this before using StringBuilder in hot paths with known approximate size.
    /// </summary>
    public static StringBuilder CreateOptimizedBuilder(int estimatedCapacity = 256)
    {
        // Add 10% buffer for growth without reallocation
        return new StringBuilder(estimatedCapacity + estimatedCapacity / 10);
    }
}
