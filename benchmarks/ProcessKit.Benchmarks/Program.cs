using System.Reflection;
using BenchmarkDotNet.Running;

// Build the library in Release first (the project references ProcessKit via <Reference>, not
// ProjectReference, so a solution build is what compiles the dependency), then run --no-build:
//   dotnet build ProcessKit.slnx -c Release
//   dotnet run -c Release --project benchmarks/ProcessKit.Benchmarks --no-build -- --filter '*'
// Filter a subset, e.g.:  -- --filter '*ProcessStartExit*'
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
