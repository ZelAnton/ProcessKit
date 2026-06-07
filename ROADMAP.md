# ProcessKit Roadmap

ProcessKit (C#) is the .NET counterpart of [ProcessKit-rs](https://github.com/ZelAnton/ProcessKit-rs), a Rust crate for cross-platform child-process management. The Rust version (v0.7) currently exposes a wider surface — supervised restarts, shell-free pipelines, readiness probes, Linux cgroup v2 backend, whole-tree resource limits, scripted/recording/record-replay test doubles, structured tracing. The C# library (v1.3.2) covers the foundation: a kill-on-drop `ProcessGroup` (Windows Job Object / POSIX pgroup), an `IProcessRunner` seam, an `IRunningProcess` handle, `ProcessResult<T>` with `EnsureSuccess`, closed-union `StandardInput`, bounded output buffering, bulk run-and-capture helpers, and interactive stdin.

This roadmap closes the gap **without copying Rust idioms verbatim** — each feature is re-thought against .NET conventions (`Activity` / `EventSource` instead of `tracing::span`, `IAsyncEnumerable<T>` instead of `Stream<Item>`, `Func<>`/`Action<>` instead of `Box<dyn Fn>`). Extensibility seams are introduced only where there is a real test-, mock-, or observability-driven need; we deliberately avoid speculative hook surfaces.

## Target architecture (5 packages)

```
ProcessKit                       core: groups, runner, command, signals, probes,
                                       pipelines, retry, stats sampler, profile,
                                       Activity + EventSource instrumentation
ProcessKit.Testing               ScriptedProcessRunner, RecordingProcessRunner,
                                 RecordReplayProcessRunner (JSON cassettes), FakeClock
ProcessKit.Supervisor            ProcessSupervisor — restart policy, backoff, stop-when
ProcessKit.Limits                ResourceLimits — Windows Job extended limits + Linux cgroup v2
ProcessKit.Extensions.Logging    Optional ILogger<T> bridge over the EventSource (deferred)
```

All packages share a synchronized version. Dependencies fan out from the core; the Testing / Supervisor / Limits packages are opt-in and stay AOT-friendly.

## Rust → C# feature mapping

| ProcessKit-rs (v0.7)              | ProcessKit (target)                                 | Phase |
|-----------------------------------|-----------------------------------------------------|-------|
| `ProcessGroup` (Job / pgroup)     | `ProcessGroup` (Job / pgroup)                       | done  |
| `JobRunner` / `ProcessRunner`     | `ProcessRunner` / `IProcessRunner`                  | done  |
| `Stdin::*`                        | `StandardInput.*` closed union                      | done  |
| `ProcessResult<T>`                | `ProcessResult<T>`                                  | done  |
| `OutputBufferPolicy`              | `OutputBufferPolicy`                                | done  |
| `tracing` events                  | `ActivitySource("ProcessKit")` + `EventSource`      | 1     |
| `Signal` enum, `signal/suspend/…` | `Signal` enum + `ProcessGroup.SignalAsync` family   | 2     |
| `Mechanism` enum                  | `Mechanism` enum + `ProcessGroup.Mechanism`         | 2     |
| `wait_for_line/wait_for_port`     | `IRunningProcess.WaitForLineAsync/WaitForPortAsync` | 3     |
| `Command` builder                 | `Command` fluent builder                            | 4     |
| `wait_any`                        | `IEnumerable<IRunningProcess>.WaitAnyAsync`         | 4     |
| `cancel_on` + `Error::Cancelled`  | `Command.WithCancellation` + `ProcessCancelledException` | 4 |
| `Command::pipe(...)`              | `ProcessPipeline`                                   | 5     |
| `Command::retry(...)`             | `RetryPolicy` + `Command.WithRetry`                 | 6     |
| Linux cgroup v2 backend           | `LinuxCgroupV2 : IProcessGroupImpl`                 | 7     |
| `ResourceLimits`                  | `ResourceLimits` (ProcessKit.Limits)                | 8     |
| `ScriptedRunner` / `RecordingRunner` | `ScriptedProcessRunner` / `RecordingProcessRunner` | 9   |
| `RecordReplayRunner`              | `RecordReplayProcessRunner` (System.Text.Json)      | 9     |
| `Supervisor`                      | `ProcessSupervisor` (ProcessKit.Supervisor)         | 10    |
| `StatsSampler` / `RunProfile`     | `ProcessGroup.SampleStatsAsync` / `RunProfile`      | 11    |
| Unix `uid`/`gid`/`setsid`         | `Command.WithUserId/WithGroupId/WithSetsid`         | 12    |
| `cli_client!` macro               | not ported (use hand-written wrappers)              | —     |
| `MockRunner` (mockall)            | not ported (use Moq/NSubstitute on `IProcessRunner`) | —    |
| `#[non_exhaustive]`               | not applicable (use semver)                          | —    |

## Phases

Each phase is a self-contained minor release with CI green on Windows/Linux/macOS, an AOT smoke that exercises the new API, and a CHANGELOG entry. There are no current consumers, so renames and signature changes are fair game.

### Phase 1 — v1.4.0 · Observability foundation
Standard .NET diagnostics so consumers can plug in OpenTelemetry / dotnet-trace / PerfView without code changes.

- `ActivitySource("ProcessKit", "1.4.0")` with spans `processkit.process.run`, `processkit.group.shutdown`.
- `EventSource("ProcessKit")` with `ProcessStarted`, `ProcessExited`, `GroupShutdown`. (A line-level event was scoped out — its high-volume verbose-channel value did not justify threading the pid + stream identifiers through every line-pump path. Revisit in a later phase if subscribers need it.)
- Tags carry program name, pid, exit code, timed-out flag, mechanism, duration. `argv` and environment are deliberately omitted to avoid leaking secrets.

Acceptance: an `ActivityListener` test captures the span with the expected tags; AOT smoke still emits events after publish.

### Phase 2 — v1.5.0 · Signals, Suspend, Members, Adopt, Mechanism
Extend `ProcessGroup` with the operations Rust exposes under the `process-control` feature flag.

```csharp
public enum Signal { Term, Kill, Int, Hup, Quit, Usr1, Usr2 }
public readonly record struct CustomSignal(int Number);     // Unix only
public enum Mechanism { JobObject, ProcessGroup, CgroupV2, None }

public sealed class ProcessGroup
{
    public Mechanism Mechanism { get; }
    public Task SignalAsync(Signal signal, CancellationToken ct = default);
    public Task SignalAsync(CustomSignal signal, CancellationToken ct = default);
    public Task SuspendAsync(CancellationToken ct = default);
    public Task ResumeAsync(CancellationToken ct = default);
    public Task<IReadOnlyList<int>> GetMembersAsync(CancellationToken ct = default);
    public Task AdoptAsync(Process externalProcess, CancellationToken ct = default);
}
```

Windows uses `TerminateJobObject` for `Signal.Kill`, per-thread `SuspendThread`/`ResumeThread` via a `Toolhelp32` snapshot for suspend/resume (per-thread suspend counts stack — matches Rust v0.7.1, documented API), `JobObjectBasicProcessIdList` for members, `AssignProcessToJobObject` for adopt. Non-Kill signals on Windows throw `PlatformNotSupportedException`. Unix uses `killpg`, `SIGSTOP/SIGCONT`, and a snapshot of tracked processes for `GetMembersAsync` (Phase 7 will swap the Linux side over to `cgroup.procs`). All P/Invoke goes through `[LibraryImport]`.

### Phase 3 — v1.6.0 · Readiness probes
Wait for a child to become ready without killing it on timeout.

```csharp
Task<string> WaitForLineAsync(Predicate<string> match, TimeSpan within, CancellationToken ct = default);
Task WaitForAsync(Func<CancellationToken, Task<bool>> check, TimeSpan within, TimeSpan poll = default, CancellationToken ct = default);
Task WaitForPortAsync(IPEndPoint endpoint, TimeSpan within, CancellationToken ct = default);
```

`ProcessNotReadyException` is distinct from a process timeout — probes leave the child running so callers can decide whether to kill or wait further. `WaitForLineAsync` tees into the existing line sink so it coexists with `await foreach (var line in p.StdOut)`.

### Phase 4 — v1.7.0 · `Command` builder, `WaitAnyAsync`, cancellation distinction
A fluent builder that desugars to `(ProcessStartInfo, ProcessRunOptions)`. Existing `IProcessRunner.Start(...)` overloads stay; `Command` is sugar, not a replacement.

```csharp
public sealed class Command
{
    public static Command Create(string program);
    public Command Args(params string[] args);
    public Command WithWorkingDirectory(string path);
    public Command WithEnvironment(string key, string? value);
    public Command WithTimeout(TimeSpan timeout);
    public Command WithCancellation(CancellationToken token);
    public Command WithStandardInput(StandardInput input);
    public Command OnStandardOutputLine(Action<string> handler);
    public Command OnStandardErrorLine(Action<string> handler);
    public Command WithOutputBuffer(OutputBufferPolicy policy);
    public Command WithEncoding(Encoding stdout, Encoding stderr);
    public Command InheritEnvironment(params string[] varNames);

    public Task<IRunningProcess> StartAsync(IProcessRunner? runner = null);
    public Task<ProcessResult<string>> OutputStringAsync(IProcessRunner? runner = null);
    public Task<ProcessResult<byte[]>> OutputBytesAsync(IProcessRunner? runner = null);
    public Task<int> ExitCodeAsync(IProcessRunner? runner = null);
    public Task<bool> ProbeAsync(IProcessRunner? runner = null);
    public Task<string> RunAsync(IProcessRunner? runner = null);
    public Task<string?> FirstLineAsync(Predicate<string>? match = null, IProcessRunner? runner = null);
}

Task<(int Index, int? ExitCode)> WaitAnyAsync(this IEnumerable<IRunningProcess> processes, CancellationToken ct = default);
```

Cancellation gets its own semantics: `WithCancellation(token)` always raises `ProcessCancelledException` (terminal), whereas a process timeout is captured in `ProcessResult.WasTimedOut`. A `WasCancelled` flag is added to `ProcessResult` for the non-throw bulk paths.

### Phase 5 — v1.8.0 · Pipelines
Shell-free `a | b | c` with a shared kill-on-drop group and pipefail attribution.

```csharp
public sealed class ProcessPipeline
{
    public ProcessPipeline Pipe(Command next);
    public ProcessPipeline WithTimeout(TimeSpan timeout);
    public Task<ProcessResult<string>> OutputStringAsync(IProcessRunner? runner = null);
    public Task<string> RunAsync(IProcessRunner? runner = null);
}

public partial class Command { public ProcessPipeline Pipe(Command next); }
```

Stdout of stage *N* feeds stage *N+1* via `StandardInput.FromStream`. Pipefail: the first failing stage's exit code, stderr, and program name win; otherwise the final stage's values. Timeout cancels the shared group, killing every stage.

### Phase 6 — v1.9.0 · Retry + `IClock` seam
Automatic re-run with an exponential-backoff classifier.

```csharp
public sealed record RetryPolicy(
    int MaxAttempts,
    TimeSpan Backoff,
    double BackoffFactor = 2.0,
    TimeSpan? MaxBackoff = null,
    bool Jitter = true,
    Predicate<Exception>? RetryIf = null);

public partial class Command { public Command WithRetry(RetryPolicy policy); }

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    Task Delay(TimeSpan duration, CancellationToken ct);
}
```

Retry applies only to success-checking verbs (`RunAsync`, `ExitCodeAsync`, `ProbeAsync`) — bulk paths return the result and never throw. Cancellation is terminal (never retried). `IClock` lets the Testing package drive backoff with virtual time.

### Phase 7 — v2.0.0 · Linux cgroup v2 backend
A new internal `LinuxCgroupV2 : IProcessGroupImpl` becomes the default on Linux hosts where cgroup v2 is delegated; otherwise the existing pgroup implementation stays.

`ProcessGroup.SelectImpl` priority becomes Windows → JobObject; Linux with writable controllers → cgroup v2; other Unix → pgroup. Each group lives in an ephemeral subcgroup `/sys/fs/cgroup/processkit/<guid>/`; on disposal `echo 1 > cgroup.kill` followed by `rmdir`. Stats now read `cpu.stat` / `memory.peak`; members read `cgroup.procs`. No new public API.

### Phase 8 — v2.1.0 · ResourceLimits (ProcessKit.Limits)
Whole-tree caps — memory, processes, CPU.

```csharp
public sealed record ResourceLimits(
    long? MaxMemoryBytes = null,
    int? MaxProcesses = null,
    double? CpuQuotaCores = null);

public static class ProcessGroupOptionsLimitsExtensions
{
    public static ProcessGroupOptions WithLimits(this ProcessGroupOptions opts, ResourceLimits limits);
}
```

Windows backs limits with `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` (memory, process count) and `JOBOBJECT_CPU_RATE_CONTROL_INFORMATION` (CPU). Linux cgroup v2 writes `memory.max`, `pids.max`, `cpu.max`. Unsupported platforms (macOS/BSD, Linux without cgroup delegation) throw `PlatformNotSupportedException` at group construction — never a silent under-enforcement.

### Phase 9 — v2.2.0 · Test doubles (ProcessKit.Testing)
Downstream tests exercise their code through `IProcessRunner` without spawning anything.

- `ScriptedProcessRunner` — predicate-driven canned replies with an optional `Else(...)` fallback.
- `RecordingProcessRunner` — wraps an inner runner, captures every `Invocation`.
- `RecordReplayProcessRunner` — JSON cassettes via `System.Text.Json` with `JsonSerializerContext` (AOT-safe). Match key: program + args + cwd + has_stdin (environment variable names are logged for debugging but excluded from matching, and values are never written).
- `Invocation` and `Reply` records.
- `FakeClock` — virtual-time `IClock` for Supervisor / retry tests.

### Phase 10 — v2.3.0 · Supervisor (ProcessKit.Supervisor)
Keep a child alive with restart policy, backoff, jitter, and a stop predicate.

```csharp
public enum RestartPolicy { Always, OnCrash, Never }
public enum StopReason { Predicate, PolicySatisfied, RestartsExhausted }
public sealed record SupervisionOutcome(ProcessResult<string> FinalResult, int Restarts, StopReason Stopped);

public sealed class ProcessSupervisor
{
    public ProcessSupervisor(Command command, IProcessRunner? runner = null);
    public ProcessSupervisor WithRestartPolicy(RestartPolicy policy);
    public ProcessSupervisor WithMaxRestarts(int n);
    public ProcessSupervisor WithBackoff(TimeSpan baseBackoff, double factor = 2.0);
    public ProcessSupervisor WithMaxBackoff(TimeSpan cap);
    public ProcessSupervisor WithJitter(bool enabled = true);
    public ProcessSupervisor StopWhen(Predicate<ProcessResult<string>> predicate);
    public Task<SupervisionOutcome> RunAsync(CancellationToken ct = default);
}
```

The supervisor delegates to `IProcessRunner` and `IClock`, so it tests cleanly against `ScriptedProcessRunner` + `FakeClock` without real processes or real time.

### Phase 11 — v2.4.0 · Stats sampler + `RunProfile`
Time-series group metrics and per-run profile summaries.

```csharp
IAsyncEnumerable<ProcessGroupStats> SampleStatsAsync(this ProcessGroup group, TimeSpan interval, CancellationToken ct = default);
Task<RunProfile> ProfileAsync(this IRunningProcess process, TimeSpan sampleInterval, CancellationToken ct = default);

public sealed record RunProfile(int? ExitCode, TimeSpan Duration, TimeSpan? CpuTime, long? PeakMemoryBytes, int Samples)
{
    public double? AverageCpuCores => ...;
}
```

Both helpers are pure managed code over the existing `GetStats()` / `CpuTime` / `PeakMemoryBytes` surfaces.

### Phase 12 — v2.5.0 · Unix advanced spawn (uid / gid / setsid + env allowlist)
```csharp
public partial class Command
{
    public Command WithUserId(uint uid);
    public Command WithGroupId(uint gid);
    public Command WithSetsid();
    public Command InheritEnvironment(params string[] varNames);
}
```

`ProcessStartInfo` does not expose `uid`/`gid` on Linux, so this phase introduces an internal `PosixSpawnLauncher` built on `posix_spawn` + `posix_spawn_file_actions_*`. Windows throws `PlatformNotSupportedException`. `InheritEnvironment(["PATH"])` clears the environment then copies the named variables from the current process — a deny-by-default model for sandboxed children.

### Phase 13 — vX (optional) · ProcessKit.Extensions.Logging
An `EventListener` bridge that forwards `EventSource("ProcessKit")` events to `ILogger<T>`. Deferred; runs when there is concrete demand.

## Cross-cutting concerns

### Extensibility — what we add, what we deliberately skip

We add:
- Delegate callbacks for per-line output (`Action<string>` — already present).
- `Func<>` predicates for probes, supervisor `StopWhen`, retry classifier.
- `IProcessRunner` as the mock seam (one method, trivially fakeable).
- Standard .NET observability (`ActivityListener`, `EventListener`, OpenTelemetry exporters).

We deliberately skip:
- `event EventHandler<…>` lifecycle hooks. `Completion` / `Exited` already cover the one-consumer case; multi-consumer observation goes through `ActivityListener`.
- Plugin architectures (`IProcessLifecycleHook` with `OnStarted/OnExited/…`) — speculative surface, no demonstrated need.
- Custom kill protocols or backoff functions. `ProcessGroupOptions.ShutdownTimeout` + `EscalateToKill` and exponential-with-jitter backoff cover the realistic cases; bespoke policies are easier to write as a wrapper over `IProcessRunner` than to retro-fit as a hook.

The principle: a seam earns its place only when there is a concrete test, mock, or observability scenario behind it. Every additional hook is API surface that has to be maintained forever.

### Testability seams

| Seam              | Where         | Why                                                          |
|-------------------|---------------|--------------------------------------------------------------|
| `IProcessRunner`  | existing      | downstream tests fake the whole executor                     |
| `IProcessHandle`  | existing      | unit tests for `ProcessSession` without spawning             |
| `IProcessGroupImpl` | existing    | unit tests for `ProcessGroup` facade                         |
| `IClock`          | added Phase 6 | deterministic backoff / supervisor tests                     |

There is intentionally no `IFileSystem` abstraction — the cgroup v2 backend writes real files; tests cover it through a `LinuxCgroupV2` factory override instead of a full FS shim.

### AOT compatibility

Every phase ships AOT-clean. All P/Invoke uses `[LibraryImport]`, never `[DllImport]`. The Testing package uses `System.Text.Json` source-generated contexts. No `Activator.CreateInstance(string)`, no `Assembly.Load(string)`. The CI `aot-publish` job fails on any `IL2xxx` / `IL3xxx` warning, and the AOT smoke binary exercises every new public API.

### Versioning

ProcessKit currently has no users — naming, signatures, and architecture can change freely until the cycle settles. Minor releases ship feature additions; the major bumps to v2.0 at Phase 7 when the Linux backend changes default. Removed members go away without `[Obsolete]` shims.

## What we don't port

| Rust feature | Decision | Reason |
|--------------|----------|--------|
| `#[non_exhaustive]` enums | drop | C# has no equivalent; API stability via semver |
| `cli_client!` macro | drop | hand-written wrappers (see JjRun) cover the case; a source generator could come later if asked for |
| `MockRunner` (mockall-generated) | drop | `IProcessRunner` is one method; Moq / NSubstitute / `ScriptedProcessRunner` cover mocking ergonomically |
| `tokio::time::pause()` in tests | replace | `IClock` + `FakeClock` is the standard .NET pattern |

## Per-phase verification

Each phase merges only when:

1. `dotnet build -c Release` is warning-clean (`TreatWarningsAsErrors=true`).
2. `dotnet test` passes on Windows, Linux (in Docker via `scripts/test-linux.ps1`), and macOS — the existing CI matrix.
3. The AOT smoke binary publishes and runs on `linux-x64` exercising the new API.
4. An `ActivityListener` test asserts the new spans with the expected tags.
5. `CHANGELOG.md` has an `## [Unreleased]` entry under the appropriate bucket.
6. The release workflow tags, packs, and publishes every package whose version moved.

After Phase 12 the README gets a full feature-matrix table (`Rust crate → C# package + class`) and a fresh-project install check confirms every package resolves cleanly.
