using System.Diagnostics;
using System.Runtime.InteropServices;
using ProcessKit.Diagnostics;

namespace ProcessKit;

public sealed class ProcessGroup : IDisposable, IAsyncDisposable
{
	readonly IProcessGroupImpl _impl;
	int _disposed;

	// Mechanism string for span/event tags. Phase 2 will replace this with a public Mechanism enum
	// and a property; until then we derive it from the runtime impl type so spans are still tagged.
	internal string MechanismName => _impl switch
	{
		WindowsJobObject => "JobObject",
		UnixProcessGroup => "Pgroup",
		_ => "None",
	};

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
		if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
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

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		using var activity = ProcessKitActivitySource.Source.StartActivity(
			"processkit.group.shutdown",
			ActivityKind.Internal);
		var processCount = SnapshotActiveProcessCount();
		var mechanism = MechanismName;
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
		var mechanism = MechanismName;
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
