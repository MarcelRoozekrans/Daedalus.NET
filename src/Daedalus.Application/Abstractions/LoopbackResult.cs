using System.Text;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Result of loop-back evaluation to be injected into the next iteration's prompt.
/// </summary>
public sealed class LoopbackResult
{
    /// <summary>Whether the build succeeded.</summary>
    public bool BuildSucceeded { get; init; }

    /// <summary>Build output (stdout + stderr), truncated to preserve context window.</summary>
    public string BuildOutput { get; init; } = string.Empty;

    /// <summary>Whether all tests passed.</summary>
    public bool TestsPassed { get; init; }

    /// <summary>Test output summary, truncated to preserve context window.</summary>
    public string TestOutput { get; init; } = string.Empty;

    /// <summary>Number of tests passed / failed / skipped.</summary>
    public int TestsPassed_Count { get; init; }

    public int TestsFailed_Count { get; init; }

    public int TestsSkipped_Count { get; init; }

    /// <summary>Any compilation errors extracted from build output.</summary>
    public IReadOnlyList<string> CompilationErrors { get; init; } = [];

    /// <summary>Any test failure messages extracted from test output.</summary>
    public IReadOnlyList<string> TestFailures { get; init; } = [];

    /// <summary>
    ///     Generates a compact summary for injection into the next iteration's prompt.
    ///     Keeps context window usage minimal per the article's guidance.
    /// </summary>
    public string ToPromptSection()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== LOOP-BACK EVALUATION RESULTS ===");

        if (BuildSucceeded)
        {
            sb.AppendLine("BUILD: SUCCESS");
        }
        else
        {
            sb.AppendLine("BUILD: FAILED");
            foreach (var error in CompilationErrors.Take(10))
            {
                sb.Append("  ERROR: ").AppendLine(error);
            }

            if (CompilationErrors.Count > 10)
            {
                sb.Append("  ... and ").Append(CompilationErrors.Count - 10).AppendLine(" more errors");
            }
        }

        if (TestsPassed)
        {
            sb.Append("TESTS: ALL PASSED (").Append(TestsPassed_Count)
                .Append(" passed, ").Append(TestsSkipped_Count).AppendLine(" skipped)");
        }
        else
        {
            sb.Append("TESTS: FAILED (").Append(TestsPassed_Count)
                .Append(" passed, ").Append(TestsFailed_Count)
                .Append(" failed, ").Append(TestsSkipped_Count).AppendLine(" skipped)");
            foreach (var failure in TestFailures.Take(5))
            {
                sb.Append("  FAILURE: ").AppendLine(failure);
            }

            if (TestFailures.Count > 5)
            {
                sb.Append("  ... and ").Append(TestFailures.Count - 5).AppendLine(" more failures");
            }
        }

        return sb.ToString();
    }
}
