using System.Diagnostics;

namespace ProcessKit;

interface IProcessGroupImpl : IDisposable, IAsyncDisposable
{
	Process StartAndAdd(ProcessStartInfo startInfo);
	void Add(Process process);
	void TerminateAll();
	ProcessGroupStats GetStats();

	/// <summary>The containment mechanism this implementation uses.</summary>
	Mechanism Mechanism { get; }

	/// <summary>Delivers a canonical signal to every member. Throws
	/// <see cref="PlatformNotSupportedException"/> when the impl can't deliver it (Windows: only
	/// <see cref="Signal.Kill"/>).</summary>
	Task SignalAsync(Signal signal, CancellationToken cancellationToken);

	/// <summary>Delivers a raw POSIX signal to every member. Unix-only.</summary>
	Task SignalAsync(CustomSignal signal, CancellationToken cancellationToken);

	/// <summary>Pauses every member. Unix: <c>SIGSTOP</c>. Windows: <c>SuspendThread</c> per thread
	/// of every Job Object member (suspend counts stack).</summary>
	Task SuspendAsync(CancellationToken cancellationToken);

	/// <summary>Resumes every member. Mirror of <see cref="SuspendAsync"/>.</summary>
	Task ResumeAsync(CancellationToken cancellationToken);

	/// <summary>Snapshot of live member PIDs.</summary>
	Task<IReadOnlyList<int>> GetMembersAsync(CancellationToken cancellationToken);

	/// <summary>
	/// True after teardown if shutdown had to escalate from SIGTERM to SIGKILL. Always false on
	/// Windows (Job Object terminates members atomically; there is no soft-signal grace window).
	/// Read after <see cref="IDisposable.Dispose"/> / <see cref="IAsyncDisposable.DisposeAsync"/>;
	/// undefined while still running.
	/// </summary>
	bool EscalatedToKill { get; }
}
