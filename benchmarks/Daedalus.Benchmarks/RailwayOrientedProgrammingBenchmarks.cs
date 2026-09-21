namespace Daedalus.Benchmarks;

using ZeroAlloc.Results;
using ZeroAlloc.Results.Extensions;
using System.Globalization;

/// <summary>
/// Benchmarks for Railway-Oriented Programming patterns using Result{T}.
/// Measures performance of Result chaining, binding, and mapping operations.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class RailwayOrientedProgrammingBenchmarks
{
    private string _validInput = default!;
    private string _invalidInput = default!;

    [GlobalSetup]
    public void Setup()
    {
        _validInput = "Valid test data";
        _invalidInput = "";
    }

    [Benchmark(Description = "Result.Success creation")]
    public Result<string> ResultSuccess()
    {
        return Result<string>.Success(_validInput);
    }

    [Benchmark(Description = "Result.Failure creation")]
    public Result<string> ResultFailure()
    {
        return Result<string>.Failure("Error message");
    }

    [Benchmark(Description = "Result.Map operation")]
    public Result<int> ResultMap()
    {
        return Result<string>.Success(_validInput)
            .Map(x => x.Length);
    }

    [Benchmark(Description = "Result.Bind operation")]
    public Result<bool> ResultBind()
    {
        return Result<string>.Success(_validInput)
            .Bind(x => x.Length > 0 
                ? Result<bool>.Success(true) 
                : Result<bool>.Failure("Empty string"));
    }

    [Benchmark(Description = "Result chain - 3 operations")]
    public Result<string> ResultChain()
    {
        return Result<string>.Success(_validInput)
            .Map(x => x.ToUpperInvariant())
            .Map(x => $"[{x}]")
            .Map(x => x + "_suffix");
    }

    [Benchmark(Description = "Result - IsSuccess check")]
    public bool ResultIsSuccess()
    {
        var result = Result<string>.Success(_validInput);
        return result.IsSuccess;
    }

    [Benchmark(Description = "Result - IsFailure check")]
    public bool ResultIsFailure()
    {
        var result = Result<string>.Failure("Error");
        return result.IsFailure;
    }

    [Benchmark(Description = "Result - Match success case")]
    public string ResultMatchSuccess()
    {
        return Result<string>.Success(_validInput)
            .Match(
                success => success.ToUpperInvariant(),
                error => error);
    }

    [Benchmark(Description = "Result - Match failure case")]
    public string ResultMatchFailure()
    {
        return Result<string>.Failure("Error message")
            .Match(
                success => success,
                error => $"Error: {error}");
    }

    [Benchmark(Description = "Conditional Result creation - valid")]
    public Result<string> ConditionalValid()
    {
        if (string.IsNullOrEmpty(_validInput))
            return Result<string>.Failure("Input cannot be empty");
        
        return Result<string>.Success(_validInput);
    }

    [Benchmark(Description = "Conditional Result creation - invalid")]
    public Result<string> ConditionalInvalid()
    {
        if (string.IsNullOrEmpty(_invalidInput))
            return Result<string>.Failure("Input cannot be empty");
        
        return Result<string>.Success(_invalidInput);
    }

    [Benchmark(Description = "Result - Complex chain (5 operations)")]
    public Result<string> ComplexResultChain()
    {
        return Result<string>.Success(_validInput)
            .Map(x => x.Trim())
            .Map(x => x.ToUpperInvariant())
            .Bind(x => x.Length > 3 
                ? Result<string>.Success(x) 
                : Result<string>.Failure("Too short"))
            .Map(x => $"{x}_processed")
            .Map(x => x.Replace('_', '-'));
    }

    [Benchmark(Description = "Exception vs Result - try/catch approach")]
    public string TryCatchApproach()
    {
        try
        {
            return _validInput.ToUpperInvariant();
        }
        catch
        {
            return "Error";
        }
    }

    [Benchmark(Description = "Result - Error propagation chain")]
    public Result<int> ErrorPropagation()
    {
        return Result<string>.Success(_invalidInput)
            .Bind(x => string.IsNullOrEmpty(x)
                ? Result<string>.Failure("Empty")
                : Result<string>.Success(x))
            .Map(x => x.Length);
    }
}
