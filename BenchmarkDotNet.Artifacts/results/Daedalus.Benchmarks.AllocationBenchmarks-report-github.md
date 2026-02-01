```

BenchmarkDotNet v0.15.0, Windows 11 (10.0.26200.7623) (Hyper-V)
Unknown processor
.NET SDK 10.0.102
  [Host]     : .NET 10.0.2 (10.0.225.61305), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.2 (10.0.225.61305), X64 RyuJIT AVX2


```
| Method                                     | Mean        | Error      | StdDev     | Median      | Rank | Gen0   | Gen1   | Allocated |
|------------------------------------------- |------------:|-----------:|-----------:|------------:|-----:|-------:|-------:|----------:|
| &#39;Standard LINQ: Tuple list Select&#39;         |   177.96 ns |   3.615 ns |   6.427 ns |   176.54 ns |    2 | 0.0744 |      - |     936 B |
| &#39;String array iteration - foreach&#39;         |    41.34 ns |   0.871 ns |   2.120 ns |    40.53 ns |    1 |      - |      - |         - |
| &#39;Standard LINQ: String array Where&#39;        |   710.05 ns |  13.982 ns |  17.683 ns |   712.17 ns |    4 | 0.0715 |      - |     904 B |
| &#39;StringBuilder: Building 100 items&#39;        |   609.31 ns |  12.187 ns |  21.023 ns |   606.27 ns |    3 | 0.3090 | 0.0019 |    3880 B |
| &#39;String concatenation: Building 100 items&#39; | 5,626.66 ns | 106.896 ns | 131.278 ns | 5,629.55 ns |    5 | 6.8207 | 0.0229 |   85624 B |
