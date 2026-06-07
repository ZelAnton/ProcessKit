using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using static ProcessKit.Libc;

namespace ProcessKit;

/// <summary>
/// Linux cgroup v2 implementation of <see cref="IProcessGroupImpl"/>. Provides escape-proof
/// kill-on-drop containment (a child cannot escape via <c>setsid</c>), atomic kill via
/// <c>cgroup.kill</c> (kernel ≥ 5.14), and atomic freeze via <c>cgroup.freeze</c> (kernel ≥ 5.2).
/// Falls back to per-pid SIGKILL/SIGSTOP loops on older kernels.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Spawn-time race window.</strong> .NET does not expose a pre-exec hook, so the child PID
/// is written to <c>cgroup.procs</c> AFTER <see cref="Process.Start"/> returns. There is a
/// microsecond window between <c>fork+exec</c> and the write where a child's own
/// <c>fork()</c> descendants would escape the cgroup. The window is bounded by the wall-clock
/// distance between the two operations (typically ≪ 1 ms); real-world processes never fork
/// within this gap. Phase 8 (resource limits) may revisit if the gap proves observable.
/// </para>
/// <para>
/// Selected by <see cref="ProcessGroup"/> on Linux when both:
/// (1) <c>/sys/fs/cgroup/cgroup.controllers</c> exists (cgroup v2 is mounted), and
/// (2) the current process can create a sub-cgroup (delegation is granted — root, container, or
/// systemd unit with <c>Delegate=yes</c>). Otherwise <see cref="ProcessGroup"/> falls back to
/// <see cref="UnixProcessGroup"/> transparently.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
sealed class LinuxCgroupV2 : IProcessGroupImpl
{
	static int s_nextId;

	readonly Lock _lock = new();
	readonly List<Process> _processes = [];
	readonly string _cgroupPath;
	int _escalated;
	int _disposed;

	public Mechanism Mechanism => Mechanism.CgroupV2;

	public bool EscalatedToKill => Volatile.Read(ref _escalated) != 0;

	LinuxCgroupV2(string cgroupPath)
	{
		_cgroupPath = cgroupPath;
	}

	/// <summary>Detects whether cgroup v2 is mounted AND the current process can create a sub-cgroup.
	/// Used for test gating; <see cref="TryCreate"/> is the authoritative production check.</summary>
	public static bool IsAvailable() =>
		OperatingSystem.IsLinux()
		&& File.Exists("/sys/fs/cgroup/cgroup.controllers")
		&& CanCreateSubgroup();

	/// <summary>Attempts to create a private cgroup v2 subgroup for the new <see cref="ProcessGroup"/>.
	/// Returns null when cgroup v2 isn't mounted, no delegation, or directory creation fails —
	/// callers fall back to <see cref="UnixProcessGroup"/>.</summary>
	public static LinuxCgroupV2? TryCreate(ProcessGroupOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (!OperatingSystem.IsLinux())
			return null;
		if (!File.Exists("/sys/fs/cgroup/cgroup.controllers"))
			return null;
		var parent = ReadSelfCgroupParent();
		if (parent is null)
			return null;

		var name = $"processkit-{Environment.ProcessId}-{Interlocked.Increment(ref s_nextId)}";
		var path = Path.Combine(parent, name);
		try
		{
			Directory.CreateDirectory(path);
			return new LinuxCgroupV2(path);
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or PathTooLongException or NotSupportedException)
		{
			// Any directory-creation failure routes back to UnixProcessGroup. The contract is
			// "transparent fallback when delegation isn't granted" — every exception type
			// Directory.CreateDirectory can throw (no permission, read-only fs, exotic chars in
			// the path from /proc/self/cgroup, path too long, etc.) means "no cgroup for us".
			return null;
		}
	}

	public Process StartAndAdd(ProcessStartInfo startInfo)
	{
		var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start process.");
		try
		{
			// Move the freshly-spawned child into our cgroup. See the type-level remark on the
			// micro-window race here — .NET has no pre-exec analogue to Rust's hook.
			TryWriteCgroupProcs(process);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// We failed to place the child in the cgroup (e.g. cgroup directory unwritable). The
			// child is now an uncontained orphan from our group's perspective — best-effort kill so
			// the caller doesn't leak it, then surface the original IO error. Note: ESRCH on a
			// dead-pid write is NOT caught here (TryWriteCgroupProcs already swallows it as a
			// benign exit race).
			try
			{
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
			}
			catch (Exception killEx) when (killEx is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				// process already gone (InvalidOperationException) or OS refused the kill
				// (Win32Exception) — original IO error is the one we want to surface; the
				// cleanup is best-effort.
			}
			process.Dispose();
			throw;
		}
		lock (_lock)
			_processes.Add(process);
		return process;
	}

	public void Add(Process process)
	{
		ArgumentNullException.ThrowIfNull(process);
		// Adopt: write external pid into cgroup.procs. The kernel re-parents it; existing
		// descendants stay outside, future forks join. Matches Rust adopt() semantics.
		// ESRCH-on-dead-pid is silently tolerated as a benign exit race (matches Rust).
		TryWriteCgroupProcs(process);
		lock (_lock)
			_processes.Add(process);
	}

	public void TerminateAll() => Kill();

	public ProcessGroupStats GetStats()
	{
		// Per-process /proc reads (mirrors UnixProcessGroup) — cgroup-level cpu.stat / memory.peak
		// require enabled controllers, which Phase 8 (resource limits) will add.
		Process[] snapshot;
		lock (_lock)
			snapshot = [.. _processes];

		var active = 0;
		var cpu = TimeSpan.Zero;
		long peak = 0;
		foreach (var process in snapshot)
		{
			try
			{
				if (process.HasExited)
					continue;
				process.Refresh();
				active++;
				cpu += process.TotalProcessorTime;
				peak += process.PeakWorkingSet64;
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				// Process gone mid-iteration — natural shutdown race; skip.
			}
		}
		return new ProcessGroupStats(active, cpu, peak);
	}

	public Task SignalAsync(Signal signal, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		// Kill maps to cgroup.kill (atomic). Other signals fall through to per-pid broadcast.
		if (signal == Signal.Kill)
		{
			Kill();
			return Task.CompletedTask;
		}
		Broadcast(SignalNumbers.ToPosix(signal));
		return Task.CompletedTask;
	}

	public Task SignalAsync(CustomSignal signal, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Broadcast(signal.Number);
		return Task.CompletedTask;
	}

	public Task SuspendAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Freeze(frozen: true);
		return Task.CompletedTask;
	}

	public Task ResumeAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Freeze(frozen: false);
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<int>> GetMembersAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IReadOnlyList<int> members = ReadCgroupProcs();
		return Task.FromResult(members);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		Kill();
		WaitUntilEmpty();
		try
		{
			Directory.Delete(_cgroupPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best-effort. The kernel may still be reaping zombies; rmdir returns EBUSY until every
			// member has actually exited (a process leaves cgroup.procs on exit, before reap, so
			// this normally drains within milliseconds). If WaitUntilEmpty's 100 ms budget didn't
			// suffice, the directory persists in /sys/fs/cgroup/... — flag _escalated so the
			// processkit.group.shutdown span records that teardown was incomplete. Operators see a
			// non-zero escalated_to_kill in tracing and can investigate the stuck cgroup.
			Interlocked.Exchange(ref _escalated, 1);
		}
	}

	public ValueTask DisposeAsync()
	{
		Dispose();
		return ValueTask.CompletedTask;
	}

	// --- Private helpers ---

	void TryWriteCgroupProcs(Process process)
	{
		try
		{
			File.WriteAllText(
				Path.Combine(_cgroupPath, "cgroup.procs"),
				process.Id.ToString(CultureInfo.InvariantCulture));
		}
		catch (IOException) when (IsBenignDeadPidWrite(process))
		{
			// The pid is gone (process.HasExited == true). The kernel typically rejects writes
			// to cgroup.procs for dead pids with ESRCH; the underlying File.WriteAllText surfaces
			// it as IOException. We also tolerate other IO failures here (e.g. dir vanished due
			// to a racing Dispose) when the process is verifiably dead — the containment
			// question is moot for a process that has already exited, so swallowing matches the
			// effective semantics even when the errno isn't strictly ESRCH.
		}
	}

	static bool IsBenignDeadPidWrite(Process process)
	{
		try
		{
			return process.HasExited;
		}
		catch (InvalidOperationException)
		{
			// HasExited throws if Process never started or was disposed mid-check; treat as
			// "definitely gone" — the write was definitively against a dead pid.
			return true;
		}
	}

	List<int> ReadCgroupProcs()
	{
		try
		{
			var lines = File.ReadAllLines(Path.Combine(_cgroupPath, "cgroup.procs"));
			var pids = new List<int>(lines.Length);
			foreach (var line in lines)
			{
				if (int.TryParse(line.Trim(), CultureInfo.InvariantCulture, out var pid))
					pids.Add(pid);
			}
			return pids;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// cgroup dir removed (Dispose already ran) → DirectoryNotFoundException (IOException);
			// or transient permission revocation → UnauthorizedAccessException. Both mean "no
			// members observable" — return empty so callers (GetMembersAsync, Kill loop,
			// WaitUntilEmpty) treat as "all gone" rather than fault.
			return [];
		}
	}

	void Kill()
	{
		// Kernel ≥ 5.14: atomic SIGKILL of the whole subtree in one write.
		try
		{
			File.WriteAllText(Path.Combine(_cgroupPath, "cgroup.kill"), "1");
			return;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// IOException covers DirectoryNotFoundException (dir already gone) and
			// FileNotFoundException (kernel < 5.14, cgroup.kill not exposed).
			// UnauthorizedAccessException covers permission revocation. Fall through to the
			// per-pid SIGKILL sweep below; flag _escalated so the processkit.group.shutdown span
			// records that we had to escalate from the preferred atomic path.
		}

		Interlocked.Exchange(ref _escalated, 1);
		// 50 × 2 ms = 100 ms total. Plenty for a forked subtree to drain after SIGKILL — long
		// enough to avoid leaking the cgroup directory, short enough not to hang DisposeAsync.
		for (var i = 0; i < 50; i++)
		{
			var members = ReadCgroupProcs();
			if (members.Count == 0)
				break;
			foreach (var pid in members)
			{
				var rc = kill(pid, SignalNumbers.SIGKILL);
				if (rc != 0)
				{
					// ESRCH (pid gone) is the expected race; we don't track the errno here — the
					// loop re-reads cgroup.procs on the next iteration to converge.
				}
			}
			Thread.Sleep(2);
		}
	}

	void Freeze(bool frozen)
	{
		// Kernel ≥ 5.2: atomic state change. Children added to a frozen cgroup freeze on attach —
		// this is the cgroup-state property that the per-pid fallback below cannot reproduce.
		try
		{
			File.WriteAllText(Path.Combine(_cgroupPath, "cgroup.freeze"), frozen ? "1" : "0");
			return;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Older kernel (cgroup.freeze missing → FileNotFoundException, an IOException
			// derivative), cgroup directory gone (DirectoryNotFoundException), or permission
			// revoked. Fall through to per-pid SIGSTOP/SIGCONT broadcast — note the semantic
			// gap: the fallback signals a SNAPSHOT of cgroup.procs, so any child that forks
			// between the snapshot read and the kernel applying signals will keep running. The
			// atomic cgroup.freeze path does not have this race.
		}

		var signalNumber = frozen ? SignalNumbers.SigStop() : SignalNumbers.SigCont();
		Broadcast(signalNumber);
	}

	void Broadcast(int signalNumber)
	{
		var members = ReadCgroupProcs();
		foreach (var pid in members)
		{
			var rc = kill(pid, signalNumber);
			if (rc != 0)
			{
				// Race with natural exit (ESRCH) is the expected case; nothing to do.
			}
		}
	}

	void WaitUntilEmpty()
	{
		for (var i = 0; i < 50; i++)
		{
			if (ReadCgroupProcs().Count == 0)
				return;
			Thread.Sleep(2);
		}
	}

	[SupportedOSPlatform("linux")]
	static string? ReadSelfCgroupParent()
	{
		try
		{
			// /proc/self/cgroup on v2 is a single line: "0::/path/to/cgroup". v1 has multiple
			// "<id>:<controller>:<path>" lines; we skip those.
			foreach (var line in File.ReadLines("/proc/self/cgroup"))
			{
				if (!line.StartsWith("0::", StringComparison.Ordinal))
					continue;
				var rel = line[3..].Trim();
				if (rel.Length == 0 || rel == "/")
					return "/sys/fs/cgroup";
				return "/sys/fs/cgroup" + rel;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// /proc/self/cgroup may be hidden by seccomp / hidepid mount option (IOException)
			// or denied by chrooted host (UnauthorizedAccessException). Either way, fall through
			// to null so the caller (TryCreate) routes to UnixProcessGroup.
		}
		return null;
	}

	[SupportedOSPlatform("linux")]
	static bool CanCreateSubgroup()
	{
		var parent = ReadSelfCgroupParent();
		if (parent is null)
			return false;
		var probe = Path.Combine(parent,
			string.Create(CultureInfo.InvariantCulture, $"processkit-probe-{Environment.ProcessId}-{Random.Shared.Next()}"));
		try
		{
			Directory.CreateDirectory(probe);
			Directory.Delete(probe);
			return true;
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or PathTooLongException or NotSupportedException)
		{
			// Same family of exceptions Directory.CreateDirectory throws — see TryCreate. Any of
			// them means "no delegation"; caller routes around us to UnixProcessGroup.
			return false;
		}
	}
}
