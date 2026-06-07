using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

// ReSharper disable InconsistentNaming

namespace ProcessKit;

[SupportedOSPlatform("windows")]
static partial class Kernel32
{
	internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

	internal const uint TH32CS_SNAPTHREAD = 0x4;
	internal const uint THREAD_SUSPEND_RESUME = 0x2;
	internal const int ERROR_MORE_DATA = 234;

	internal enum JobObjectInfoClass
	{
		BasicAccountingInformation = 1,
		BasicProcessIdList = 3,
		ExtendedLimitInformation = 9,
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct THREADENTRY32
	{
		public uint dwSize;
		public uint cntUsage;
		public uint th32ThreadID;
		public uint th32OwnerProcessID;
		public int tpBasePri;
		public int tpDeltaPri;
		public uint dwFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nuint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct IO_COUNTERS
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
	{
		public long TotalUserTime;
		public long TotalKernelTime;
		public long ThisPeriodTotalUserTime;
		public long ThisPeriodTotalKernelTime;
		public uint TotalPageFaultCount;
		public uint TotalProcesses;
		public uint ActiveProcesses;
		public uint TotalTerminatedProcesses;
	}

	[LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	internal static partial SafeFileHandle CreateJobObjectW(nint lpJobAttributes, string? lpName);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool SetInformationJobObject(
		SafeFileHandle hJob,
		JobObjectInfoClass infoClass,
		ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
		uint cbJobObjectInformationLength);

	[LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool QueryAccountingInformation(
		SafeFileHandle hJob,
		JobObjectInfoClass infoClass,
		out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,
		uint cbInfoLength,
		nint lpReturnLength);

	[LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool QueryExtendedLimits(
		SafeFileHandle hJob,
		JobObjectInfoClass infoClass,
		out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
		uint cbInfoLength,
		nint lpReturnLength);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool AssignProcessToJobObject(SafeFileHandle hJob, SafeHandle hProcess);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);

	// Variable-size payload (header followed by N ProcessIdList[]); the caller passes a raw buffer
	// and re-tries on ERROR_MORE_DATA. Kept as nint to avoid struct marshalling for the tail.
	[LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool QueryJobMembers(
		SafeFileHandle hJob,
		JobObjectInfoClass infoClass,
		nint lpJobObjectInformation,
		uint cbJobObjectInformationLength,
		out uint lpReturnLength);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	internal static partial SafeFileHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool Thread32First(SafeFileHandle hSnapshot, ref THREADENTRY32 lpte);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool Thread32Next(SafeFileHandle hSnapshot, ref THREADENTRY32 lpte);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	internal static partial SafeFileHandle OpenThread(
		uint dwDesiredAccess,
		[MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
		uint dwThreadId);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	internal static partial uint SuspendThread(SafeFileHandle hThread);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	internal static partial uint ResumeThread(SafeFileHandle hThread);
}
