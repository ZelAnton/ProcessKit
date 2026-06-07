namespace ProcessKit.Tests;

/// <summary>
/// Virtual-time <see cref="IClock"/> for deterministic retry/backoff tests. Records every
/// <see cref="Delay"/> call and advances <see cref="UtcNow"/> without performing any real wait.
/// Honors cancellation so tests can verify mid-backoff cancellation propagation.
/// </summary>
sealed class FakeClock : IClock
{
	public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public List<TimeSpan> RecordedDelays { get; } = [];

	public TimeSpan TotalDelayed
	{
		get
		{
			var total = TimeSpan.Zero;
			foreach (var delay in RecordedDelays)
				total += delay;
			return total;
		}
	}

	public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		RecordedDelays.Add(duration);
		UtcNow = UtcNow.Add(duration);
		return Task.CompletedTask;
	}
}
