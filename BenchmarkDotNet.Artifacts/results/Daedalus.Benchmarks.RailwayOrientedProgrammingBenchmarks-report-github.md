```

BenchmarkDotNet v0.15.0, Windows 11 (10.0.26200.7623) (Hyper-V)
Unknown processor
.NET SDK 10.0.102
  [Host]     : .NET 10.0.2 (10.0.225.61305), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.2 (10.0.225.61305), X64 RyuJIT AVX2


```
| Method                                     | Mean       | Error     | StdDev    | Median     | Rank | Gen0   | Allocated |
|------------------------------------------- |-----------:|----------:|----------:|-----------:|-----:|-------:|----------:|
| &#39;Result.Success creation&#39;                  |  1.5305 ns | 0.0682 ns | 0.1101 ns |  1.4907 ns |    5 |      - |         - |
| &#39;Result.Failure creation&#39;                  |  0.2726 ns | 0.0327 ns | 0.0306 ns |  0.2714 ns |    4 |      - |         - |
| &#39;Result.Map operation&#39;                     |  1.7895 ns | 0.0735 ns | 0.0902 ns |  1.7885 ns |    6 |      - |         - |
| &#39;Result.Bind operation&#39;                    |  5.1421 ns | 0.1345 ns | 0.1651 ns |  5.0835 ns |    8 |      - |         - |
| &#39;Result chain - 3 operations&#39;              | 32.9122 ns | 0.6879 ns | 1.6481 ns | 32.3532 ns |   11 | 0.0147 |     184 B |
| &#39;Result - IsSuccess check&#39;                 |  0.0518 ns | 0.0324 ns | 0.0532 ns |  0.0359 ns |    2 |      - |         - |
| &#39;Result - IsFailure check&#39;                 |  0.0072 ns | 0.0079 ns | 0.0178 ns |  0.0000 ns |    1 |      - |         - |
| &#39;Result - Match success case&#39;              | 12.1286 ns | 0.4538 ns | 1.3092 ns | 11.6760 ns |   10 | 0.0045 |      56 B |
| &#39;Result - Match failure case&#39;              |  7.5592 ns | 0.2202 ns | 0.5402 ns |  7.4276 ns |    9 | 0.0051 |      64 B |
| &#39;Conditional Result creation - valid&#39;      |  1.5519 ns | 0.0574 ns | 0.0824 ns |  1.5381 ns |    5 |      - |         - |
| &#39;Conditional Result creation - invalid&#39;    |  0.0791 ns | 0.0376 ns | 0.0462 ns |  0.0755 ns |    3 |      - |         - |
| &#39;Result - Complex chain (5 operations)&#39;    | 40.4924 ns | 0.8241 ns | 1.6458 ns | 40.2299 ns |   12 | 0.0159 |     200 B |
| &#39;Exception vs Result - try/catch approach&#39; | 11.7305 ns | 0.3075 ns | 0.5466 ns | 11.5977 ns |   10 | 0.0044 |      56 B |
| &#39;Result - Error propagation chain&#39;         |  2.6989 ns | 0.0928 ns | 0.1069 ns |  2.6911 ns |    7 |      - |         - |
