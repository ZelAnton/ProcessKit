# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

[AGENTS.md](AGENTS.md) is the canonical source for the engineering rules (architecture invariants, ProcessRunner conventions, thread-safety contracts, release/changelog policy). This file summarises the same ground plus the day-to-day commands; when the two ever disagree, AGENTS.md wins.

## Formatting

- **Tabs for indentation, never spaces** — applies to every file type (`.cs`, `.csproj`, `.props`, `.targets`, `.slnx`, `.md`, …), per `.editorconfig`.
- **LF line endings** unless a file-specific rule says otherwise.
- File-scoped namespaces; nullable + implicit usings enabled; `TreatWarningsAsErrors=true` (the build is warning-clean — keep it so).

## Commands

```bash
# Build (warnings are errors)
dotnet build

# Run all tests
dotnet test tests/ProcessKit.Tests/ProcessKit.Tests.csproj

# Run a single test
dotnet test tests/ProcessKit.Tests/ProcessKit.Tests.csproj --filter "FullyQualifiedName~TestMethodName"

# Run tests inside a Linux container (requires Rancher Desktop or Docker Desktop, PowerShell 7+)
pwsh scripts/test-linux.ps1
pwsh scripts/test-linux.ps1 -Filter "FullyQualifiedName~TestMethodName"

# AOT publish smoke test (Linux only — exercises Native AOT toolchain end-to-end)
dotnet publish tests/ProcessKit.AotSmoke/ProcessKit.AotSmoke.csproj -c Release -r linux-x64 -p:PublishAot=true
```

## Architecture

The library namespace is `ProcessKit`; the two public surfaces are `ProcessGroup` (process lifetime) and `ProcessRunner` / `IProcessRunner` (run-and-capture-output). `AssemblyName` and `PackageId` are `ProcessKit` — those are the package identity; the namespace is the API surface.

### `ProcessGroup` (lifetime layer)

`ProcessGroup` is a thin cross-platform façade over two platform-specific implementations behind `IProcessGroupImpl`:

- **`WindowsJobObject`** — wraps a Windows [Job Object](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. All assigned processes are killed atomically when the job handle is closed (on `Dispose`). Native interop lives in `Kernel32.cs` (`[LibraryImport]`, `SafeFileHandle` for the job handle).

- **`UnixProcessGroup`** — creates a POSIX process group (`setpgid`) with the first process's PID as the group leader. On `Dispose`, sends `SIGTERM` to `-pgid` (the whole group), waits up to 2 seconds (monotonic, via `Stopwatch`), then falls back to `Process.Kill(entireProcessTree: true)` for any survivors. Native interop lives in `Libc.cs`.

Native P/Invoke declarations are isolated in `Kernel32.cs` / `Libc.cs` as `static partial` classes (one per DLL), using `[LibraryImport]` exclusively — no `[DllImport]`, no `CsWin32`, no `NativeMethods.txt`. The platform-specific impl classes pull them in via `using static`.

### Public API

```csharp
public sealed class ProcessGroup : IDisposable, IAsyncDisposable
{
    public Process Start(ProcessStartInfo startInfo, CancellationToken cancellationToken = default);
    public void Add(Process process);
    public void TerminateAll();
    public ProcessGroupStats GetStats();
    public void Dispose();
    public ValueTask DisposeAsync();
}

public readonly record struct ProcessGroupStats(
    int ActiveProcessCount,
    TimeSpan TotalCpuTime,
    long PeakMemoryBytes);
```

`GetStats()` is backed by `QueryInformationJobObject` on Windows (accounting + extended limit info). On Unix it iterates `_processes`, summing `TotalProcessorTime` and `PeakWorkingSet64` from live processes (exited and disposed are skipped). Semantics differ slightly: Windows reports the kernel-tracked peak of *total* job memory over time; Unix reports the sum of per-process peaks, which is an upper bound — actual concurrent peak may be lower if processes peaked at different times.

`CancellationToken` in `Start` kills the process on cancellation via `Process.Exited` cleanup — it does not prevent the process from starting.

### Key design constraints

- `setpgid` errors `ESRCH`, `EPERM`, `EACCES` are silently ignored — all three are race conditions with the child's `exec()` or natural exit, not real failures.
- On Unix, processes started by `StartAndAdd` are added to `_processes` **before** calling `setpgid`, so a setpgid failure never leaks a process we created. `Add` (for externally-started processes) intentionally appends only after `setpgid` succeeds — the contract is "if Add throws, we did not take ownership."
- On Windows, if `AssignProcessToJobObject` fails after `Process.Start`, the process is killed and disposed before re-throwing — same guarantee.
- The 2-second Unix shutdown timeout is a **shared deadline** across all processes, not per-process.
- `ProcessGroup` is thread-safe: `_disposed` uses `Interlocked.Exchange`/`Volatile.Read`; `UnixProcessGroup` guards `_processes`/`_pgid` with `System.Threading.Lock` and takes a snapshot before any blocking wait or `await` so I/O runs outside the lock. `WindowsJobObject` relies on the kernel's own synchronisation.
- `IsAotCompatible = true` — keep all P/Invoke via `[LibraryImport]`; no reflection-based interop. The CI `aot-publish` job (see [.github/workflows/ci.yml](.github/workflows/ci.yml)) runs `dotnet publish -p:PublishAot=true` on `tests/ProcessKit.AotSmoke` and executes the resulting native binary, so any IL2xxx/IL3xxx warning introduced by a code change fails CI.
- `TreatWarningsAsErrors = true` — the build is warning-clean; keep it that way.

### Exception handling style

- **No one-line `try` / `catch` / `finally`.** Each keyword owns a braced block on its own lines. `try { ... } catch { }` collapsed onto a single line is a style violation.
- **Empty `catch` blocks must carry a comment explaining the rationale** — both what is being swallowed and why ignoring is correct here. `// ignored` alone is not enough; the comment should answer "what exception did we expect, and why is doing nothing the right response?".

Example:
```csharp
try
{
	_exitedCts.Cancel();
}
catch (ObjectDisposedException)
{
	// already disposed - the runner is being torn down concurrently; pumps are winding down anyway.
}
```
See [AGENTS.md](AGENTS.md#exception-handling-style) for the canonical rule.

### `ProcessRunner` (execution layer)

`ProcessRunner : IProcessRunner` is a thin runner that turns a `ProcessStartInfo` into an `IRunningProcess` handle. It wraps `ProcessGroup` internally so every spawned process inherits the kill-on-dispose guarantee.

The interface itself is intentionally minimal — **one method**:

```csharp
public interface IProcessRunner
{
    IRunningProcess Start(
        ProcessStartInfo startInfo,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

Everything else (`GetOutputAsync`, `GetFirstLineOutputAsync`, `GetFullOutputAsync`, `GetBytesOutputAsync`, `GetExitCodeAsync`, `Start(exe, args)` convenience overload, sync wrappers, `Task<ProcessResult<T>>.EnsureSuccessAsync()`) lives in [ProcessRunnerExtensions](src/ProcessKit/ProcessRunnerExtensions.cs) as extension methods on `IProcessRunner`. This split keeps fakes/mocks trivial (one method to implement) and guarantees all helpers behave consistently across implementations.

`ProcessRunner.Default` is a static singleton for callers that don't use DI: `await ProcessRunner.Default.GetFullOutputAsync(...)`. The runner is stateless so the singleton is thread-safe.

#### The handle: `IRunningProcess`

```csharp
public interface IRunningProcess : IAsyncDisposable
{
    IAsyncEnumerable<string> StdOut { get; }         // line-streamed, decoded as UTF-8
    IAsyncEnumerable<string> StdErr { get; }         // ditto
    int StdOutLineCount { get; }                     // atomic, live + stable after exit
    int StdErrLineCount { get; }
    int Pid { get; }
    DateTime StartTime { get; }
    TimeSpan? Duration { get; }                      // null until the process exits, then stable (Stopwatch-based, monotonic)
    TimeSpan? CpuTime { get; }                       // live-sampled; cached snapshot after exit (survives Process.Dispose)
    long? PeakMemoryBytes { get; }                   // same live-then-cached pattern; may be null when OS no longer exposes the counter
    bool WasTimedOut { get; }                        // true iff Options.Timeout fired (NOT external cancellation)
    CancellationToken Exited { get; }                // fires when the process exits
    Task<int> Completion { get; }                    // resolves with the raw exit code; await with .WaitAsync(ct) if needed
}
```

Both `StdOut` and `StdErr` are backed by unbounded `Channel<string>`s pumped by background tasks (`PumpLinesAsync` in [PipePumpHelpers.cs](src/ProcessKit/PipePumpHelpers.cs)). Pumps run unconditionally — even if the caller never enumerates the streams — so the process never blocks on a full OS pipe buffer. That means unconsumed `StdErr` will buffer indefinitely on chatty processes; the XML-doc on `StdErr` documents this OOM risk.

`Completion` may resolve before the last few lines reach `StdOut` — `Process.Exited` fires when the process exits, not when the pipes are fully drained. `await p.Completion` does not guarantee stdout has been fully consumed; if both matter, await both.

`WasTimedOut` is backed by a **separate** `CancellationTokenSource` whose token registration cancels `_killCts`. Do not collapse this into `_killCts.CancelAfter(timeout)` — that would erase the "kind-of-cancellation" signal that distinguishes timeout-kill from caller-cancellation.

`CpuTime` and `PeakMemoryBytes` are live-sampled (read directly from `Process.TotalProcessorTime` / `Process.PeakWorkingSet64`) until `Process.Exited` fires; then the values are cached in `long` sentinels (`-1` = not cached) via `Interlocked.Exchange` so they remain readable after `_process.Dispose()`. Both may return `null` when the underlying counter is unavailable — typical on Unix once `/proc/$PID/` is gone.

#### `ProcessRunOptions`

```csharp
public sealed record ProcessRunOptions
{
    public StandardInput? StandardInput { get; init; }              // null/Empty → stdin closed at start
    public Action<string>? StandardOutputHandler { get; init; }     // push-style stdout tee
    public Action<string>? StandardErrorHandler { get; init; }      // push-style stderr tee
    public ProcessGroup? ProcessGroup { get; init; }                // null → private group + auto-dispose; non-null → shared, caller owns
    public TimeSpan? Timeout { get; init; }                         // auto-kill after this duration (drives WasTimedOut)
    public Encoding? StdOutEncoding { get; init; }                  // overrides PSI; UTF-8 fallback
    public Encoding? StdErrEncoding { get; init; }
}
```

Declared as a `record class` so callers can derive variants via `with`-expression: `var slow = fast with { Timeout = TimeSpan.FromMinutes(5) };`. Equality is structural (handlers compared via `Delegate.Equals`, `ProcessGroup` by reference).

Push handlers run in parallel to the corresponding `IAsyncEnumerable`. Both can be used at the same time — each line goes to both consumers. Handlers run synchronously inside the pump task; exceptions thrown by user code are swallowed so the pump cannot be derailed.

#### `StandardInput`

Closed union of factory methods — `private protected` constructor blocks external derivation, internal sealed subtypes drive the pattern-match in `WriteStandardInputAsync`:

- `StandardInput.Empty`
- `StandardInput.FromString(string, Encoding? = UTF-8)`
- `StandardInput.FromBytes(ReadOnlyMemory<byte>)`
- `StandardInput.FromStream(Stream, bool leaveOpen = false)`
- `StandardInput.FromLines(IAsyncEnumerable<string>, Encoding? = UTF-8)` — newline-delimited streamed input
- `StandardInput.FromEnumerable(IEnumerable<string>, Encoding? = UTF-8)` — synchronous counterpart
- `StandardInput.FromFile(string path)` — file contents piped into stdin. Existence is validated **eagerly** at factory time (`FileNotFoundException`) — otherwise a missing path would be silently swallowed by the stdin pump's `IOException` handler.

#### Defensive PSI clone

`Start` does **not** mutate the caller's `ProcessStartInfo`. `PrepareStartInfo` in [PipePumpHelpers.cs](src/ProcessKit/PipePumpHelpers.cs) builds a copy with the runner's required flags (`RedirectStandardOutput=true`, `RedirectStandardError=true`, `UseShellExecute=false`, and `RedirectStandardInput=true` **always**). stdin is always redirected; when no input is supplied (`null`/`Empty`) the runner closes it immediately in `WriteStandardInputAsync` so the child sees EOF at once — the documented "stdin closed at start" contract. (Leaving stdin inherited would let a stdin-reading child block forever.) The environment is cloned faithfully: `PrepareStartInfo` calls `psi.Environment.Clear()` before copying `source.Environment`, because a fresh `ProcessStartInfo.Environment` is pre-seeded with the current process environment — without the clear, a variable the caller *removed* from the source would silently reappear. Commonly-used PSI fields are copied (FileName, ArgumentList, WorkingDirectory, Environment, CreateNoWindow, WindowStyle, Verb, UserName; Windows-only Domain/PasswordInClearText/LoadUserProfile inside an `OperatingSystem.IsWindows()` guard for CA1416). Exotic settings outside this list may not propagate — document if you extend the clone.

#### `ProcessExitException` and `EnsureSuccess`

`ProcessResult<T>` exposes `bool IsSuccess => ExitCode == 0`, `bool WasTimedOut` (set when `Options.Timeout` fired), and a fluent `EnsureSuccess()` that throws `ProcessExitException` (carrying `ExitCode` and the captured `StdErr`) on non-zero exit. The exception message truncates stderr to 4 KB so unintentional log-poisoning by a 100 MB error dump is impossible.

For async chains: `Task<ProcessResult<T>>.EnsureSuccessAsync()` extension awaits and calls `EnsureSuccess` in one go — useful for `(await runner.GetFullOutputAsync(...).EnsureSuccessAsync()).StdOut` style.

The runner itself never throws on non-zero exit — it always returns the result; only `EnsureSuccess`/`EnsureSuccessAsync` opts into throwing. Streaming methods don't surface the exit code at all (use the bulk variants or the handle's `Completion` if needed).

#### `GetBytesOutputAsync` — the one special case

`GetBytesOutputAsync` is the only extension that **does not** go through `IRunningProcess` — the handle is line-oriented (channels of `string`), and forcing binary stdout through string decoding would be lossy. Instead it talks to `ProcessGroup` directly and copies `process.StandardOutput.BaseStream` into a `MemoryStream`. Stderr is still captured via the standard pump helpers. Some boilerplate is duplicated from `RunningProcess` to avoid leaking raw streams onto the public `IRunningProcess`.

#### Disposal sequencing

`RunningProcess.DisposeAsync` is idempotent (Interlocked guard). On dispose:
1. Cancel `_killCts` — the linked-token registration inside `ProcessGroup.Start` kills the process.
2. Wait up to **5 seconds** for stdout/stderr/stdin pumps to wind down (bounded — a stuck OS pipe must not hang dispose forever).
3. Complete both channels (in case a pump never observed EOF).
4. Reap the process via `WaitForExitAsync(CancellationToken.None)` — must not honour caller cancellation here or the process zombie leaks.
5. If we own the `ProcessGroup` (private case), dispose it. Shared groups are left alone — the caller owns them.

### Test project setup

Both `tests/ProcessKit.Tests` and `tests/ProcessKit.AotSmoke` reference the library via direct `<Reference Include="ProcessKit" />` + `AssemblySearchPaths` (not `<ProjectReference>`). Build ordering comes from `BuildDependency` entries in `ProcessKit.slnx`. Run tests after a `dotnet build` or let the test runner build implicitly.

`tests/ProcessKit.AotSmoke` sets `<PublishAot>true</PublishAot>` in the csproj, so AOT analyzers (`IL2xxx`/`IL3xxx`) run on every `dotnet build` of the solution — not only at `dotnet publish` time. Native AOT compilation itself still happens only at publish. The CI `aot-publish` job runs the published binary, so code that AOT-strips at runtime (e.g. unannotated reflection) breaks CI even if compilation succeeded.

### Linux testing from Windows

`scripts/test-linux.ps1` mounts the repo into `mcr.microsoft.com/dotnet/sdk:10.0` and runs `dotnet build` + `dotnet test`. Anonymous Docker volumes shadow `src/ProcessKit/bin`, `src/ProcessKit/obj`, `tests/ProcessKit.Tests/bin`, and `tests/ProcessKit.Tests/obj` to keep the host working copy untouched. A named volume (`processkit-nuget`) caches packages between runs. CI mirrors this with `.github/workflows/ci.yml`, which runs the same build/test across `ubuntu-latest`, `windows-latest`, and `macos-latest` on PR and push to main.

### MSBuild path properties

`Directory.Build.props` defines two canonical path properties that every project in the repo inherits:

- `$(RepoRoot)` — absolute path to the repository root (trailing separator included). Derived from `$(MSBuildThisFileDirectory)` inside `Directory.Build.props`, so it is always the directory that contains that file.
- `$(ProcessKitProjectDir)` — absolute path to `src/ProcessKit/`.

Use these properties wherever a `.csproj`, `.props`, or `.targets` file must reference something outside its own directory — never write `..\..\` or `$(MSBuildThisFileDirectory)..\` directly. If a new project is added that others reference by path, add a matching `$(XxxProjectDir)` property to `Directory.Build.props`.

## Changelog

`CHANGELOG.md` is the single source of truth for release notes. The release workflow reads the `## [Unreleased]` section automatically — it populates the GitHub Release body and the NuGet `<PackageReleaseNotes>` field.

**Rule: every user-visible change ships with a `CHANGELOG.md` entry in the same change set.** This covers new or modified public API, behavioural changes, bug fixes, deprecations, and removals. The only exemption is pure internal refactors that do not alter observable behaviour. The changelog update is part of the change, not a follow-up task — never defer it.

**How to add an entry:**

1. Open `CHANGELOG.md`.
2. Under `## [Unreleased]`, find the appropriate subsection:
   - `### Added` — new features or API members
   - `### Changed` — modified behaviour or API
   - `### Fixed` — bug fixes
   - `### Removed` — removed features or API members
   - `### Deprecated` — features still present but marked for removal
3. Replace the placeholder `-` with a real bullet (or append after existing bullets). Keep it one line, written for a consumer of the library — not for the implementer. One bullet per distinct user-visible effect — bundle nothing.

Example:

```markdown
### Fixed
- `TerminateAll` no longer throws `ObjectDisposedException` when called concurrently with `Dispose`.
```

Do **not** touch the versioned sections (`## [1.0.0]`, etc.) — the release workflow manages those.

### Auto-fill from git log

If `## [Unreleased]` has no real bullets when the release workflow runs, it auto-generates entries from commits since the previous tag via [git-cliff](https://git-cliff.org/) (config: `cliff.toml`). Manual entries always take priority — auto-fill is a fallback so a release never blocks on missing notes, not the default.

The auto-fill bucket is decided by the first word of the commit subject:

| Prefix (case-insensitive) | Bucket |
|---|---|
| `Add`, `Feat` | `### Added` |
| `Fix`, `Bug` | `### Fixed` |
| `Remove`, `Delete`, `Drop` | `### Removed` |
| `Refactor`, `Update`, `Change`, `Rename`, `Perf`, `CI`, `Cleanup`, ... | `### Changed` |
| `Doc`, `Chore`, `Test`, `Style` | skipped (not in notes) |
| `Release v...`, `Merge ...` | skipped |
| anything else | `### Changed` (fallback) |

Write commit subjects accordingly when you want them to appear in the right bucket without touching `CHANGELOG.md`. If you want one wording for the commit and another for the changelog, write the manual entry — it wins.

## Release packaging

The release workflow ([.github/workflows/release.yml](.github/workflows/release.yml)) packs `.nupkg`/`.snupkg`, writes a `SHA256SUMS` manifest (standard `sha256sum -c` format), pushes the package to NuGet.org, and attaches all three artifacts to the GitHub Release. There is **no** author-signing step — NuGet.org adds its repository signature on the publisher account automatically, which is what attributes the package on the registry.

Publishing requires one repo secret: `NUGET_API_KEY` — nuget.org API key with push permission for the `ProcessKit` package.

Self-signed author-signing was tried and removed: nuget.org validates the author signature's chain against the Microsoft Trusted Root Program and rejects self-signed packages with `NU3018`. If author-signing is ever reinstated, the certificate must come from a public CA (DigiCert, Sectigo, SSL.com, …) — not from `New-SelfSignedCertificate`.

## Security scanning

[.github/workflows/codeql.yml](.github/workflows/codeql.yml) runs GitHub CodeQL against the C# codebase on PR, push to `main`, and weekly. The query suite is `security-and-quality` (broader than the default `security-extended`) and `build-mode: manual` so the workflow drives an explicit `dotnet build ProcessKit.slnx` — autobuild can pick the wrong SDK or miss `.slnx`. Findings land under repo **Security → Code scanning**. Treat new alerts like build warnings; if a finding is a confirmed false positive, dismiss it in the GitHub UI with a written justification rather than tweaking the workflow to hide it.

## Version control workflow

The repo uses [jujutsu (`jj`)](https://jj-vcs.github.io/jj/) (colocated with git). Use `jj` commands; the canonical workflow:

- **Describe early.** When starting a new piece of work, immediately set the change description:
	```
	jj describe -m "Concise summary"
	```
	Small follow-ups for the same task get folded into the current change without asking — keep extending the same `jj` change, don't spawn one per edit. If the scope shifts, run `jj describe -m "..."` again so the description matches reality.
- **Unrelated work mid-task.** If the user requests something orthogonal, ask before splitting:
	- Current change finished? → `jj new -m "..."` (descendant).
	- Current change still in progress? → `jj new @- -m "..."` (parallel sibling, so you can return to the original later).
- **Sync on the user's trigger.** When the user says `pull` (or `push`/`sync`), run the full handshake:
	1. `jj git fetch` first — picks up any remote movement (CI release commits, etc.).
	2. Rebase if `main@origin` advanced: `jj rebase -r @- -d main@origin`.
	3. `jj bookmark set main -r <rev>` then `jj git push --bookmark main`.

	Never push without an explicit signal from the user.
- **Undoing dropped work.** When the user decides to abandon something already done, reach for `jj`'s safety net rather than hand-cleanup:
	- `jj undo` (alias of `jj op undo`) reverses the last operation — describe, edit, squash, rebase, abandon, push, all of it. Repeatable.
	- `jj abandon <rev>` drops a specific change entirely; descendants auto-rebase.
	- `jj restore` discards working-copy edits back to the parent's tree.
	- `jj op log` is the full reflog if you need to go further back via `jj op restore <op-id>`.
- **No new bookmarks** unless the user explicitly asks. Work lives on `main`; that is the publish target.
