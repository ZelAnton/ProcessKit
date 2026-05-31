using System.Diagnostics;

namespace ProcessKit;

/// <summary>
/// Abstraction over the slice of <see cref="System.Diagnostics.Process"/> the runner depends on.
/// Lets the lifecycle logic in <see cref="ProcessSession"/> be unit-tested against an in-memory
/// fake instead of a real OS process. The real implementation is a thin pass-through; the kernel
/// kill-on-dispose guarantee lives in <see cref="ProcessGroup"/> behind <see cref="IProcessHandleFactory"/>,
/// not here.
/// </summary>
interface IProcessHandle : IDisposable
{
	StreamReader StandardOutput { get; }
	StreamReader StandardError { get; }
	StreamWriter StandardInput { get; }
	event EventHandler Exited;
	bool EnableRaisingEvents { get; set; }
	bool HasExited { get; }
	int ExitCode { get; }
	int Id { get; }
	DateTime StartTime { get; }
	TimeSpan TotalProcessorTime { get; }
	long PeakWorkingSet64 { get; }
	void Refresh();
	void Kill(bool entireProcessTree);
	Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Starts a process and returns an <see cref="IProcessHandle"/>. Contract: the returned process is
/// assigned to <paramref name="group"/> (kill-on-dispose) <em>and</em> wired to be killed when
/// <c>killToken</c> fires — the real factory gets both from <see cref="ProcessGroup.Start"/>; a fake
/// must replicate the kill-on-cancel wiring.
/// </summary>
interface IProcessHandleFactory
{
	IProcessHandle Start(ProcessGroup group, ProcessStartInfo startInfo, CancellationToken killToken);
}

/// <summary>Thin pass-through wrapping a real <see cref="System.Diagnostics.Process"/>.</summary>
sealed class RealProcessHandle(Process process) : IProcessHandle
{
	public StreamReader StandardOutput => process.StandardOutput;
	public StreamReader StandardError => process.StandardError;
	public StreamWriter StandardInput => process.StandardInput;

	public event EventHandler Exited
	{
		add => process.Exited += value;
		remove => process.Exited -= value;
	}

	public bool EnableRaisingEvents
	{
		get => process.EnableRaisingEvents;
		set => process.EnableRaisingEvents = value;
	}

	public bool HasExited => process.HasExited;
	public int ExitCode => process.ExitCode;
	public int Id => process.Id;
	public DateTime StartTime => process.StartTime;
	public TimeSpan TotalProcessorTime => process.TotalProcessorTime;
	public long PeakWorkingSet64 => process.PeakWorkingSet64;
	public void Refresh() => process.Refresh();
	public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
	public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
	public void Dispose() => process.Dispose();
}

/// <summary>
/// Production factory: starts the process through the group (which assigns it to the Job Object /
/// pgid and registers kill-on-cancel against <c>killToken</c>) and wraps the real process. The seam
/// wraps <em>after</em> assignment, so the kill-on-dispose guarantee is untouched.
/// </summary>
sealed class RealProcessHandleFactory : IProcessHandleFactory
{
	public static RealProcessHandleFactory Instance { get; } = new();

	public IProcessHandle Start(ProcessGroup group, ProcessStartInfo startInfo, CancellationToken killToken)
		=> new RealProcessHandle(group.Start(startInfo, killToken));
}
