using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProcessKit;

public sealed class ProcessGroup : IDisposable, IAsyncDisposable
{
	readonly IProcessGroupImpl _impl;
	int _disposed;

	public ProcessGroup()
	{
		if (OperatingSystem.IsWindows())
			_impl = new WindowsJobObject();
		else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
			_impl = new UnixProcessGroup();
		else
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
				// ignored
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
