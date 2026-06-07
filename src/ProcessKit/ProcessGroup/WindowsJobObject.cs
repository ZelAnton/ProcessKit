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

	public bool EscalatedToKill => false;

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
}
