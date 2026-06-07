using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static ProcessKit.Kernel32;

namespace ProcessKit;

[SupportedOSPlatform("windows")]
sealed class WindowsJobObject : IProcessGroupImpl
{
	readonly SafeFileHandle _jobHandle;

	// options is accepted for parity with UnixProcessGroup but intentionally unused: a Job Object
	// with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE terminates all members atomically when the handle is
	// closed, so there is no SIGTERM-style grace window to honor and no soft-signal to escalate from.
	public WindowsJobObject(ProcessGroupOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		_jobHandle = CreateJobObjectW(nint.Zero, null);
		if (_jobHandle.IsInvalid)
			throw new Win32Exception(Marshal.GetLastWin32Error());

		var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
		{
			BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
			{
				LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
			},
		};

		var len = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
		if (!SetInformationJobObject(_jobHandle, JobObjectInfoClass.ExtendedLimitInformation, ref info, len))
			throw new Win32Exception(Marshal.GetLastWin32Error());
	}

	public Mechanism Mechanism => Mechanism.JobObject;

	public bool EscalatedToKill => false;

	public Process StartAndAdd(ProcessStartInfo startInfo)
	{
		var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start process.");
		try
		{
			AssignToJob(process);
			return process;
		}
		catch
		{
			KillAndDispose(process);
			throw;
		}
	}

	public void Add(Process process) => AssignToJob(process);

	public void TerminateAll()
	{
		if (!TerminateJobObject(_jobHandle, 1))
			throw new Win32Exception(Marshal.GetLastWin32Error());
	}

	public ProcessGroupStats GetStats()
	{
		var accountingSize = (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
		if (!QueryAccountingInformation(
				_jobHandle,
				JobObjectInfoClass.BasicAccountingInformation,
				out var accounting,
				accountingSize,
				nint.Zero))
			throw new Win32Exception(Marshal.GetLastWin32Error());

		var limitsSize = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
		if (!QueryExtendedLimits(
				_jobHandle,
				JobObjectInfoClass.ExtendedLimitInformation,
				out var limits,
				limitsSize,
				nint.Zero))
			throw new Win32Exception(Marshal.GetLastWin32Error());

		var cpuTicks = accounting.TotalUserTime + accounting.TotalKernelTime;
		return new ProcessGroupStats(
			ActiveProcessCount: (int)accounting.ActiveProcesses,
			TotalCpuTime: TimeSpan.FromTicks(cpuTicks),
			PeakMemoryBytes: (long)limits.PeakJobMemoryUsed);
	}

	public Task SignalAsync(Signal signal, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (signal != Signal.Kill)
			throw new PlatformNotSupportedException(
				$"Signal.{signal} is not supported on Windows; only Signal.Kill maps to TerminateJobObject.");

		if (!TerminateJobObject(_jobHandle, 1))
			throw new Win32Exception(Marshal.GetLastWin32Error());

		return Task.CompletedTask;
	}

	public Task SignalAsync(CustomSignal signal, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		throw new PlatformNotSupportedException(
			"Raw POSIX signals are not supported on Windows; use Signal.Kill or SuspendAsync/ResumeAsync.");
	}

	public Task SuspendAsync(CancellationToken cancellationToken) => ForEachMemberThread(suspend: true, cancellationToken);

	public Task ResumeAsync(CancellationToken cancellationToken) => ForEachMemberThread(suspend: false, cancellationToken);

	public Task<IReadOnlyList<int>> GetMembersAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IReadOnlyList<int> members = QueryMemberPids();
		return Task.FromResult(members);
	}

	public void Dispose() => _jobHandle.Dispose();

	public ValueTask DisposeAsync()
	{
		_jobHandle.Dispose();
		return ValueTask.CompletedTask;
	}

	void AssignToJob(Process process)
	{
		if (!AssignProcessToJobObject(_jobHandle, process.SafeHandle))
			throw new Win32Exception(Marshal.GetLastWin32Error());
	}

	static void KillAndDispose(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Best-effort cleanup after AssignProcessToJobObject failed. Kill throws
			// InvalidOperationException if the process already exited or Win32Exception if the OS
			// refuses — we still dispose below and rethrow the original assignment failure regardless.
		}

		process.Dispose();
	}

	List<int> QueryMemberPids()
	{
		// JOBOBJECT_BASIC_PROCESS_ID_LIST is a variable-size struct: header (NumberOfAssignedProcesses
		// + NumberOfProcessIdsInList) followed by a flexible array of UIntPtr-sized PIDs. We start
		// with capacity for 32 PIDs and grow on ERROR_MORE_DATA, matching the Rust impl's strategy.
		// Capacity caps at 1<<20 (~1M PIDs) — well above any realistic job size; prevents an unbounded
		// loop if the OS keeps returning ERROR_MORE_DATA against a fork-bomb of growing membership.
		const int MaxCapacity = 1 << 20;
		var capacity = 32;
		while (true)
		{
			var headerBytes = sizeof(uint) * 2;
			var bufferBytes = headerBytes + capacity * nuint.Size;
			var buffer = Marshal.AllocHGlobal(bufferBytes);
			try
			{
				if (QueryJobMembers(
						_jobHandle,
						JobObjectInfoClass.BasicProcessIdList,
						buffer,
						(uint)bufferBytes,
						out _))
				{
					var assigned = (int)(uint)Marshal.ReadInt32(buffer);
					var inList = (int)(uint)Marshal.ReadInt32(buffer + sizeof(uint));
					var pids = new List<int>(inList);
					var listStart = buffer + headerBytes;
					for (var i = 0; i < inList; i++)
					{
						var pid = nuint.Size == 8
							? (int)(uint)Marshal.ReadInt64(listStart + i * nuint.Size)
							: Marshal.ReadInt32(listStart + i * nuint.Size);
						pids.Add(pid);
					}
					_ = assigned;  // header value; unused — inList is the count actually filled in.
					return pids;
				}

				var err = Marshal.GetLastWin32Error();
				if (err != ERROR_MORE_DATA)
					throw new Win32Exception(err);
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}

			// Re-try with a larger buffer. Double each round; the Job Object rarely holds more than
			// a handful of processes, so a few growth iterations suffice. Cap at MaxCapacity to keep
			// the loop bounded even against pathological membership growth.
			if (capacity >= MaxCapacity)
				throw new InvalidOperationException(
					$"QueryJobMembers still reported ERROR_MORE_DATA after capacity reached {MaxCapacity} PIDs — refusing to grow further.");
			capacity *= 2;
		}
	}

	Task ForEachMemberThread(bool suspend, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var memberPids = new HashSet<uint>(QueryMemberPids().Select(p => (uint)p));
		if (memberPids.Count == 0)
			return Task.CompletedTask;

		using var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
		if (snapshot.IsInvalid)
			throw new Win32Exception(Marshal.GetLastWin32Error());

		var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
		if (!Thread32First(snapshot, ref entry))
		{
			var err = Marshal.GetLastWin32Error();
			// ERROR_NO_MORE_FILES (18) means the snapshot is empty — nothing to do.
			if (err == 18)
				return Task.CompletedTask;
			throw new Win32Exception(err);
		}

		do
		{
			if (!memberPids.Contains(entry.th32OwnerProcessID))
				continue;

			using var thread = OpenThread(THREAD_SUSPEND_RESUME, false, entry.th32ThreadID);
			if (thread.IsInvalid)
			{
				// The thread may have exited between snapshot and OpenThread — race; skip it.
				// Other errors (ACCESS_DENIED on protected threads) are non-fatal best-effort too.
				continue;
			}

			var result = suspend ? SuspendThread(thread) : ResumeThread(thread);
			if (result == unchecked((uint)-1))
			{
				// Thread died between OpenThread and Suspend/ResumeThread; same race-skip as above.
				continue;
			}
		}
		while (Thread32Next(snapshot, ref entry));

		return Task.CompletedTask;
	}
}
