namespace Daedalus.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
        BenchmarkRunner.Run<StringValidationBenchmarks>();
        BenchmarkRunner.Run<AllocationBenchmarks>();
        BenchmarkRunner.Run<CommandHandlerBenchmarks>();
        BenchmarkRunner.Run<QueryHandlerBenchmarks>();
        BenchmarkRunner.Run<RailwayOrientedProgrammingBenchmarks>();
        BenchmarkRunner.Run<LlmResponseBenchmarks>();
        BenchmarkRunner.Run<DtoMappingBenchmarks>();
        BenchmarkRunner.Run<JsonSerializationBenchmarks>();
        BenchmarkRunner.Run<DomainEntityBenchmarks>();
        BenchmarkRunner.Run<PromptBuildingBenchmarks>();
        BenchmarkRunner.Run<DependencyResolutionBenchmarks>();
        BenchmarkRunner.Run<ResponseExtractionBenchmarks>();
    }
}
