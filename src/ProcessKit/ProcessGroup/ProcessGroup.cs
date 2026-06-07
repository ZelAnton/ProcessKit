using System.Diagnostics;
using System.Runtime.InteropServices;
using ProcessKit.Diagnostics;

namespace ProcessKit;

public sealed class ProcessGroup : IDisposable, IAsyncDisposable
{
	readonly IProcessGroupImpl _impl;
	int _disposed;

	/// <summary>The kernel-level containment mechanism this group is using on the current host.</summary>
	public Mechanism Mechanism => _impl.Mechanism;

	/// <summary>Creates a process group with default shutdown behavior (<see cref="ProcessGroupOptions.Default"/>).</summary>
	public ProcessGroup() : this(ProcessGroupOptions.Default) { }

	/// <summary>Creates a process group with the given shutdown options.</summary>
	public ProcessGroup(ProcessGroupOptions options) : this(SelectImpl(options)) { }

	// Internal seam: lets tests inject a fake IProcessGroupImpl to exercise the façade
	// (disposed guards, argument validation, delegation, dispose idempotency, pre-start
	// cancellation) without spawning a real OS process.
	internal ProcessGroup(IProcessGroupImpl impl)
	{
		ArgumentNullException.ThrowIfNull(impl);
		_impl = impl;
	}

	static IProcessGroupImpl SelectImpl(ProcessGroupOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (OperatingSystem.IsWindows())
			return new WindowsJobObject(options);
		if (OperatingSystem.IsLinux())
		{
			// Prefer cgroup v2 when mounted AND delegation is granted. Falls back to the POSIX
			// process group transparently on hosts without delegation (no exception surface).
			var cgroup = LinuxCgroupV2.TryCreate(options);
			if (cgroup is not null)
				return cgroup;
			return new UnixProcessGroup(options);
		}
		if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
			return new UnixProcessGroup(options);

		throw new PlatformNotSupportedException(
			$"ProcessGroup is not supported on {RuntimeInformation.OSDescription}.");
	}

	public Process Start(ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		ArgumentNullException.ThrowIfNull(startInfo);

		cancellationToken.ThrowIfCancellationRequested();

		var process = _impl.StartAndAdd(startInfo);

		if (cancellationToken.CanBeCanceled)
			RegisterKillOnCancel(process, cancellationToken);

		return process;
	}

	public void Add(Process process)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		ArgumentNullException.ThrowIfNull(process);

		_impl.Add(process);
	}

	public void TerminateAll()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		_impl.TerminateAll();
	}

	public ProcessGroupStats GetStats()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		return _impl.GetStats();
	}

	/// <summary>
	/// Sends a canonical signal to every group member. Windows: only <see cref="Signal.Kill"/> is
	/// supported (maps to <c>TerminateJobObject</c>); other signals throw
	/// <see cref="PlatformNotSupportedException"/>. Unix: broadcasts via <c>killpg</c>.
	/// </summary>
	public Task SignalAsync(Signal signal, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		cancellationToken.ThrowIfCancellationRequested();
		return SignalCoreAsync(signal.ToString(), ct => _impl.SignalAsync(signal, ct), cancellationToken);
	}

	/// <summary>
	/// Sends a raw POSIX signal number to every group member. Unix-only — throws
	/// <see cref="PlatformNotSupportedException"/> on Windows.
	/// </summary>
	public Task SignalAsync(CustomSignal signal, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		cancellationToken.ThrowIfCancellationRequested();
		return SignalCoreAsync($"Custom({signal.Number})", ct => _impl.SignalAsync(signal, ct), cancellationToken);
	}

	async Task SignalCoreAsync(string signalName, Func<CancellationToken, Task> implCall, CancellationToken cancellationToken)
	{
		var mechanism = _impl.Mechanism.ToString();
		var processCount = SnapshotActiveProcessCount();

		using var activity = ProcessKitActivitySource.Source.StartActivity(
			"processkit.group.signal",
			ActivityKind.Internal);
		activity?.SetTag("mechanism", mechanism);
		activity?.SetTag("signal", signalName);
		activity?.SetTag("process_count", processCount);

		try
		{
			await implCall(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
			throw;
		}

		ProcessKitEventSource.Log.GroupSignalled(mechanism, signalName, processCount);
	}

	/// <summary>
	/// Pauses every group member. Unix: broadcasts <c>SIGSTOP</c> via <c>killpg</c> (atomic w.r.t.
	/// every member of the process group). Windows: best-effort — enumerates job-member threads via
	/// <c>Toolhelp32</c> and calls <c>SuspendThread</c> on each. Threads that can't be opened
	/// (already exited, or protected/system threads denying <c>THREAD_SUSPEND_RESUME</c>) are
	/// skipped without surfacing — they keep running. Per-thread suspend counts stack, so each
	/// <see cref="SuspendAsync"/> must be paired with a matching <see cref="ResumeAsync"/>.
	/// </summary>
	public Task SuspendAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		cancellationToken.ThrowIfCancellationRequested();
		return SuspendResumeCoreAsync("processkit.group.suspend", suspend: true, _impl.SuspendAsync, cancellationToken);
	}

	/// <summary>
	/// Resumes every group member. Mirror of <see cref="SuspendAsync"/>.
	/// </summary>
	public Task ResumeAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		cancellationToken.ThrowIfCancellationRequested();
		return SuspendResumeCoreAsync("processkit.group.resume", suspend: false, _impl.ResumeAsync, cancellationToken);
	}

	async Task SuspendResumeCoreAsync(string spanName, bool suspend, Func<CancellationToken, Task> implCall, CancellationToken cancellationToken)
	{
		var mechanism = _impl.Mechanism.ToString();
		var processCount = SnapshotActiveProcessCount();

		using var activity = ProcessKitActivitySource.Source.StartActivity(spanName, ActivityKind.Internal);
		activity?.SetTag("mechanism", mechanism);
		activity?.SetTag("process_count", processCount);

		try
		{
			await implCall(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
			throw;
		}

		if (suspend)
			ProcessKitEventSource.Log.GroupSuspended(mechanism, processCount);
		else
			ProcessKitEventSource.Log.GroupResumed(mechanism, processCount);
	}

	/// <summary>
	/// Snapshot of live group member PIDs. Semantics differ per mechanism: <see cref="Mechanism.JobObject"/>
	/// returns every process the OS assigned to the job; <see cref="Mechanism.ProcessGroup"/> returns
	/// every process the group is tracking that has not yet exited.
	/// </summary>
	public Task<IReadOnlyList<int>> GetMembersAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		cancellationToken.ThrowIfCancellationRequested();
		return _impl.GetMembersAsync(cancellationToken);
	}

	/// <summary>
	/// Brings an externally-started <see cref="Process"/> under this group's containment. Async
	/// observed synonym for <see cref="Add(Process)"/> — emits the <c>processkit.group.adopt</c>
	/// span. Future descendants of the adopted process are captured on Windows (kernel-enforced)
	/// and Linux cgroup v2 (Phase 7); on POSIX process groups only the adopted process itself is
	/// contained.
	/// </summary>
	public async Task AdoptAsync(Process externalProcess, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		ArgumentNullException.ThrowIfNull(externalProcess);

		cancellationToken.ThrowIfCancellationRequested();

		var mechanism = _impl.Mechanism.ToString();
		int pid;
		try
		{
			pid = externalProcess.Id;
		}
		catch (InvalidOperationException)
		{
			// Process never started or already disposed — surface as a regular argument failure
			// before opening an activity span (no point tracing an immediate-reject path).
			throw new ArgumentException(
				"The supplied Process has no associated OS process; start it before calling AdoptAsync.",
				nameof(externalProcess));
		}

		using var activity = ProcessKitActivitySource.Source.StartActivity(
			"processkit.group.adopt",
			ActivityKind.Internal);
		activity?.SetTag("mechanism", mechanism);
		activity?.SetTag("pid", pid);

		try
		{
			_impl.Add(externalProcess);
		}
		catch (Exception ex)
		{
			activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
			throw;
		}

		// process_count is post-adoption so it reflects the new member.
		activity?.SetTag("process_count", SnapshotActiveProcessCount());
		await Task.CompletedTask.ConfigureAwait(false);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		using var activity = ProcessKitActivitySource.Source.StartActivity(
			"processkit.group.shutdown",
			ActivityKind.Internal);
		var processCount = SnapshotActiveProcessCount();
		var mechanism = _impl.Mechanism.ToString();
		activity?.SetTag("mechanism", mechanism);
		activity?.SetTag("process_count", processCount);

		try
		{
			_impl.Dispose();
		}
		catch (Exception ex)
		{
			// The teardown impl threw — the worst case for observability, since the user most needs
			// to see it. Mark the span as failed and surface the message, then re-throw so callers
			// still get the original exception. The finally block below still writes the
			// escalated_to_kill tag and fires the GroupShutdown event.
			activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
			throw;
		}
		finally
		{
			var escalated = _impl.EscalatedToKill;
			activity?.SetTag("escalated_to_kill", escalated);
			ProcessKitEventSource.Log.GroupShutdown(mechanism, escalated, processCount);
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		using var activity = ProcessKitActivitySource.Source.StartActivity(
			"processkit.group.shutdown",
			ActivityKind.Internal);
		var processCount = SnapshotActiveProcessCount();
		var mechanism = _impl.Mechanism.ToString();
		activity?.SetTag("mechanism", mechanism);
		activity?.SetTag("process_count", processCount);

		try
		{
			await _impl.DisposeAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
			throw;
		}
		finally
		{
			var escalated = _impl.EscalatedToKill;
			activity?.SetTag("escalated_to_kill", escalated);
			ProcessKitEventSource.Log.GroupShutdown(mechanism, escalated, processCount);
		}
	}

	int SnapshotActiveProcessCount()
	{
		try
		{
			return _impl.GetStats().ActiveProcessCount;
		}
		catch
		{
			// Stats unavailable (impl-specific failure or platform quirk). We must not throw from
			// the disposal path — fall back to zero so the span/event still records.
			return 0;
		}
	}

	static void RegisterKillOnCancel(Process process, CancellationToken cancellationToken)
	{
		var registration = cancellationToken.Register(static state =>
		{
			try
			{
				((Process)state!).Kill(entireProcessTree: true);
			}
			catch
			{
				// Best-effort kill on cancellation. Kill throws InvalidOperationException when the
				// process already exited (the common case — the cancellation just lost the race) and
				// Win32Exception if the OS refuses; either way there is nothing left to terminate.
			}
		}, process);

		try
		{
			process.EnableRaisingEvents = true;
			process.Exited += (_, _) => registration.Dispose();
			if (process.HasExited)
				registration.Dispose();
		}
		catch (InvalidOperationException)
		{
			registration.Dispose();
		}
	}
}
