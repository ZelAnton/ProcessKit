using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProcessKit;

public sealed class ProcessGroup : IDisposable, IAsyncDisposable
{
	readonly IProcessGroupImpl _impl;
	int _disposed;

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
		_impl.Dispose();
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		await _impl.DisposeAsync().ConfigureAwait(false);
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
