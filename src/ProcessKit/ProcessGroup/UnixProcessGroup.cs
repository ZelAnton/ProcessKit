using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static ProcessKit.Libc;

namespace ProcessKit;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("freebsd")]
sealed class UnixProcessGroup : IProcessGroupImpl
{
	readonly Lock _lock = new();
	readonly List<Process> _processes = [];
	readonly ProcessGroupOptions _options;
	int _pgid;
	int _escalated;

	public bool EscalatedToKill => Volatile.Read(ref _escalated) != 0;

	public UnixProcessGroup(ProcessGroupOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
	}

	public Process StartAndAdd(ProcessStartInfo startInfo)
	{
		var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start process.");

		var pid = process.Id;
		int pgid;
		lock (_lock)
		{
			if (_pgid == 0)
				_pgid = pid;
			pgid = _pgid;
			_processes.Add(process);
		}

		var result = setpgid(pid, pgid);
		if (result != 0)
		{
			var err = Marshal.GetLastPInvokeError();
			if (err is not (ESRCH or EPERM or EACCES))
				throw new InvalidOperationException(
					$"setpgid({pid}, {pgid}) failed with errno {err}.");
		}

		return process;
	}

	public void Add(Process process)
	{
		var pid = process.Id;
		int pgid;
		lock (_lock)
		{
			if (_pgid == 0)
				_pgid = pid;
			pgid = _pgid;
		}

		var result = setpgid(pid, pgid);
		if (result != 0)
		{
			var err = Marshal.GetLastPInvokeError();
			if (err is not (ESRCH or EPERM or EACCES))
				throw new InvalidOperationException(
					$"setpgid({pid}, {pgid}) failed with errno {err}.");
		}

		lock (_lock)
		{
			_processes.Add(process);
		}
	}

	public void TerminateAll()
	{
		Process[] snapshot;
		int pgid;
		lock (_lock)
		{
			pgid = _pgid;
			snapshot = [.. _processes];
		}

		SignalAll(pgid, snapshot);
	}

	public ProcessGroupStats GetStats()
	{
		Process[] snapshot;
		lock (_lock)
		{
			snapshot = [.. _processes];
		}

		var active = 0;
		var cpu = TimeSpan.Zero;
		long peakMem = 0;

		foreach (var process in snapshot)
		{
			try
			{
				if (process.HasExited)
					continue;

				process.Refresh();
				active++;
				cpu += process.TotalProcessorTime;
				peakMem += process.PeakWorkingSet64;
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				// The process exited (or was disposed by another thread) between the HasExited check
				// and reading its counters — a normal shutdown race. Skip it; it contributes nothing.
			}
		}

		return new ProcessGroupStats(active, cpu, peakMem);
	}

	public void Dispose()
	{
		Process[] snapshot;
		int pgid;
		lock (_lock)
		{
			pgid = _pgid;
			snapshot = [.. _processes];
		}

		SignalAll(pgid, snapshot);

		var start = Stopwatch.GetTimestamp();
		var timeout = _options.ShutdownTimeout;
		foreach (var process in snapshot)
		{
			try
			{
				var remaining = timeout - Stopwatch.GetElapsedTime(start);
				var remainingMs = remaining > TimeSpan.Zero ? (int)remaining.TotalMilliseconds : 0;
				if (!process.HasExited && !process.WaitForExit(remainingMs) && _options.EscalateToKill)
				{
					process.Kill(entireProcessTree: true);
					Interlocked.Exchange(ref _escalated, 1);
				}
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				// Process exited or was disposed concurrently during teardown — already gone, which
				// is exactly the goal here; nothing left to wait for or kill.
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		Process[] snapshot;
		int pgid;
		lock (_lock)
		{
			pgid = _pgid;
			snapshot = [.. _processes];
		}

		SignalAll(pgid, snapshot);

		var start = Stopwatch.GetTimestamp();
		var timeout = _options.ShutdownTimeout;
		foreach (var process in snapshot)
		{
			try
			{
				if (process.HasExited)
					continue;

				var remaining = timeout - Stopwatch.GetElapsedTime(start);
				if (remaining > TimeSpan.Zero)
				{
					using var cts = new CancellationTokenSource(remaining);
					try
					{
						await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						// grace window elapsed — fall through to escalation (if enabled)
					}
				}

				if (!process.HasExited && _options.EscalateToKill)
				{
					process.Kill(entireProcessTree: true);
					Interlocked.Exchange(ref _escalated, 1);
				}
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				// Process exited or was disposed concurrently during teardown — already gone, which
				// is exactly the goal here; nothing left to wait for or kill.
			}
		}
	}

	static void SignalAll(int pgid, Process[] processes)
	{
		if (pgid != 0)
			kill(-pgid, SIGTERM);

		foreach (var process in processes)
		{
			try
			{
				if (!process.HasExited)
					kill(process.Id, SIGTERM);
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				// Process has already exited or was disposed by another thread mid-iteration —
				// expected race during shutdown; nothing to signal.
			}
		}
	}
}
