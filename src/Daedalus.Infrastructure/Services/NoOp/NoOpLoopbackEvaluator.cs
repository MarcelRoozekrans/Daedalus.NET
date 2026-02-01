using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Infrastructure.Services.NoOp;

/// <summary>
///     No-op loopback evaluator. Always returns "build succeeded, tests passed"
///     so downstream logic doesn't branch.
/// </summary>
public sealed class NoOpLoopbackEvaluator : ILoopbackEvaluator
{
    private static readonly LoopbackResult Success = new()
    {
        BuildSucceeded = true, TestsPassed = true, CompilationErrors = [], TestFailures = []
    };

    public Task<Result<LoopbackResult>> EvaluateAsync(
        string workspacePath, string llmResponse, CancellationToken ct)
        => Task.FromResult(Result.Success(Success));

    public Task<Result<CommandExecutionResult>> RunCommandAsync(
        string workspacePath, string command, string arguments, int timeoutSeconds = 120,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success(new CommandExecutionResult
        {
            ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty
        }));
}
