# ProcessKit

[![NuGet](https://img.shields.io/nuget/v/ProcessKit.svg)](https://www.nuget.org/packages/ProcessKit)
[![CI](https://github.com/ZelAnton/ProcessKit/actions/workflows/ci.yml/badge.svg)](https://github.com/ZelAnton/ProcessKit/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Cross-platform child-process management for .NET, with two complementary surfaces:

- **`ProcessGroup`** — every child started in a group is killed atomically when the
  group is disposed, even if the parent process crashes. Windows: kernel
  [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
  with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. Unix (Linux / macOS / FreeBSD): POSIX
  process groups, with a graceful `SIGTERM`-then-`Kill` shutdown.
- **`ProcessRunner` / `IProcessRunner`** — an async-first runner for external commands.
  Stream stdout/stderr line-by-line via `IAsyncEnumerable<string>`, capture bulk output
  (`ProcessResult<T>` with stderr and exit code), or just get the exit code. Pipe stdin
  from a `string` / bytes / `Stream` / `IAsyncEnumerable<string>` / file.

Every spawned process — whether started via `ProcessGroup.Start` or `ProcessRunner` —
inherits the kill-on-dispose guarantee. AOT-compatible. Zero external runtime
dependencies.

## Why ProcessKit?

`System.Diagnostics.Process` leaks orphaned child processes when the parent dies, and
wiring up reliable stdout/stderr capture (without deadlocking on a full pipe buffer) is
notoriously fiddly. ProcessKit fixes both:

- **No orphans.** Children are bound to an OS-level group and reaped on dispose — even on
  a hard parent crash, the kernel tears the group down (Windows Job Object) or the runner
  signals the whole process group (Unix).
- **No pipe deadlocks.** stdout and stderr are always drained on background tasks, so a
  chatty child never blocks on a full OS buffer regardless of whether you read the streams.
- **No stdin hangs.** When you supply no input, stdin is closed at start, so a process that
  reads stdin sees EOF immediately instead of blocking on an inherited console handle.
- **Async-first, allocation-light, and AOT-clean.** All native interop is `[LibraryImport]`;
  the library is verified end-to-end by a Native AOT smoke test in CI.

## Features

- Atomic kill-on-dispose for whole process trees (Windows Job Objects / POSIX groups).
- Streaming stdout/stderr as `IAsyncEnumerable<string>`, decoded UTF-8 by default.
- Bulk capture to `string` or `byte[]`, with exit code and captured stderr.
- Stdin from `string` / bytes / `Stream` / `IAsyncEnumerable<string>` / `IEnumerable<string>` / file.
- Per-line push handlers (tee output to a logger while also streaming/capturing).
- Timeouts (with a distinct `WasTimedOut` flag) and full `CancellationToken` support.
- Runtime diagnostics: PID, start time, duration, CPU time, peak memory, live line counters.
- Fluent error handling: `EnsureSuccess()` / `EnsureSuccessAsync()` → `ProcessExitException`.
- Shared or private process groups per run; structural `with`-options.
- Thread-safe `ProcessGroup`; stateless runner with a ready-to-use `ProcessRunner.Default` singleton.

## Requirements

- .NET 10.0 or later
- Windows 8+ / Linux / macOS / FreeBSD
- AOT-compatible (`IsAotCompatible=true`)

## Installation

Available on [NuGet.org](https://www.nuget.org/packages/ProcessKit).

```sh
dotnet add package ProcessKit
```

## Quick start

```csharp
using ProcessKit;

// Run a command and capture everything:
var result = await ProcessRunner.Default.GetFullOutputAsync("git", ["status", "--porcelain"]);
Console.WriteLine($"exit={result.ExitCode}\n{result.StdOut}");

// Guarantee a worker tree is cleaned up when you're done:
using var group = new ProcessGroup();
group.Start(new ProcessStartInfo("myworker", ["--serve"]) { UseShellExecute = false });
// ...all children die here, atomically, even on an unhandled exception:
```

## `ProcessGroup` — lifetime management

```csharp
using System.Diagnostics;
using ProcessKit;

// Children are terminated when the group is disposed — even if the parent process crashes.
using var group = new ProcessGroup();

var psi = new ProcessStartInfo("myworker", ["--arg"]) { UseShellExecute = false };
var worker = group.Start(psi);

// Kill on cancellation (the token kills the process; it does not prevent it from starting).
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var transient = group.Start(psi, cts.Token);

// Adopt an externally-started process into the group.
var external = Process.Start(psi);
group.Add(external!);

// Terminate everything now, without disposing the group.
group.TerminateAll();

// Runtime statistics (CPU time, peak memory, active count).
var stats = group.GetStats();
Console.WriteLine($"active={stats.ActiveProcessCount} cpu={stats.TotalCpuTime} peak={stats.PeakMemoryBytes}");

// Async dispose — non-blocking on Unix, where the SIGTERM/wait handshake is involved.
await using var asyncGroup = new ProcessGroup();
// ...
```

`ProcessGroup` is **thread-safe**: `Start`, `Add`, `TerminateAll`, `GetStats`, and dispose
may be called concurrently from multiple threads.

## `ProcessRunner` — run external commands

`ProcessRunner` wraps `ProcessGroup` under the hood, so every spawned process gets the same
kill-on-dispose guarantee. The interface has a single method (`Start`); everything else is
built on top as extension methods. For casual use without DI, `ProcessRunner.Default` is a
shared, thread-safe singleton.

```csharp
using ProcessKit;

IProcessRunner runner = new ProcessRunner();    // or use ProcessRunner.Default

// Bulk: full stdout + stderr + exit code in one call.
var result = await runner.GetFullOutputAsync("git", ["status", "--porcelain"]);
if (result.IsSuccess)
    Console.WriteLine(result.StdOut);

// Fluent error handling — throws ProcessExitException on non-zero exit.
var head = (await runner.GetFullOutputAsync("git", ["rev-parse", "HEAD"])
    .EnsureSuccessAsync()).StdOut.Trim();

// Streaming: read stdout line by line as the process produces it.
await foreach (var line in runner.GetOutputAsync("docker", ["logs", "-f", "myapp"]))
    Console.WriteLine(line);

// Exit-only: no output capture, just the code (streams are still drained).
var rc = await runner.GetExitCodeAsync("npm", ["install"]);

// First matching line (disposing the handle kills the process after the match).
var branch = await runner.GetFirstLineOutputAsync("git", ["branch", "--show-current"]);

// Binary output (bypasses line decoding, uses the raw stdout stream).
var bytes = await runner.GetBytesOutputAsync("git", ["show", "HEAD:logo.png"]);
```

### Runner methods at a glance

All of these are extension methods on `IProcessRunner`; each has a `(ProcessStartInfo, …)`
overload and a convenience `(string executable, IEnumerable<string> arguments, …)` overload.

| Method | Returns | Use when |
|---|---|---|
| `Start` | `IRunningProcess` | You need the live handle (PID, counters, both streams, diagnostics). |
| `GetOutputAsync` | `IAsyncEnumerable<string>` | Stream stdout line-by-line. |
| `GetFirstLineOutputAsync` | `Task<string?>` | You only need the first (optionally matching) stdout line. |
| `GetFullOutputAsync` | `Task<ProcessResult<string>>` | Capture stdout + stderr + exit code together. |
| `GetBytesOutputAsync` | `Task<ProcessResult<byte[]>>` | stdout is binary and must not be line-decoded. |
| `GetExitCodeAsync` | `Task<int>` | You only care about the exit code. |
| `GetOutput` / `GetFirstLineOutput` | `string` / `string?` | Synchronous convenience wrappers. |
| `EnsureSuccessAsync` | `Task<ProcessResult<T>>` | Await + throw on non-zero in one fluent step. |

### The running handle

Use `Start` directly when you need the running handle — its PID, line counters, the
`Exited` cancellation token, or simultaneous stdout **and** stderr enumeration:

```csharp
await using var p = runner.Start("ffmpeg", ["-i", "in.mp4", "out.webm"]);

// ffmpeg writes progress to stderr — consume it in real time.
_ = Task.Run(async () =>
{
    await foreach (var line in p.StdErr)
        progressUi.Update(line);
});

var code = await p.Completion;
Console.WriteLine(
    $"pid={p.Pid} duration={p.Duration} cpu={p.CpuTime} peak={p.PeakMemoryBytes} " +
    $"stdoutLines={p.StdOutLineCount} timedOut={p.WasTimedOut}");
```

`IRunningProcess` exposes: `StdOut` / `StdErr` (line streams), `StdOutLineCount` /
`StdErrLineCount` (atomic, live), `Pid`, `StartTime`, `Duration`, `CpuTime`,
`PeakMemoryBytes`, `WasTimedOut`, the `Exited` cancellation token, and the `Completion`
task that resolves with the raw exit code.

From a handle you can also collapse to a result or get a timeout-aware exit code:

```csharp
await using var p = runner.Start("git", ["status"]);
ProcessResult<string> result = await p.ToResultAsync();   // drain stdout+stderr+exit
int code = await p.CompletionOrThrowAsync();              // throws TimeoutException if Timeout fired
```

> **Always dispose the handle** (use `await using`). Disposing kills the process if it is
> still running, drains the pumps, and releases the underlying group when the runner owns it.
> Breaking out of a `foreach` over `StdOut` early does **not** kill the process — dispose to terminate.

## Standard input

Supply stdin in whatever form fits, via `StandardInput`:

```csharp
var options = new ProcessRunOptions
{
    StandardInput = StandardInput.FromString("first\nsecond\n"),
    // or .FromBytes(ReadOnlyMemory<byte>)
    //    .FromStream(stream, leaveOpen: false)
    //    .FromLines(IAsyncEnumerable<string>)      // streamed, newline-delimited
    //    .FromEnumerable(IEnumerable<string>)      // synchronous counterpart
    //    .FromFile("path/to/input.txt")            // eager existence check (FileNotFoundException)

    Timeout = TimeSpan.FromSeconds(30),             // sets WasTimedOut when it fires
    StandardErrorHandler = line => logger.LogWarning("{Line}", line),
};
var result = await runner.GetFullOutputAsync("grep", ["pattern"], options);
```

When you supply **no** input (the default, or `StandardInput.Empty`), the runner closes the
child's stdin immediately, so a process that reads stdin sees EOF at once rather than
inheriting and blocking on the parent's stdin.

## Options

`ProcessRunOptions` is a `record` — derive variants with `with`:

```csharp
var fast = new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(5) };
var slow = fast with { Timeout = TimeSpan.FromMinutes(5) };
```

| Option | Effect |
|---|---|
| `StandardInput` | Source of stdin data. `null` / `Empty` → stdin closed at start. |
| `StandardOutputHandler` | Per-line push callback for stdout (runs in parallel to streaming/capture). |
| `StandardErrorHandler` | Per-line push callback for stderr. |
| `ProcessGroup` | Join a caller-owned group (see below). `null` → private group, auto-disposed. |
| `Timeout` | Auto-kill after the duration; surfaces as `WasTimedOut`. |
| `StdOutEncoding` / `StdErrEncoding` | Override decoding (defaults to UTF-8). |
| `OutputBuffer` | Cap unconsumed stdout/stderr (`OutputBufferPolicy`, drop-oldest/newest). `null` → unbounded. |
| `WorkingDirectory` / `Environment` | Working dir / env for the `Start(exe, args)` convenience overloads. |

### Bounding memory on unconsumed output

The pumps always drain the OS pipe so the child never blocks — which means unconsumed stderr
buffers unbounded by default (a risk on chatty processes you don't read). Cap it without ever
blocking the child:

```csharp
var options = new ProcessRunOptions
{
    OutputBuffer = new OutputBufferPolicy { MaxBufferedLines = 1_000, Overflow = OutputOverflowMode.DropOldest },
};
```

Line counters (`StdOutLineCount` / `StdErrLineCount`) still count every line read off the pipe, so
`StdOutLineCount` greater than the number of lines you received means some were dropped.

## Timeout vs. cancellation

Both kill the process, but they are distinguishable:

- **`Timeout`** firing sets `WasTimedOut = true` on the handle and on `ProcessResult<T>`.
- **External `CancellationToken`** cancellation throws `OperationCanceledException` from the
  awaiting call and leaves `WasTimedOut = false`.

```csharp
var result = await runner.GetFullOutputAsync("sleep", ["30"],
    new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(1) });
// result.WasTimedOut == true, result.IsSuccess == false
```

## Error handling

The runner never throws on a non-zero exit — it always returns the result. Opt into throwing
with `EnsureSuccess()` (sync) or `EnsureSuccessAsync()` (fluent on the task):

```csharp
try
{
    var r = await runner.GetFullOutputAsync("dotnet", ["build"]).EnsureSuccessAsync();
}
catch (ProcessExitException ex)
{
    Console.Error.WriteLine($"build failed ({ex.ExitCode}): {ex.StdErr}");
}
```

`ProcessExitException` carries the `ExitCode` and the captured `StdErr`. (The exception
*message* truncates stderr to 4 KB to avoid log-poisoning; the `StdErr` property keeps the
full text for programmatic inspection.)

## Shared process groups

By default each run gets a private `ProcessGroup` that is disposed with the handle. Pass a
shared group to bind several runs to one lifetime — the runner then does **not** dispose it;
you own it:

```csharp
using var group = new ProcessGroup();
var options = new ProcessRunOptions { ProcessGroup = group };

await runner.GetExitCodeAsync("step-1", [], options);
await runner.GetExitCodeAsync("step-2", [], options);
// Disposing `group` (here, at end of `using`) terminates anything still alive from either step.
```

## Diagnostics & platform notes

`GetStats()` / `CpuTime` / `PeakMemoryBytes` are best-effort and platform-dependent:

- **Windows** reports kernel-tracked Job Object accounting — including the peak of *total*
  job memory over time, and any auto-assigned descendants (e.g. `conhost`).
- **Unix** sums per-process counters from live processes (exited ones are skipped). The peak
  memory is therefore an upper bound — concurrent peak may be lower if processes peaked at
  different times. After a process exits, `/proc` may no longer expose its counters, so
  `CpuTime` / `PeakMemoryBytes` can return `null`.

## Verifying the package

Each GitHub Release ships a `SHA256SUMS` file alongside the `.nupkg` / `.snupkg`. Download
all three into the same directory, then:

```sh
sha256sum -c SHA256SUMS
```

Expected:

```
ProcessKit.<version>.nupkg: OK
ProcessKit.<version>.snupkg: OK
```

The package on NuGet.org carries a repository signature from nuget.org, which attributes it
to the `ProcessKit` account. Inspect it with
`dotnet nuget verify ProcessKit.<version>.nupkg --all`.

## Contributing

Build with `dotnet build` (warnings are errors) and run tests with
`dotnet test tests/ProcessKit.Tests/ProcessKit.Tests.csproj`. Contributors on Windows can
run the suite inside a Linux container — see [docs/linux-testing.md](docs/linux-testing.md).

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the version history.

## License

This project is licensed under the [MIT License](LICENSE).
