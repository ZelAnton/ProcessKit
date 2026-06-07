using System.Diagnostics;

namespace ProcessKit;

interface IProcessGroupImpl : IDisposable, IAsyncDisposable
{
	Process StartAndAdd(ProcessStartInfo startInfo);
	void Add(Process process);
	void TerminateAll();
	ProcessGroupStats GetStats();

	/// <summary>
	/// True after teardown if shutdown had to escalate from SIGTERM to SIGKILL. Always false on
	/// Windows (Job Object terminates members atomically; there is no soft-signal grace window).
	/// Read after <see cref="IDisposable.Dispose"/> / <see cref="IAsyncDisposable.DisposeAsync"/>;
	/// undefined while still running.
	/// </summary>
	bool EscalatedToKill { get; }
}
