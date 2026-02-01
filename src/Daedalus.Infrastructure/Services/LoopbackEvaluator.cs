using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Executes build/test commands and captures output for loop-back injection.
///     Implements the article's key pattern: "tool execution → evaluation → context allocation".
/// </summary>
public sealed partial class LoopbackEvaluator(
    ILogger<LoopbackEvaluator> logger) : ILoopbackEvaluator
{
    /// <summary>Maximum output size to preserve context window budget.</summary>
    private const int _maxOutputLength = 4000;

    public async Task<Result<LoopbackResult>> EvaluateAsync(
        string workspacePath,
        string llmResponse,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result.Failure<LoopbackResult>("Workspace path cannot be empty");
        }

        LogStartingEvaluation(logger, workspacePath);

        // Run build
        var buildResult = await RunCommandAsync(workspacePath, "dotnet", "build --no-restore", 120, ct)
            .ConfigureAwait(false);
        if (buildResult.IsFailure)
        {
            return Result.Failure<LoopbackResult>($"Build command failed to execute: {buildResult.Error}");
        }

        var build = buildResult.Value;
        var compilationErrors = build.Succeeded
            ? Array.Empty<string>()
            : ExtractCompilationErrors(build.StandardOutput + build.StandardError);

        // Run tests only if build succeeded
        CommandExecutionResult? tests = null;
        string[] testFailures = [];
        var testsPassed = 0;
        var testsFailed = 0;
        var testsSkipped = 0;

        if (build.Succeeded)
        {
            var testResult = await RunCommandAsync(workspacePath, "dotnet",
                "test --no-build --verbosity minimal", 180, ct).ConfigureAwait(false);

            if (testResult.IsSuccess)
            {
                tests = testResult.Value;
                testFailures = tests.Succeeded
                    ? []
                    : ExtractTestFailures(tests.StandardOutput + tests.StandardError);

                (testsPassed, testsFailed, testsSkipped) =
                    ParseTestCounts(tests.StandardOutput + tests.StandardError);
            }
        }

        var result = new LoopbackResult
        {
            BuildSucceeded = build.Succeeded,
            BuildOutput = TruncateOutput(build.StandardOutput + build.StandardError),
            TestsPassed = tests?.Succeeded ?? false,
            TestOutput = tests != null
                ? TruncateOutput(tests.StandardOutput + tests.StandardError)
                : string.Empty,
            TestsPassed_Count = testsPassed,
            TestsFailed_Count = testsFailed,
            TestsSkipped_Count = testsSkipped,
            CompilationErrors = compilationErrors,
            TestFailures = testFailures
        };

        LogEvaluationCompleted(logger, result.BuildSucceeded, result.TestsPassed,
            testsPassed, testsFailed);

        return Result.Success(result);
    }

    public async Task<Result<CommandExecutionResult>> RunCommandAsync(
        string workspacePath,
        string command,
        string arguments,
        int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result.Failure<CommandExecutionResult>("Workspace path cannot be empty");
        }

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            return Result.Success(new CommandExecutionResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdoutBuilder.ToString(),
                StandardError = stderrBuilder.ToString(),
                TimedOut = false,
                Duration = stopwatch.Elapsed
            });
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            try
            {
                process.Kill(true);
            }
            catch
            {
                // Best-effort kill
            }

            return Result.Success(new CommandExecutionResult
            {
                ExitCode = -1,
                StandardOutput = stdoutBuilder.ToString(),
                StandardError = "Command timed out",
                TimedOut = true,
                Duration = stopwatch.Elapsed
            });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Result.Failure<CommandExecutionResult>($"Failed to execute '{command}': {ex.Message}");
        }
    }

    private static string[] ExtractCompilationErrors(string output)
    {
        return CompilationErrorRegex()
            .Matches(output)
            .Select(m => m.Value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
    }

    private static string[] ExtractTestFailures(string output)
    {
        return TestFailureRegex()
            .Matches(output)
            .Select(m => m.Value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();
    }

    private static (int passed, int failed, int skipped) ParseTestCounts(string output)
    {
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        var passedMatch = TestPassedCountRegex().Match(output);
        if (passedMatch.Success)
        {
            int.TryParse(passedMatch.Groups[1].Value, CultureInfo.InvariantCulture, out passed);
        }

        var failedMatch = TestFailedCountRegex().Match(output);
        if (failedMatch.Success)
        {
            int.TryParse(failedMatch.Groups[1].Value, CultureInfo.InvariantCulture, out failed);
        }

        var skippedMatch = TestSkippedCountRegex().Match(output);
        if (skippedMatch.Success)
        {
            int.TryParse(skippedMatch.Groups[1].Value, CultureInfo.InvariantCulture, out skipped);
        }

        return (passed, failed, skipped);
    }

    private static string TruncateOutput(string output)
    {
        if (output.Length <= _maxOutputLength)
        {
            return output;
        }

        // Keep the last portion since errors tend to appear at the end
        return "... [truncated] ...\n" + output[^(_maxOutputLength - 30)..];
    }

    [GeneratedRegex(@"error\s+\w+\s*:.*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.NonBacktracking)]
    private static partial Regex CompilationErrorRegex();

    [GeneratedRegex(@"Failed\s+\w+.*", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.NonBacktracking)]
    private static partial Regex TestFailureRegex();

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Starting loop-back evaluation in {WorkspacePath}")]
    private static partial void LogStartingEvaluation(ILogger logger, string workspacePath);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message =
            "Loop-back evaluation completed: build_ok={BuildOk}, tests_ok={TestsOk}, passed={Passed}, failed={Failed}")]
    private static partial void LogEvaluationCompleted(ILogger logger, bool buildOk, bool testsOk, int passed,
        int failed);

#pragma warning disable MA0023 // Simple \d+ patterns — ExplicitCapture not needed, NonBacktracking prevents ReDoS
    [GeneratedRegex(@"Passed:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex TestPassedCountRegex();

    [GeneratedRegex(@"Failed:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex TestFailedCountRegex();

    [GeneratedRegex(@"Skipped:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex TestSkippedCountRegex();
#pragma warning restore MA0023
}
