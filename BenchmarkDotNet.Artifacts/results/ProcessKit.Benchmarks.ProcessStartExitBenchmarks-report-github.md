```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26200.8457)
12th Gen Intel Core i9-12900H 2.50GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.300
  [Host] : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2
  Dry    : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                 | Mean     | Error | Ratio | Allocated | Alloc Ratio |
|----------------------- |---------:|------:|------:|----------:|------------:|
| RawProcess             | 38.73 ms |    NA |  1.00 |   1.91 KB |        1.00 |
| ProcessKit_GetExitCode | 48.91 ms |    NA |  1.26 | 145.91 KB |       76.23 |
