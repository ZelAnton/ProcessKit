# AGENTS.md

## Project

- This repository contains `ProcessKit`, a reusable .NET library for cross-platform child process lifetime management and external command execution.
- The public API lives in `src/ProcessKit`.
- Tests live in `tests/ProcessKit.Tests`.
- The AOT validation smoke executable lives in `tests/ProcessKit.AotSmoke` — a minimal console app that exercises the public API and is `dotnet publish -p:PublishAot=true`-ed in CI to catch AOT-incompatible changes (including transitive ones).
- BenchmarkDotNet benchmarks live in `benchmarks/ProcessKit.Benchmarks` (`IsPackable=false`, not in the package or CI). It references the library via `<Reference … Private="true">` + `AssemblySearchPaths` and needs a prior `dotnet build -c Release` of the library (it is not a `ProjectReference`). It is intentionally not AOT-compatible (BDN uses reflection) and is excluded from the AOT smoke. Do not add it to the CI hot path.
- Keep the repository focused on the library; do not introduce CLI, UI, hosting, logging, or dependency injection infrastructure unless explicitly requested.

## Platform

- Target platforms: Windows, Linux, macOS, FreeBSD.
- Windows behavior is implemented with Windows Job Objects.
- Unix-like behavior is implemented with POSIX process groups.

## Runtime

- Use .NET 10.
- Target framework must remain `net10.0` unless explicitly changed.
- Use the repository-wide language settings from `Directory.Build.props`.

## Dependencies

- Do not introduce new NuGet packages without explicit approval.
- Use centralized package management.
- Manage package versions only in `Directory.Packages.props`.
- Do not put package versions on individual `PackageReference` items.

## Current Libraries

- Production project currently has no external NuGet dependencies.
- Test project uses:
	- Microsoft.NET.Test.Sdk
	- NUnit
	- NUnit3TestAdapter
- `Microsoft.NET.Test.Sdk` is required for test discovery and execution through `dotnet test`; do not remove it.

## Architecture

- Keep all functionality available as reusable library APIs.
- Keep platform-specific implementations internal to the library.
- The library namespace is `ProcessKit`. Two public surfaces:
	- **Lifetime layer:** `ProcessGroup` (cross-platform façade over `IProcessGroupImpl`).
	- **Execution layer:** `ProcessRunner` / `IProcessRunner` + `IRunningProcess` (built on top of `ProcessGroup`).
- Preserve the splits:
	- Lifetime: `ProcessGroup` (public) → `IProcessGroupImpl` (internal) → `WindowsJobObject` / `UnixProcessGroup`.
	- Execution: `IProcessRunner` (one-method interface) → `ProcessRunner` (implementation) → `ProcessSession` (internal lifecycle core) → `RunningProcess` (thin `IRunningProcess` adapter) / bytes path → `ProcessRunnerExtensions` (all helpers as extension methods).
	- `ProcessSession` is the **single** owner of the process lifecycle (group, kill/timeout CTS, exit handling, diagnostics, pumps, teardown). Both `RunningProcess` (line mode) and `GetBytesOutputAsync` (byte mode) build on it — do not reintroduce a parallel lifecycle. Stdout transport varies via `IStdOutSink` (`LineChannelStdOutSink` / `ByteBufferStdOutSink`); the line backlog policy varies via `ILineBuffer` (unbounded / bounded-drop / discard). Stderr is always line-oriented.
	- The `IProcessHandle` / `IProcessHandleFactory` seam abstracts `System.Diagnostics.Process` so `ProcessSession` is unit-testable against a `FakeProcessHandle` (via `<InternalsVisibleTo>` to `ProcessKit.Tests`). `RealProcessHandleFactory` wraps the real `Process` **after** `ProcessGroup.Start` assigns it to the Job Object / pgid — the kernel kill-on-dispose guarantee stays in `ProcessGroup` and remains integration-tested. Do not move native assignment behind the fake.
	- `ProcessGroup` shutdown is configured by `ProcessGroupOptions` (`ShutdownTimeout`, `EscalateToKill`) — **Unix-only**; `WindowsJobObject` accepts but ignores it (atomic kill on handle close). The OS branch is `ProcessGroup.SelectImpl`; an internal `ProcessGroup(IProcessGroupImpl)` ctor exists purely so `FakeProcessGroupImpl` can unit-test the façade (guards/validation/delegation/dispose-idempotency/pre-start-cancellation). The post-start `RegisterKillOnCancel` path needs a real `Process` and stays integration-tested.
- Do not expose platform-specific implementation types publicly unless explicitly requested.
- Do not add dependency injection for this small library unless there is a concrete need.

## ProcessRunner conventions

- `IProcessRunner` has **one** method — `Start(ProcessStartInfo, ProcessRunOptions?, CancellationToken)`. Everything else (`GetOutputAsync`, `GetFullOutputAsync`, `GetFirstLineOutputAsync`, `GetBytesOutputAsync`, `GetExitCodeAsync`, convenience `Start(exe, args)`, sync wrappers) must remain extension methods in `ProcessRunnerExtensions`. New helpers go there, not on the interface — this keeps fakes/mocks trivial and helpers consistent.
- The runner never throws on non-zero exit. Bulk methods return `ProcessResult<T>` carrying `StdOut`, `StdErr`, `ExitCode`. Use `ProcessResult.IsSuccess` and `ProcessResult.EnsureSuccess()` for opt-in throwing.
- `ProcessExitException` is the **only** exception used to surface non-zero exits. Do not introduce additional exit-related exception types.
- The runner takes a **defensive copy** of the supplied `ProcessStartInfo`. Never mutate the caller's PSI. The clone helper lives in `PipePumpHelpers.PrepareStartInfo`.
- The runner **forces** `RedirectStandardOutput=true`, `RedirectStandardError=true`, `UseShellExecute=false`, and `RedirectStandardInput=true` on the clone — stdin is **always** redirected. When no input is supplied (`null`/`Empty`), `WriteStandardInputAsync` closes stdin immediately so the child sees EOF at start (the documented "stdin closed" contract); otherwise an inherited stdin would let a stdin-reading child hang. Caller-provided redirection flags on those streams are ignored.
- The PSI clone copies the environment faithfully: `PrepareStartInfo` calls `psi.Environment.Clear()` before copying `source.Environment`. A freshly constructed `ProcessStartInfo.Environment` is pre-seeded with the current process environment, so without the clear a variable the caller *removed* from the source would reappear in the child.
- All text is **UTF-8 by default**. Encoding overrides resolve as `ProcessRunOptions.Std{Out,Err}Encoding` > `ProcessStartInfo.Standard{Output,Error}Encoding` > UTF-8.
- `IRunningProcess` is **line-oriented** by design (`IAsyncEnumerable<string>` for both stdout and stderr) — lines are terminator-free, so reconstructing exact text from them is lossy. Raw byte access is intentionally absent from the interface. The **bulk** helpers capture faithfully without the handle: `GetFullOutputAsync` uses a `TextBufferSink` per stream (exact decoded text — line endings + trailing newline preserved), `GetBytesOutputAsync` uses a `ByteBufferStdOutSink` for stdout + `TextBufferSink` for stderr. `ToResultAsync` operates on the line handle and is therefore line-normalized (documented). Each `ProcessSession` stream takes an `IStdOutSink`; stderr's is optional (null ⇒ line buffer for the streaming handle). Do not route the faithful bulk text back through the line channel.
- Stdout/stderr pumps run unconditionally on background tasks so the child never blocks on a full OS pipe buffer. Unconsumed output buffers indefinitely **by default**; `ProcessRunOptions.OutputBuffer` (`OutputBufferPolicy`) caps the backlog via a non-blocking drop policy (`Channel.CreateBounded` with `DropOldest`/`DropNewest`, or discard at cap 0). A bounded buffer must **never** block the pump (no `BoundedChannelFullMode.Wait`) — that would reintroduce the pipe deadlock. Line counters increment **before** the buffer write so they stay exact even when lines are dropped. `GetExitCodeAsync` defaults to discard (cap 0) since it exposes no output.
- `ProcessGroup` integration has two modes:
	- **Private** (`ProcessRunOptions.ProcessGroup == null`): runner creates its own `ProcessGroup` and disposes it when the handle is disposed.
	- **Shared** (`ProcessRunOptions.ProcessGroup != null`): runner adds the process to the caller-provided group and does **not** dispose it. Caller owns the group lifetime.
	- "No group at all" is intentionally unsupported — call `Process.Start` directly if you do not want lifetime management.
- `IRunningProcess.DisposeAsync` is idempotent and must remain idempotent (Interlocked guard). Final teardown uses `WaitForExitAsync(CancellationToken.None)` — the caller's cancellation token has already done its work (killed the process); honouring it during reap would leak a zombie.
- The `StandardInput` union (`Empty` / `FromString` / `FromBytes` / `FromStream` / `FromLines` / `FromEnumerable` / `FromFile`) is a **closed hierarchy** (`private protected` constructor). Each subtype overrides `internal virtual Task WriteToAsync(Stream, CancellationToken)` to pump itself; `WriteStandardInputAsync` only dispatches and owns the exception containment + the always-close-stdin `finally`. Add a new source = new subtype + factory + `WriteToAsync` override in `StandardInput.cs` (no switch to touch elsewhere). `FromFile` eagerly validates path existence at factory time — without this, a missing path is silently swallowed by the stdin pump's `catch (IOException)`.
- `ProcessRunOptions` is a `record class` (use `with`-expressions to derive variations). Adding new options is a non-breaking change as long as defaults preserve current behaviour.
- `ProcessRunner` may carry baseline options via `new ProcessRunner(defaults)`; `ProcessRunOptionsMerge.Merge` (called once in `Start`) overlays per-call options field-by-field (per-call non-null wins; `Environment` unioned with `StringComparer.Ordinal`, per-call key winning; `KeepStandardInputOpen` per-call only). Keep merge a pure function — never mutate either input. New `ProcessRunOptions` fields must be added to the merge table.
- **Interactive stdin:** `KeepStandardInputOpen` keeps stdin open and surfaces `IRunningProcess.StandardInput` (`IProcessStandardInput` → `ProcessStandardInputWriter`: `SemaphoreSlim`-serialized writes, idempotent `CompleteAsync`). It is honored only by `Start`; the bulk helpers MUST force it off (`CloseStdinForBulk`) since they expose no writer and an open stdin would hang a stdin-reading child. Combining it with an up-front `StandardInput` throws `ArgumentException`. Default stays closed-at-start.
- `ProcessRunOptions.PumpTeardownTimeout` (default 5 s) bounds `DisposeAsync`'s wait for the pumps; `ProcessRunOptions.ProcessGroupOptions` configures only the **private** group the runner creates (never a caller-owned group).
- `IRunningProcess` exposes `CpuTime`, `PeakMemoryBytes`, `WasTimedOut`, `Duration`, `Pid`, `StartTime` and `Completion`/`Exited`. `CpuTime` and `PeakMemoryBytes` are live-sampled before exit and cached after exit (so they survive `_process.Dispose()`). Both may return `null` when the OS no longer exposes the counter (typical on Unix post-exit) — do not tighten the contract to "never null".
- `WasTimedOut` reflects whether `ProcessRunOptions.Timeout` specifically fired. External-token cancellation must keep `WasTimedOut == false`. The implementation tracks this via a **separate** `CancellationTokenSource` whose token registration cascades into `_killCts.Cancel()` — do not merge the timeout into `_killCts.CancelAfter()`, that loses the signal.

## Thread Safety

- `ProcessGroup` public methods (`Start`, `Add`, `TerminateAll`, `GetStats`, `Dispose`, `DisposeAsync`) are safe to call concurrently from multiple threads.
- `_disposed` is coordinated with `Interlocked.Exchange` / `Volatile.Read`; `Dispose` and `DisposeAsync` run their underlying teardown exactly once.
- `UnixProcessGroup` guards `_processes` and `_pgid` with a `System.Threading.Lock` and uses a snapshot pattern in `TerminateAll`, `GetStats`, `Dispose`, and `DisposeAsync` so blocking work (waiting for child exit) happens outside the lock.
- `WindowsJobObject` relies on the kernel's own synchronisation (`SafeFileHandle` + Win32 Job Object APIs) and holds no managed list.
- Do not remove this protection without a replacement; concurrent `Start` followed by `Dispose` from another thread must not corrupt state or leak a child process.

## Process Execution

- All processes started through `ProcessGroup.Start` must be attached to the active process group implementation immediately after start.
- All processes started through `IProcessRunner.Start` go through `ProcessGroup.Start` internally — they automatically inherit the same guarantee.
- On Windows, every spawned process managed by this library must be assigned to a Windows Job Object.
- On Linux, macOS, and FreeBSD, every spawned process managed by this library must be assigned to the POSIX process group used by the library.
- `Dispose` must terminate managed child processes.
- `TerminateAll` must terminate all processes currently managed by the group.
- Tests that start external processes must always clean them up, including failure paths.

## Project References

- Do not use `ProjectReference`.
- Cross-project references must use `Reference`.
- Do not use `HintPath`.
- Projects that reference outputs from other projects must define `AssemblySearchPaths`.
- `AssemblySearchPaths` must contain the output directories of referenced projects.
- Project references must resolve through assembly lookup paths only.
- This rule applies to `tests/ProcessKit.AotSmoke` as well: it consumes the library through `<Reference Include="ProcessKit" />` + `AssemblySearchPaths`. AOT analyzers still operate because `src/ProcessKit` builds with `IsAotCompatible=true`, which bakes the relevant attributes into the produced assembly metadata, and the smoke project sets `<PublishAot>true</PublishAot>`. Build ordering is enforced by `BuildDependency` in `ProcessKit.slnx`.

## Build Ordering

- Use the `.slnx` solution format.
- `.slnx` must define build dependencies between projects.
- Referencing projects must depend on referenced projects.
- Referenced projects must build before dependent projects.
- Build ordering must be explicit and deterministic.

## Repository Structure

- Use `ProcessKit.slnx` as the solution file.
- Use `Directory.Build.props` for repository-wide MSBuild configuration.
- Use `Directory.Packages.props` for centralized package versions.
- Keep source code under `src/`.
- Keep tests under `tests/`.
- Keep helper scripts under `scripts/`.

## MSBuild Path Properties

- `Directory.Build.props` defines two canonical path properties available to every project in the repository:
	- `$(RepoRoot)` — absolute path to the repository root, with a trailing directory separator. Resolved from `$(MSBuildThisFileDirectory)` inside `Directory.Build.props`, which always equals the directory containing that file.
	- `$(ProcessKitProjectDir)` — absolute path to `src/ProcessKit/` (the library project directory).
- Use these properties instead of relative constructs (`..\..\`, `$(MSBuildThisFileDirectory)..\`, etc.) whenever a project file needs to reference a file or directory outside its own directory.
- Do not hardcode cross-project or cross-directory relative paths in `.csproj`, `.props`, or `.targets` files.
- If a new project is added that other projects must reference by path, add a corresponding `$(XxxProjectDir)` property to `Directory.Build.props`.

## Build And Test

- Use `dotnet build ProcessKit.slnx` to validate compilation.
- Use `dotnet test tests/ProcessKit.Tests/ProcessKit.Tests.csproj --no-build` to run tests after a successful build.
- Test execution must report NUnit discovery and a test summary, for example:
	- `NUnit3TestExecutor discovered ...`
	- `Test summary: total: ..., failed: 0, succeeded: ...`
- A successful test run must execute the discovered tests, not only complete MSBuild targets.
- Because project-to-project references use `Reference` instead of `ProjectReference`, build ordering must come from `ProcessKit.slnx`.

## AOT Validation

- `IsAotCompatible=true` on the library declares AOT-readiness; it must also be **proven** by the CI `aot-publish` job (`.github/workflows/ci.yml`), which runs on `ubuntu-latest`.
- The smoke app at `tests/ProcessKit.AotSmoke` sets `<PublishAot>true</PublishAot>` so AOT analyzers (IL2xxx / IL3xxx) run on every `dotnet build` of the solution, not only at publish time.
- The CI job runs `dotnet publish tests/ProcessKit.AotSmoke/ProcessKit.AotSmoke.csproj -c Release -r linux-x64 -p:PublishAot=true` and **executes** the resulting native binary — a non-zero exit fails CI. This catches both compile-time AOT incompatibilities and runtime regressions (e.g. unannotated reflection that AOT strips silently).
- Native AOT prerequisites on the runner (`clang`, `zlib1g-dev`) are installed by the job — do not remove them.
- When introducing new code or new transitive dependencies, treat any AOT warning the same as a compile error: fix the root cause, do not suppress.

## Linux Testing (local, from Windows)

- `scripts/test-linux.ps1` runs the full test suite inside a Linux container using Rancher Desktop or Docker Desktop.
- Requires PowerShell 7+ and a running Docker daemon (`docker` on PATH).
- The script shadows `bin/` and `obj/` folders with anonymous Docker volumes so Windows IDE artifacts do not leak into the Linux build.
- A named volume (`processkit-nuget`) caches NuGet packages between runs.
- Supports `-Filter`, `-Configuration`, and `-Rebuild` parameters.
- Do not modify the anonymous-volume list in the script without also verifying that the Linux build still resolves `ProcessKit.dll` correctly (the test project uses `AssemblySearchPaths` pointing to the standard `src/ProcessKit/bin/` location).

## Formatting

- Use tabs for indentation.
- Never use spaces for indentation.
- Apply tab-only indentation to:
	- `.cs`
	- `.csproj`
	- `.props`
	- `.targets`
	- `.slnx`
	- `.config`
	- `.md`
	- all other repository files
- Preserve LF line endings unless a file-specific rule says otherwise.
- Follow `.editorconfig`.

## C# Style

- Use file-scoped namespaces.
- Keep nullable annotations enabled.
- Keep implicit usings enabled.
- Treat warnings as errors.
- Prefer simple, direct code over new abstractions.
- Minimize public API surface area.
- Public API changes must be intentional and documented.

### Exception handling style

- **No one-line `try`/`catch`/`finally`.** Every `try`, `catch`, and `finally` keyword must own a brace block on its own lines. Forbidden:
	```csharp
	try { foo(); } catch { }
	try { foo(); } catch (IOException) { /* swallow */ }
	finally { stream.Dispose(); }
	```
	Required:
	```csharp
	try
	{
		foo();
	}
	catch (IOException)
	{
		// swallowed - pipe closed by the OS during teardown; nothing actionable.
	}
	```
- **Empty `catch` blocks must contain a short comment explaining why the exception is swallowed.** "What is being caught" plus "why ignoring is correct here". A bare `catch { }` (or `catch (X) { }`) without a justification comment is not acceptable. Examples:
	```csharp
	catch (ObjectDisposedException)
	{
		// runner already torn down — pumps observe disposal as EOF, nothing to recover.
	}

	catch
	{
		// best-effort cleanup in a catch block; rethrowing here would mask the original exception.
	}
	```
- The comment must explain the **rationale**, not just restate the catch clause. "// ignored" or "// swallow" alone is not enough.

## Documentation

- All documentation must be written in English.
- All code comments must be written in English.
- Functional changes must include corresponding README updates when behavior, requirements, usage, or public API changes.
- README updates must reflect the current behavior of the module.
- Documentation changes must be completed after implementation and successful validation.
- Do not leave changed behavior undocumented.

## Changelog

- `CHANGELOG.md` is the single source of truth for release notes.
- The release workflow reads `## [Unreleased]` automatically to populate the GitHub Release body and the NuGet `<PackageReleaseNotes>` field.
- **Every user-visible change must be accompanied by a `CHANGELOG.md` update in the same change set.** This is non-negotiable for: new or modified public API, behavioural changes, bug fixes, deprecations, removals. Pure internal refactors that do not alter observable behaviour are the only exemption.
	- The changelog entry is part of the change, not an optional follow-up. Do not split it into a separate commit unless explicitly asked.
	- If a single change set produces multiple user-visible effects, write one bullet per effect — do not bundle.
- Add a manual bullet under `## [Unreleased]` in `CHANGELOG.md`. Use the appropriate subsection:
	- `### Added` — new features or API members
	- `### Changed` — modified behaviour or API
	- `### Fixed` — bug fixes
	- `### Removed` — removed features or API members
	- `### Deprecated` — features still present but marked for removal
- Write the entry for a consumer of the library, not the implementer. Keep it to one line.
- Replace the placeholder `-` with a real bullet; do not leave placeholder lines alongside real entries.
- Do not modify versioned sections (`## [1.0.0]`, etc.) — those are managed by the release workflow.

### Auto-fill fallback

- If `## [Unreleased]` has no real bullets at release time, the workflow auto-generates entries from commits since the previous tag using `git-cliff` (config: `cliff.toml`). Manual entries always win over auto-fill.
- The first word of the commit subject decides the bucket (case-insensitive):
	- `Add`, `Feat` → `### Added`
	- `Fix`, `Bug` → `### Fixed`
	- `Remove`, `Delete`, `Drop` → `### Removed`
	- `Refactor`, `Update`, `Change`, `Rename`, `Perf`, `CI`, `Cleanup`, etc. → `### Changed`
	- `Doc`, `Chore`, `Test`, `Style` → skipped (excluded from notes)
	- `Release v...` and merge commits → skipped
	- anything unrecognised → `### Changed` (fallback)
- Write commit subjects with these prefixes when you want them to land in the right bucket without editing `CHANGELOG.md`.
- If the auto-fill produces no entries (e.g. only skipped commits since the previous tag), the release fails with a clear error — add a manual entry to unblock it.

## Release Checksums

- The release workflow (`.github/workflows/release.yml`) does **not** author-sign the `.nupkg`/`.snupkg`. NuGet.org adds a repository signature on the publisher account automatically; that is what attributes the package to the `ProcessKit` owner.
- A `SHA256SUMS` manifest is generated from the packed artifacts and attached to the GitHub Release. Format is the standard `<hex>  <filename>` consumed by `sha256sum -c` — this is how downstream consumers verify integrity of artifacts downloaded from the GitHub Release.
- Publishing requires one repository secret:
	- `NUGET_API_KEY` — nuget.org API key with push permission for the `ProcessKit` package
- Do not reintroduce `dotnet nuget sign` against a self-signed certificate: nuget.org validates the author signature's certificate chain against the Microsoft Trusted Root Program and rejects self-signed packages with `NU3018`. If author-signing is brought back later, the cert must be from a public CA.

## Security Scanning

- `.github/workflows/codeql.yml` runs GitHub CodeQL against the C# codebase on pull requests, pushes to `main`, and weekly on a cron schedule. It uses the `security-and-quality` query suite (broader than the default `security-extended`) so injection patterns, unsafe interop, and quality issues are surfaced.
- Build mode is `manual` (the workflow runs `dotnet build ProcessKit.slnx`) because autobuild does not always select the right SDK or solution format.
- CodeQL findings appear under the repository's **Security → Code scanning** tab. Treat new alerts the same as build warnings — investigate and fix or, when a finding is a confirmed false positive, dismiss it in the GitHub UI with a written justification.
- Do not silence or skip CodeQL by editing the workflow to exclude paths or queries without explicit approval — the smaller the suppression surface, the more value the scan provides.

## Comments

- Minimize comments.
- Write comments only when explaining:
	- why something exists
	- architectural decisions
	- non-obvious platform behavior
	- non-obvious process lifetime behavior
- Do not write comments describing what the code already says.

## Version control (jujutsu)

This repository uses [jujutsu (`jj`)](https://jj-vcs.github.io/jj/) for version control. The repo is colocated with git, but `jj` is the primary tool — use `jj` commands for everything in this workflow, not raw `git`.

### Describing the current change

- When you start a new piece of work, set the change description right away:
	```
	jj describe -m "Concise summary of what this change does"
	```
- For larger work, fold subsequent small edits into the current change without asking the user — keep extending the same change rather than starting a new one for each follow-up.
- If the scope of the current change shifts mid-work, refresh the description with another `jj describe -m "..."`. The description must always reflect what's actually being done.

### Starting unrelated work

If the user asks for something unrelated to the in-progress change:
- **Current change is complete** → propose a new change descended from it:
	```
	jj new -m "Description of the new task"
	```
- **Current change still needs more work** → propose a parallel change off the same parent so the user can come back to the current one later:
	```
	jj new @- -m "Description of the unrelated task"
	```
- Do not silently mix the two — every change must stay coherent.

### Pushing to remote

The user signals "synchronise with remote" with a short trigger word (typically `pull` or `push`). On that signal, run the full sync:
1. `jj git fetch` — pull down any remote-side movement (e.g. CI release commits or other contributors' pushes) **before** doing anything else.
2. If `main@origin` has moved past the local change, rebase: `jj rebase -r @- -d main@origin`.
3. Move the `main` bookmark to the completed change: `jj bookmark set main -r <rev>`.
4. Push: `jj git push --bookmark main`.

Never push without an explicit signal from the user.

### Undoing work

When the user decides to abandon work in progress, prefer `jj`'s native undo facilities — they are safer than hand-rolled cleanup:

- **`jj undo`** (alias of `jj op undo`) — reverses the last operation (describe / edit / squash / rebase / abandon / push / etc.). Use this when the latest step was the wrong call. It is repeatable: `jj undo` again undoes the previous one.
- **`jj abandon <rev>`** — drops a specific change entirely. Descendants automatically rebase onto its parent. Useful for "this whole change is the wrong direction; throw it away".
- **`jj restore`** — discards working-copy modifications and resets `@` to its parent's tree. Useful for "I haven't committed yet, just wipe what I did".
- **`jj op log`** is the reflog equivalent — every operation is reachable. If `jj undo` overshoots, `jj op restore <op-id>` jumps to any prior point.

Never hide a deliberate undo: if the user asks to "undo the last commit/change", run `jj undo` (or `jj abandon`) and tell them what was reverted.

### Bookmarks

Work happens on `main`. **Do not create new bookmarks unless the user explicitly asks for one** (e.g. for a feature-branch / PR workflow). The default flow is push-to-main.

### Safety

- Do not revert or amend changes the user authored without explicit agreement.
- Do not rewrite unrelated files when making a focused change.

## Command Conventions

- Commands and APIs should be idempotent where possible.
- Output should remain concise.
- Output should remain script-friendly.
- Breaking changes must be explicit.
