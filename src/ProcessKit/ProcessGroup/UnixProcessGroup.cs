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
	// Subset of _processes whose setpgid silently failed with ESRCH/EPERM/EACCES — those are NOT in
	// our pgroup, so a `kill(-pgid, sig)` broadcast misses them. They need per-process delivery in
	// BroadcastSignal. Mirrors the Rust pgroup impl's "solos" list (src/sys/pgroup.rs).
	readonly List<Process> _solos = [];
	readonly ProcessGroupOptions _options;
	int _pgid;
	int _escalated;

	public Mechanism Mechanism => Mechanism.ProcessGroup;

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
		int currentPgid;
		lock (_lock)
		{
			currentPgid = _pgid;
			// Add to _processes BEFORE setpgid so a setpgid failure never leaks a process we
			// created. Whether this process becomes a member of our pgroup or a solo is decided
			// after setpgid returns.
			_processes.Add(process);
		}

		// If no leader yet, this process tries to become one (setpgid(pid, pid) → pid is the new
		// leader). Otherwise it joins the existing leader.
		var target = currentPgid == 0 ? pid : currentPgid;
		var result = setpgid(pid, target);
		bool isSolo = false;
		if (result != 0)
		{
			var err = Marshal.GetLastPInvokeError();
			if (err is not (ESRCH or EPERM or EACCES))
				throw new InvalidOperationException(
					$"setpgid({pid}, {target}) failed with errno {err}.");
			isSolo = true;
		}

		PublishLeaderOrMarkSolo(process, pid, currentPgid, isSolo);
		return process;
	}

	public void Add(Process process)
	{
		var pid = process.Id;
		int currentPgid;
		lock (_lock)
		{
			currentPgid = _pgid;
		}

		var target = currentPgid == 0 ? pid : currentPgid;
		var result = setpgid(pid, target);
		bool isSolo = false;
		if (result != 0)
		{
			var err = Marshal.GetLastPInvokeError();
			if (err is not (ESRCH or EPERM or EACCES))
				throw new InvalidOperationException(
					$"setpgid({pid}, {target}) failed with errno {err}.");
			isSolo = true;
		}

		lock (_lock)
		{
			_processes.Add(process);
		}
		PublishLeaderOrMarkSolo(process, pid, currentPgid, isSolo);
	}

	// Race-safe publication of the elected leader pgid, with fall-back to solo tracking. Called
	// after setpgid returns. Three cases:
	//   1. Joined an existing leader (currentPgid != 0): nothing to publish; if setpgid failed,
	//      track as solo.
	//   2. We were the first caller (currentPgid == 0) and setpgid succeeded: try to publish our
	//      pid as the leader. If another concurrent caller already won the election in the
	//      meantime, our process is in its OWN orphan pgroup — track as solo so BroadcastSignal
	//      still reaches it.
	//   3. We were leader-electing but setpgid failed: don't publish anything (_pgid stays 0 so
	//      the next caller can elect a real leader); track as solo.
	void PublishLeaderOrMarkSolo(Process process, int pid, int currentPgid, bool isSolo)
	{
		lock (_lock)
		{
			if (!isSolo && currentPgid == 0)
			{
				if (_pgid == 0)
					_pgid = pid;  // we won the leader-election race
				else
					isSolo = true;  // another thread won; we're in an orphan pgroup
			}
			if (isSolo)
				_solos.Add(process);
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

	public Task SignalAsync(Signal signal, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		BroadcastSignal(SignalNumbers.ToPosix(signal));
		return Task.CompletedTask;
	}

	public Task SignalAsync(CustomSignal signal, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		BroadcastSignal(signal.Number);
		return Task.CompletedTask;
	}

	public Task SuspendAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		BroadcastSignal(SignalNumbers.SigStop());
		return Task.CompletedTask;
	}

	public Task ResumeAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		BroadcastSignal(SignalNumbers.SigCont());
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<int>> GetMembersAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		Process[] snapshot;
		lock (_lock)
		{
			snapshot = [.. _processes];
		}

		var pids = new List<int>(snapshot.Length);
		foreach (var process in snapshot)
		{
			try
			{
				if (process.HasExited)
					continue;
				pids.Add(process.Id);
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				// Process exited or was disposed concurrently — drop it from the snapshot. Matches
				// the existing GetStats race-tolerance pattern above.
			}
		}

		return Task.FromResult<IReadOnlyList<int>>(pids);
	}

	void BroadcastSignal(int signalNumber)
	{
		Process[] soloSnapshot;
		int pgid;
		lock (_lock)
		{
			pgid = _pgid;
			soloSnapshot = [.. _solos];
		}

		// One pgroup broadcast covers every grouped member exactly once — avoids the double-delivery
		// that user-installed SIGUSR1/USR2/HUP/INT handlers would observe if we ran a per-process
		// fallback over the same set. Solos (setpgid silently failed) are NOT in our pgroup, so we
		// deliver to them individually below.
		if (pgid != 0)
		{
			var rc = kill(-pgid, signalNumber);
			if (rc != 0)
			{
				var err = Marshal.GetLastPInvokeError();
				if (err != ESRCH)
					throw new InvalidOperationException(
						$"kill(-{pgid}, {signalNumber}) failed with errno {err}.");
				// ESRCH → pgroup gone (all grouped members exited). Continue to solos.
			}
		}

		foreach (var process in soloSnapshot)
		{
			int pid;
			try
			{
				if (process.HasExited)
					continue;
				pid = process.Id;
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				// Process gone or disposed mid-iteration — racing the broadcast against natural exit
				// is expected, not an error. Skip and move on.
				continue;
			}

			var rc = kill(pid, signalNumber);
			if (rc == 0)
				continue;

			var err = Marshal.GetLastPInvokeError();
			if (err == ESRCH)
				continue;  // solo exited between HasExited check and kill — race, ignore.
			throw new InvalidOperationException(
				$"kill({pid}, {signalNumber}) failed with errno {err}.");
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
