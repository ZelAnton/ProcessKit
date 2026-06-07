using System.Diagnostics;

namespace ProcessKit.Tests;

/// <summary>
/// In-memory <see cref="IProcessGroupImpl"/> for unit-testing the <see cref="ProcessGroup"/> façade
/// (disposed guards, argument validation, delegation, dispose idempotency, pre-start cancellation)
/// without spawning a real OS process.
/// </summary>
/// <remarks>
/// <see cref="StartAndAdd"/> deliberately throws rather than fabricating a <see cref="Process"/>:
/// the post-start path (<c>RegisterKillOnCancel</c> on a real, sealed <see cref="Process"/>) cannot
/// be faked and stays an integration test — mirroring how the <c>IProcessHandle</c> seam was scoped.
/// Façade tests assert that the guarded paths never reach <see cref="StartAndAdd"/>.
/// </remarks>
sealed class FakeProcessGroupImpl : IProcessGroupImpl
{
	public int StartAndAddCount { get; private set; }
	public int AddCount { get; private set; }
	public Process? LastAddedProcess { get; private set; }
	public int TerminateAllCount { get; private set; }
	public int GetStatsCount { get; private set; }
	public int DisposeCount { get; private set; }
	public int DisposeAsyncCount { get; private set; }
	public int SignalAsyncCount { get; private set; }
	public Signal? LastSignal { get; private set; }
	public int CustomSignalAsyncCount { get; private set; }
	public CustomSignal? LastCustomSignal { get; private set; }
	public int SuspendAsyncCount { get; private set; }
	public int ResumeAsyncCount { get; private set; }
	public int GetMembersAsyncCount { get; private set; }

	public ProcessGroupStats StatsToReturn { get; init; } = new(7, TimeSpan.FromSeconds(1), 2048);

	public IReadOnlyList<int> MembersToReturn { get; init; } = [];

	public Mechanism MechanismToReturn { get; init; } = Mechanism.None;

	public bool EscalatedToKill { get; set; }

	public Mechanism Mechanism => MechanismToReturn;

	public Process StartAndAdd(ProcessStartInfo startInfo)
	{
		StartAndAddCount++;
		throw new InvalidOperationException("FakeProcessGroupImpl.StartAndAdd must not be reached in façade tests.");
	}

	public void Add(Process process) { AddCount++; LastAddedProcess = process; }
	public void TerminateAll() => TerminateAllCount++;
	public ProcessGroupStats GetStats() { GetStatsCount++; return StatsToReturn; }
	public void Dispose() => DisposeCount++;
	public ValueTask DisposeAsync() { DisposeAsyncCount++; return ValueTask.CompletedTask; }

	public Task SignalAsync(Signal signal, CancellationToken cancellationToken)
	{
		SignalAsyncCount++;
		LastSignal = signal;
		return Task.CompletedTask;
	}

	public Task SignalAsync(CustomSignal signal, CancellationToken cancellationToken)
	{
		CustomSignalAsyncCount++;
		LastCustomSignal = signal;
		return Task.CompletedTask;
	}

	public Task SuspendAsync(CancellationToken cancellationToken) { SuspendAsyncCount++; return Task.CompletedTask; }
	public Task ResumeAsync(CancellationToken cancellationToken) { ResumeAsyncCount++; return Task.CompletedTask; }

	public Task<IReadOnlyList<int>> GetMembersAsync(CancellationToken cancellationToken)
	{
		GetMembersAsyncCount++;
		return Task.FromResult(MembersToReturn);
	}
}
