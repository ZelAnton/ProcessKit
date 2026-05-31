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
	public int TerminateAllCount { get; private set; }
	public int GetStatsCount { get; private set; }
	public int DisposeCount { get; private set; }
	public int DisposeAsyncCount { get; private set; }

	public ProcessGroupStats StatsToReturn { get; init; } = new(7, TimeSpan.FromSeconds(1), 2048);

	public Process StartAndAdd(ProcessStartInfo startInfo)
	{
		StartAndAddCount++;
		throw new InvalidOperationException("FakeProcessGroupImpl.StartAndAdd must not be reached in façade tests.");
	}

	public void Add(Process process) => AddCount++;
	public void TerminateAll() => TerminateAllCount++;
	public ProcessGroupStats GetStats() { GetStatsCount++; return StatsToReturn; }
	public void Dispose() => DisposeCount++;
	public ValueTask DisposeAsync() { DisposeAsyncCount++; return ValueTask.CompletedTask; }
}
