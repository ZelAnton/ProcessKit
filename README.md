# ProcessKit

Cross-platform child process lifetime management and execution for .NET.

ProcessKit ensures that child processes are terminated when the parent exits
— whether gracefully or via crash — and offers an ergonomic runner for
executing external commands and capturing their output.

On Windows this is backed by kernel Job Objects;
on Unix systems (Linux, macOS, FreeBSD) it uses POSIX process groups.

## Requirements

- .NET 10.0 or later
- Windows 8+ / Linux / macOS / FreeBSD
- AOT-compatible

## Installation

Available on [NuGet.org](https://www.nuget.org/packages/ProcessKit).

```sh
dotnet add package ProcessKit
```

## Verifying the package

Each GitHub Release ships a `SHA256SUMS` file alongside the `.nupkg` / `.snupkg`.
Download all three into the same directory, then:

```sh
sha256sum -c SHA256SUMS
```

Expected:

```
ProcessKit.<version>.nupkg: OK
ProcessKit.<version>.snupkg: OK
```

The package on NuGet.org carries a repository signature from nuget.org, which
attributes it to the `ProcessKit` account. You can inspect it with
`dotnet nuget verify ProcessKit.<version>.nupkg --all`.

## Usage

### `ProcessGroup` — lifetime management

```csharp
using System.Diagnostics;
using ProcessKit;

// Children are terminated when the group is disposed —
// even if the parent process crashes.
using var group = new ProcessGroup();

var psi = new ProcessStartInfo("myworker", ["--arg"]) { UseShellExecute = false };
var worker = group.Start(psi);

// Kill on cancellation
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var transient = group.Start(psi, cts.Token);

// Add an externally-started process to the group
var external = Process.Start(psi);
group.Add(external!);

// Runtime statistics (CPU time, peak memory, active count)
var stats = group.GetStats();
Console.WriteLine($"active={stats.ActiveProcessCount} cpu={stats.TotalCpuTime} peak={stats.PeakMemoryBytes}");

// Async dispose — non-blocking on Unix where SIGTERM/wait is involved
await using var asyncGroup = new ProcessGroup();
// ...
```

### `ProcessRunner` — run external commands

`ProcessRunner` wraps `ProcessGroup` under the hood, so every spawned process
gets the same kill-on-dispose guarantee. The interface itself has a single
method (`Start`); everything else is built on top as extension methods.
For casual use without DI, `ProcessRunner.Default` is a shared singleton.

```csharp
using ProcessKit;

IProcessRunner runner = new ProcessRunner();    // or use ProcessRunner.Default

// Bulk: full stdout + stderr + exit code in one call
var result = await runner.GetFullOutputAsync("git", ["status", "--porcelain"]);
if (result.IsSuccess)
    Console.WriteLine(result.StdOut);

// Fluent error handling
var head = (await runner.GetFullOutputAsync("git", ["rev-parse", "HEAD"])
    .EnsureSuccessAsync()).StdOut.Trim();

// Streaming: read stdout line by line as the process produces it
await foreach (var line in runner.GetOutputAsync("docker", ["logs", "-f", "myapp"]))
    Console.WriteLine(line);

// Exit-only: no output capture, just the code
var rc = await runner.GetExitCodeAsync("npm", ["install"]);

// First matching line (kills process after match)
var branch = await runner.GetFirstLineOutputAsync(
    "git", ["branch", "--show-current"]);

// Binary output (uses raw stdout stream)
var bytes = await runner.GetBytesOutputAsync("git", ["show", "HEAD:logo.png"]);
```

Stdin can be supplied in several forms via `StandardInput`:

```csharp
var options = new ProcessRunOptions
{
    StandardInput = StandardInput.FromString("first\nsecond\n"),
    // or .FromBytes(...), .FromStream(stream),
    //    .FromLines(IAsyncEnumerable<string>), .FromEnumerable(IEnumerable<string>),
    //    .FromFile("path/to/input.txt")  // eager existence check

    Timeout = TimeSpan.FromSeconds(30),    // sets ProcessResult.WasTimedOut on fire
    StandardErrorHandler = line => logger.LogWarning("{Line}", line),
};
var result = await runner.GetFullOutputAsync("grep", ["pattern"], options);
```

Use `Start` directly when you need the running handle (Pid, line counters,
the `Exited` cancellation token, simultaneous stdout and stderr enumeration):

```csharp
await using var p = runner.Start("ffmpeg", ["-i", "in.mp4", "out.webm"]);

// stderr in real-time (ffmpeg writes progress there)
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

`ProcessRunOptions` is a `record` — derive variants with `with`:

```csharp
var fast = new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(5) };
var slow = fast with { Timeout = TimeSpan.FromMinutes(5) };
```

## Running tests on Linux from Windows

The Unix code path (`UnixProcessGroup`, `Libc`) is exercised in CI on
`ubuntu-latest`, but you can also run the suite locally against a Linux
container — useful when changing native interop or shutdown semantics.

Requirements:

- [Rancher Desktop](https://rancherdesktop.io/) (or Docker Desktop) with the
  `dockerd` / moby engine enabled so `docker` is on `PATH`
- PowerShell 7+

```pwsh
pwsh ./scripts/test-linux.ps1
```

The script mounts the repo into `mcr.microsoft.com/dotnet/sdk:10.0` and runs
`dotnet build` + `dotnet test`. The host's `bin/` and `obj/` folders are
shadowed inside the container with anonymous volumes, so the Linux build
neither sees the Windows IDE artifacts nor writes back into the host tree.
A named volume (`processkit-nuget`) caches NuGet packages between runs.

Useful switches:

```pwsh
pwsh ./scripts/test-linux.ps1 -Filter "FullyQualifiedName~TerminateAll"
pwsh ./scripts/test-linux.ps1 -Configuration Debug -Rebuild
```

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the version history.

## License

This project is licensed under the [MIT License](LICENSE).
