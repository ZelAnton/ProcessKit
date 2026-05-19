# Changelog

All notable changes to **ProcessKit** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
-

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

[Unreleased]: https://github.com/ZelAnton/ProcessKit/compare/v1.1.1...HEAD
[1.1.1]: https://github.com/ZelAnton/ProcessKit/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/ZelAnton/ProcessKit/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/ZelAnton/ProcessKit/releases/tag/v1.0.0
