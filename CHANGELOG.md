# Changelog

All notable changes to **ProcessKit** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `ProcessGroupOptions` (`ShutdownTimeout`, `EscalateToKill`) and a `ProcessGroup(ProcessGroupOptions)` constructor to tune the Unix `SIGTERM` grace window and force-kill escalation. Ignored on Windows, where the Job Object terminates members atomically.
- `ProcessRunOptions.ProcessGroupOptions` (configures the private group the runner creates) and `ProcessRunOptions.PumpTeardownTimeout` (bounds how long handle disposal waits for the output pumps).
- `ProcessRunner(ProcessRunOptions defaults)` constructor: baseline options applied to every call, with per-call options overriding field-by-field (the environment is unioned, per-call key winning).
- Interactive standard input: `ProcessRunOptions.KeepStandardInputOpen` keeps stdin open after start and exposes `IRunningProcess.StandardInput` (`IProcessStandardInput`: `WriteAsync`/`WriteLineAsync`/`FlushAsync`/`CompleteAsync`) for write-then-read (REPL) processes. The bulk helpers force it off.
- A BenchmarkDotNet project (`benchmarks/ProcessKit.Benchmarks`) measuring process start/exit overhead, bulk-capture shapes, streaming throughput, and per-line pump cost. Not part of the package or CI.

### Changed
- `ProcessRunOptions.WorkingDirectory` and `ProcessRunOptions.Environment` now apply to every overload (and are inherited from `ProcessRunner` defaults), taking precedence over the supplied `ProcessStartInfo`; previously they affected only the convenience `Start(executable, arguments, …)` overloads.

### Fixed
-

## [1.3.0] - 2026-05-31

### Added
- `ProcessRunOptions.OutputBuffer` (`OutputBufferPolicy` with `OutputOverflowMode`) caps how many unconsumed stdout/stderr lines are buffered, with a non-blocking drop-oldest/drop-newest policy — closing the OOM risk on chatty processes whose output is never consumed.
- `IRunningProcess.ToResultAsync(...)` extension drains an already-started handle into a `ProcessResult<string>`.
- `IRunningProcess.CompletionOrThrowAsync(...)` extension awaits the exit code but throws `TimeoutException` when the process was killed by `ProcessRunOptions.Timeout`.
- `ProcessRunOptions.WorkingDirectory` and `ProcessRunOptions.Environment` set the working directory and environment for the convenience `Start(executable, arguments, …)` overloads.

### Changed
- `GetExitCodeAsync` now discards stdout/stderr by default (memory-flat) instead of buffering it unboundedly, since it never exposes the output. Supply an `OutputBuffer` policy to override.
- `GetBytesOutputAsync` now throws `OperationCanceledException` when the caller's `CancellationToken` is cancelled, consistent with `GetFullOutputAsync`; a `Timeout` still returns a partial result with `WasTimedOut` set.

### Fixed
- Standard input is now always closed at start when no `StandardInput` is supplied, matching the documented contract. Previously the child inherited the parent's stdin, so a process that reads stdin (e.g. `cat`) could block forever when run with no input.
- The defensive `ProcessStartInfo` clone now mirrors the caller's environment exactly. Previously an environment variable removed from `ProcessStartInfo.Environment` could reappear in the started process, because the clone was seeded with the current process environment and never cleared.
- A user-supplied stdin source (`FromStream`/`FromLines`/`FromEnumerable`) that throws no longer lets the exception escape from `IRunningProcess.DisposeAsync`; the failure is contained and the child receives whatever was written before it, then EOF.
- `IRunningProcess.CpuTime` and `PeakMemoryBytes` now refresh the underlying counters before sampling, so repeated live reads report current values instead of a stale first-read snapshot.

## [1.2.0] - 2026-05-19

### Added
- `IProcessRunner` interface and `ProcessRunner` default implementation for executing external commands with full lifetime management via `ProcessGroup`.
- `ProcessRunner.Default` static singleton for casual use without DI.
- `IRunningProcess` handle exposing `StdOut`/`StdErr` as `IAsyncEnumerable<string>`, line counters, `Pid`, `StartTime`, `Duration`, `CpuTime`, `PeakMemoryBytes`, `WasTimedOut`, `Exited` cancellation token, and `Completion` task.
- `ProcessResult<T>` record carrying `StdOut`, `StdErr`, `ExitCode`, `WasTimedOut`, `IsSuccess`, and fluent `EnsureSuccess()`.
- `ProcessExitException` raised by `EnsureSuccess()` on non-zero exit, carrying `ExitCode` and the captured `StdErr`.
- `ProcessRunOptions` (record) for stdin, stderr/stdout handlers, shared `ProcessGroup`, timeout, and encoding overrides.
- `StandardInput` closed union with factories `Empty`, `FromString`, `FromBytes`, `FromStream`, `FromLines` (async), `FromEnumerable` (sync), and `FromFile` (eagerly validated path).
- Extension methods on `IProcessRunner`: `Start(exe, args)` convenience overload, `GetOutputAsync`, `GetFirstLineOutputAsync`, `GetFullOutputAsync`, `GetBytesOutputAsync`, `GetExitCodeAsync`, sync `GetOutput`/`GetFirstLineOutput`, and `Task<ProcessResult<T>>.EnsureSuccessAsync()`.

### Changed
- README slimmed for the NuGet package page: contributor-only "Running tests on Linux from Windows" guide moved to `docs/linux-testing.md`.
- README intro and NuGet package description rewritten to reflect both surfaces (`ProcessGroup` lifetime layer + `ProcessRunner` async-first command runner).

### Fixed
-

## [1.1.1] - 2026-05-18

### Changed

- Use latest git tag (or 0.0.0) as previous version baseline


### Removed

- Remove self-signed NuGet author signing

## [1.1.0] - 2026-05-18

### Changed

- Initial commit
- Fixed initial workflow
- Updated certificate


### Fixed

- Fix release workflow when previous tag does not exist

## [1.0.0] - 2026-05-18

### Changed

- Rename package to `ProcessKit`, namespace to `ProcessKit`; publish to NuGet.org under MIT licence

[Unreleased]: https://github.com/ZelAnton/ProcessKit/compare/v1.3.0...HEAD
[1.3.0]: https://github.com/ZelAnton/ProcessKit/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/ZelAnton/ProcessKit/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/ZelAnton/ProcessKit/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/ZelAnton/ProcessKit/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/ZelAnton/ProcessKit/releases/tag/v1.0.0
