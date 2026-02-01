```

BenchmarkDotNet v0.15.0, Windows 11 (10.0.26200.7623) (Hyper-V)
Unknown processor
.NET SDK 10.0.102
  [Host]     : .NET 10.0.2 (10.0.225.61305), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.2 (10.0.225.61305), X64 RyuJIT AVX2


```
| Method                                            | Mean       | Error     | StdDev    | Median     | Rank | Gen0   | Allocated |
|-------------------------------------------------- |-----------:|----------:|----------:|-----------:|-----:|-------:|----------:|
| &#39;Validate and trim - Valid prompt&#39;                |  0.6117 ns | 0.0815 ns | 0.1001 ns |  0.6212 ns |    2 |      - |         - |
| &#39;Validate and trim - Requires trimming&#39;           | 12.9975 ns | 0.2998 ns | 0.6889 ns | 12.8598 ns |    6 | 0.0044 |      56 B |
| &#39;Validate and trim - Whitespace only&#39;             |  6.2741 ns | 0.1923 ns | 0.2567 ns |  6.1629 ns |    5 |      - |         - |
| &#39;Standard String.IsNullOrWhiteSpace check&#39;        |  0.0000 ns | 0.0000 ns | 0.0000 ns |  0.0000 ns |    1 |      - |         - |
| &#39;Multiple validation checks - Command validation&#39; |  1.9254 ns | 0.0759 ns | 0.1159 ns |  1.8901 ns |    3 |      - |         - |
| &#39;ContainsTarget - Substring search&#39;               |  6.4871 ns | 0.1104 ns | 0.1751 ns |  6.4316 ns |    5 |      - |         - |
| &#39;Standard String.Contains&#39;                        |  4.4131 ns | 0.1211 ns | 0.1189 ns |  4.3885 ns |    4 |      - |         - |
| &#39;CountOccurrences - Character counting&#39;           | 20.0458 ns | 0.4345 ns | 0.7609 ns | 19.6679 ns |    7 |      - |         - |
| &#39;Standard LINQ Count&#39;                             | 23.6307 ns | 0.3453 ns | 0.2883 ns | 23.5627 ns |    8 |      - |         - |
