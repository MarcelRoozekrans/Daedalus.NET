```

BenchmarkDotNet v0.15.0, Windows 11 (10.0.26200.7623) (Hyper-V)
Unknown processor
.NET SDK 10.0.102
  [Host]     : .NET 10.0.2 (10.0.225.61305), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.2 (10.0.225.61305), X64 RyuJIT AVX2


```
| Method                                                   | Mean           | Error       | StdDev      | Median         | Rank | Gen0   | Gen1   | Allocated |
|--------------------------------------------------------- |---------------:|------------:|------------:|---------------:|-----:|-------:|-------:|----------:|
| &#39;Pagination - Skip/Take on 1000 tasks (page 5, size 20)&#39; |     27.2257 ns |   0.6271 ns |   1.3764 ns |     26.9287 ns |    4 | 0.0185 |      - |     232 B |
| &#39;Pagination - Alternative: Direct LINQ Select&#39;           |     10.8416 ns |   0.2795 ns |   0.5111 ns |     10.7245 ns |    3 | 0.0108 |      - |     136 B |
| &#39;DTO Mapping - Project to tuple list&#39;                    | 22,439.9098 ns | 423.1760 ns | 593.2336 ns | 22,264.1357 ns |   12 | 4.4556 | 0.7324 |   56136 B |
| &#39;Filter + Map - Where + Select&#39;                          |  8,109.9736 ns | 202.9622 ns | 565.7775 ns |  7,938.1927 ns |    9 | 2.2430 | 0.0610 |   28240 B |
| &#39;Count before pagination&#39;                                |      0.0047 ns |   0.0115 ns |   0.0102 ns |      0.0000 ns |    1 |      - |      - |         - |
| &#39;Manual iteration - counting matches&#39;                    |    598.1621 ns |  11.3682 ns |  11.6743 ns |    595.9749 ns |    6 |      - |      - |         - |
| &#39;Standard LINQ Count with filter&#39;                        |    589.7442 ns |   7.8606 ns |   6.9682 ns |    588.9369 ns |    6 |      - |      - |         - |
| &#39;First or default - finding first match&#39;                 |    137.2069 ns |   2.7251 ns |   2.6765 ns |    137.4233 ns |    5 |      - |      - |         - |
| &#39;Multiple projections - nested Select&#39;                   | 20,711.2230 ns | 409.1928 ns | 835.8726 ns | 20,694.0918 ns |   11 | 7.6904 | 1.2512 |   96488 B |
| &#39;Distinct operation on 1000 tasks&#39;                       | 12,618.6834 ns | 249.4676 ns | 498.2137 ns | 12,538.3850 ns |   10 | 5.0049 | 0.6256 |   62928 B |
| &#39;Order by operation&#39;                                     |  4,803.2305 ns |  94.9154 ns | 158.5824 ns |  4,768.8202 ns |    7 | 0.6714 |      - |    8480 B |
| &#39;Group by operation&#39;                                     |  7,194.7254 ns | 143.3579 ns | 262.1381 ns |  7,198.7865 ns |    8 | 1.4801 | 0.0458 |   18656 B |
| &#39;Contains check - list membership&#39;                       |     28.7676 ns |   0.5650 ns |   0.7921 ns |     28.7508 ns |    4 |      - |      - |         - |
| &#39;Binary search - array lookup&#39;                           |      2.0839 ns |   0.0695 ns |   0.0580 ns |      2.0900 ns |    2 |      - |      - |         - |
