namespace Daedalus.Application.Abstractions;

/// <summary>
///     Result of a single command execution for loop-back feedback.
/// </summary>
public sealed class CommandExecutionResult
{
    /// <summary>The exit code of the process.</summary>
    public int ExitCode { get; init; }

    /// <summary>Standard output, truncated if too large.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Standard error, truncated if too large.</summary>
    public string StandardError { get; init; } = string.Empty;

    /// <summary>Whether the command succeeded (exit code 0).</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>Whether the command timed out.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Execution duration.</summary>
    public TimeSpan Duration { get; init; }
}
