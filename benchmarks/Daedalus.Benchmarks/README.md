# Daedalus Performance Benchmarks

This project contains comprehensive performance benchmarks for the Daedalus codebase using **BenchmarkDotNet**. Use these benchmarks to measure performance improvements across versions and identify optimization opportunities.

## Running Benchmarks

### Run all benchmarks

```bash
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release
```

### Run specific benchmark class

```bash
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release --filter "LinqBenchmarks"
```

### Run specific benchmark method

```bash
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release --filter "LinqBenchmarks.ZLinqArray"
```

## Benchmark Suites

### 1. **StringValidationBenchmarks** - String Processing Optimization

Measures performance of `PerformanceOptimizations` utility methods:

- `ValidateAndTrimString()` - Zero-allocation validation with smart trimming
- `ContainsTarget()` - Span-based substring search
- `CountOccurrences()` - Zero-allocation character counting
- Standard comparisons with built-in string methods

**Key Metric**: Zero allocations on already-trimmed strings, reduced allocations on trimming

### 2. **AllocationBenchmarks** - Memory Efficiency

Measures heap allocations across common patterns:

- LINQ Select on tuple lists
- String array filtering
- StringBuilder vs string concatenation
- Iteration patterns

**Key Metric**: Allocations and bytes allocated - higher = worse performance

### 3. **CommandHandlerBenchmarks** - Write Operation Hotpaths

Benchmarks critical command handler operations:

- String validation and trimming (used in CreateTaskCommand)
- Multiple validation checks in sequence
- Substring searching with ContainsTarget vs String.Contains
- Character counting operations

**Key Metric**: Latency and allocations - command handlers execute on every write

### 4. **QueryHandlerBenchmarks** - Read Operation Hotpaths

Benchmarks critical query handler operations:

- Pagination with Skip/Take on large datasets (1000 tasks)
- DTO mapping and projection
- LINQ filter + map operations
- Distinct, OrderBy, GroupBy operations
- Collection lookup patterns

**Key Metric**: Latency and allocations - queries execute on every read request

### 5. **RailwayOrientedProgrammingBenchmarks** - Result<T> Operations

Benchmarks Railway-Oriented Programming patterns using CSharpFunctionalExtensions:

- Result.Success/Failure creation
- Result.Map operations
- Result.Bind chaining
- Complex chains (3-5 operations)
- Result.Match success/failure cases
- Conditional Result creation
- Error propagation chains
- Comparison with try/catch exception handling

**Key Metric**: Creation speed and chaining overhead - used extensively in all handlers

### 6. **DtoMappingBenchmarks** - DTO Mapping Overhead

Benchmarks DTO mapping strategies:

- Direct mapping from domain entities to DTOs
- ZLinq vs standard LINQ for collection mapping
- Projection patterns for nested entities

**Key Metric**: Mapping latency and allocations per entity

### 7. **JsonSerializationBenchmarks** - JSON Serialization Strategies

Benchmarks JSON serialization approaches:

- System.Text.Json source-generated vs reflection-based
- DTO round-trip serialization
- Large collection serialization

**Key Metric**: Serialization throughput and allocation pressure

### 8. **LlmResponseBenchmarks** - LLM Response Processing

Benchmarks completion promise detection in LLM responses:

- Span-based vs string-based Contains/IndexOf
- ReadOnlySpan character searches
- Large response body scanning

**Key Metric**: Detection latency on variable-length LLM outputs

### 9. **DomainEntityBenchmarks** - Entity Lifecycle Hotpaths

Benchmarks domain entity operations called every Ralph loop iteration:

- `Task.Create()` with pre-trimmed vs untrimmed strings vs validation failure
- `Task.RecordExecution()` - incomplete and completion state transitions
- `Task.AddDependency()` - linear Contains scan on growing lists (50 items)
- `Task.AddFileToModify()` and `Task.UpdateMetadata()` - Trim allocations
- `Project.Create()` and `Project.AddTask()` - Exists search on 20 tasks
- `ExecutionSession.Create()` and `IsStale()` checks

**Key Metric**: Allocations per iteration — these run on every Ralph loop cycle

### 10. **PromptBuildingBenchmarks** - Prompt Pipeline (Hottest Path)

Benchmarks the single highest-allocation codepath — prompt construction:

- `GetDefaultSections()` minimal/full/with workspace context
- `BuildPromptAsync()` end-to-end prompt generation
- `PromptSection.Create()` factory — single and bulk (20 sections)
- ZLinq vs standard LINQ for filter+sort on 20/50 PromptSection collections
- History `TakeLast(5).ToList()` on 3 vs 20 entries
- History multi-pass `Count(predicate)` vs single-pass manual loop
- `Response.Substring` snippet vs `Span<char>` slicing

**Key Metric**: Allocations and latency — runs every Ralph loop iteration to build the LLM prompt

### 11. **DependencyResolutionBenchmarks** - Phase Orchestrator Graphs

Benchmarks dependency resolution in `PhaseOrchestrator.OnTaskCompletedAsync`:

- Ready-task resolution on 10/50/200 task graphs (ZLinq vs standard LINQ)
- `Dictionary<string, Status>` construction on 50/200 tasks
- `Dependencies.Contains()` with `StringComparer.Ordinal`

**Key Metric**: Scaling behavior — O(n²) dependency check becomes significant at 200+ tasks

### 12. **ResponseExtractionBenchmarks** - LLM Response Extraction & Injection

Benchmarks text extraction and prompt manipulation patterns:

- `ExtractTextContent` via StringBuilder vs `string.Join` vs `string.Concat` (3/10/25 blocks)
- `String.Insert` into 1KB/10KB/50KB prompts
- `StringBuilder.Insert` alternative for large prompts
- `IndexOf` marker scans at different prompt sizes

**Key Metric**: Allocation pressure on large prompts — 50KB prompt insertion is particularly expensive

## Running Specific Benchmarks

### Run all benchmarks

```bash
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release
```

### Run specific benchmark class

```bash
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release --filter "CommandHandlerBenchmarks"
```

### Run specific benchmark method

```bash
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release --filter "*ValidateAndTrimValid*"
```

### Run benchmarks by name pattern

```bash


- LINQ Select on tuple lists
- String array filtering
- StringBuilder vs string concatenation
- Iteration patterns

**Key Metric**: Allocations and bytes allocated - higher = worse performance

## Understanding Results

BenchmarkDotNet outputs results in a table format:

```
|                       Method |       Mean |    StdDev | Ratio | RatioSD |     Gen0 |     Gen1 |     Gen2 | Allocated | Rank |
|----------------------------- |-----------:|----------:|------:|--------:|---------:|---------:|---------:|----------:|-----:|
| StandardLinqArray            |   2.571 us | 0.0287 us |  1.00 |    0.03 |   0.3052 |        - |        - |   2.49 KB |    2 |
| ZLinqArray                   |   2.383 us | 0.0141 us |  0.93 |    0.01 |   0.2365 |        - |        - |   1.93 KB |    1 |
```

### Key Columns:

- **Mean**: Average execution time (us = microseconds)
- **Gen0/Gen1/Gen2**: Garbage collection pressure by generation
- **Allocated**: Total bytes allocated during benchmark run
- **Ratio**: Relative to the first benchmark (1.00 = baseline)
- **Rank**: 1 = best, higher = worse

## Performance Goals

### ZLinq Goals

- ✅ Eliminate `IEnumerator<T>` allocations on arrays/lists
- ✅ Same performance as manual loops
- ✅ Maintain code readability with LINQ operators

### String Validation Goals

- ✅ Zero allocations when string already trimmed
- ✅ Single allocation pass for untrimmed strings
- ✅ Avoid double-trim allocations

### Overall Goals

- ✅ Minimal Gen0 allocations in hot paths
- ✅ No Gen1/Gen2 allocations in request handling
- ✅ Predictable, consistent performance

## Integration with CI/CD

To integrate benchmarks into your CI/CD pipeline:

```bash
# Run benchmarks and export results
dotnet run -p benchmarks/Daedalus.Benchmarks -c Release --exportjson results.json

# Compare with baseline
# See: https://benchmarkdotnet.org/articles/features/baselines.html
```

## Adding New Benchmarks

1. Create a new class inheriting from benchmarks
2. Mark with `[MemoryDiagnoser]` to track allocations
3. Add `[GlobalSetup]` for initialization
4. Mark test methods with `[Benchmark]`
5. Run and compare against previous results

Example:

```csharp
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, targetCount: 5)]
public class MyBenchmarks
{
    [GlobalSetup]
    public void Setup() { }

    [Benchmark]
    public void MyOperation() { }
}
```

## Resources

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [ZLinq GitHub](https://github.com/cysharp/zlinq)
- [PerformanceOptimizations.cs](../../src/Daedalus.Application/Services/PerformanceOptimizations.cs)
