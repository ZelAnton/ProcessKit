using System.Collections.Concurrent;
using System.Diagnostics;
using ProcessKit.Diagnostics;

namespace ProcessKit.Tests;

/// <summary>
/// Tests for Phase 6 retry semantics. Injects a <see cref="FakeClock"/> via the internal
/// <see cref="Command.Clock"/> init property (accessible via the existing
/// <c>InternalsVisibleTo("ProcessKit.Tests")</c>) so backoff schedules are asserted deterministically
/// without real waits.
/// </summary>
public class RetryTests
{
	[Test]
	public void NoPolicy_RunsOnceAndPropagatesFailure()
	{
		var clock = new FakeClock();
		var cmd = WithClock(BuildCommand(TestExe.ExitWith(1)), clock);

		Assert.ThrowsAsync<ProcessExitException>(() => cmd.RunAsync());
		Assert.That(clock.RecordedDelays, Is.Empty, "No policy → no delays.");
	}

	[Test]
	public void Retries_UntilMaxAttempts()
	{
		var clock = new FakeClock();
		var cmd = WithClock(BuildCommand(TestExe.ExitWith(1)), clock)
			.WithRetry(new RetryPolicy(3, TimeSpan.FromMilliseconds(50)) { Jitter = false, RetryIf = _ => true });

		Assert.ThrowsAsync<ProcessExitException>(() => cmd.RunAsync());
		Assert.That(clock.RecordedDelays.Count, Is.EqualTo(2),
			"MaxAttempts=3 → 2 delays between 3 attempts.");
	}

	[Test]
	public void StopsWhenRetryIfRejects()
	{
		var clock = new FakeClock();
		var cmd = WithClock(BuildCommand(TestExe.ExitWith(1)), clock)
			.WithRetry(new RetryPolicy(5, TimeSpan.FromMilliseconds(50)) { Jitter = false, RetryIf = _ => false });

		Assert.ThrowsAsync<ProcessExitException>(() => cmd.RunAsync());
		Assert.That(clock.RecordedDelays, Is.Empty, "Classifier rejects → 1 attempt only.");
	}

	[Test]
	public async Task DefaultRetryIf_DoesNotRetryOnCancellation()
	{
		var clock = new FakeClock();
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		var cmd = WithClock(BuildCommand(TestExe.Sleep(5)), clock)
			.WithCancellation(cts.Token)
			.WithRetry(new RetryPolicy(5, TimeSpan.FromMilliseconds(50)) { Jitter = false });

		Assert.ThrowsAsync<ProcessCancelledException>(() => cmd.RunAsync());
		Assert.That(clock.RecordedDelays, Is.Empty, "Cancellation is terminal — no retry sleep.");
	}

	[Test]
	public async Task CancellationTerminal_EvenWhenRetryIfReturnsTrue()
	{
		var clock = new FakeClock();
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		var cmd = WithClock(BuildCommand(TestExe.Sleep(5)), clock)
			.WithCancellation(cts.Token)
			.WithRetry(new RetryPolicy(5, TimeSpan.FromMilliseconds(50))
			{
				Jitter = false,
				RetryIf = _ => true,  // even an "always retry" classifier doesn't override OCE.
			});

		Assert.ThrowsAsync<ProcessCancelledException>(() => cmd.RunAsync());
		Assert.That(clock.RecordedDelays, Is.Empty);
	}

	[Test]
	public void Backoff_FollowsExponentialSchedule()
	{
		var clock = new FakeClock();
		var cmd = WithClock(BuildCommand(TestExe.ExitWith(1)), clock)
			.WithRetry(new RetryPolicy(4, TimeSpan.FromMilliseconds(100))
			{
				BackoffFactor = 2.0,
				Jitter = false,
				RetryIf = _ => true,
			});

		Assert.ThrowsAsync<ProcessExitException>(() => cmd.RunAsync());
		Assert.That(clock.RecordedDelays, Is.EqualTo((TimeSpan[])
		[
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(200),
			TimeSpan.FromMilliseconds(400),
		]));
	}

	[Test]
	public void MaxBackoff_CapsExponentialGrowth()
	{
		var clock = new FakeClock();
		var cmd = WithClock(BuildCommand(TestExe.ExitWith(1)), clock)
			.WithRetry(new RetryPolicy(5, TimeSpan.FromMilliseconds(100))
			{
				BackoffFactor = 10.0,
				MaxBackoff = TimeSpan.FromMilliseconds(300),
				Jitter = false,
				RetryIf = _ => true,
			});

		Assert.ThrowsAsync<ProcessExitException>(() => cmd.RunAsync());
		Assert.That(clock.RecordedDelays, Is.EqualTo((TimeSpan[])
		[
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(300),
			TimeSpan.FromMilliseconds(300),
			TimeSpan.FromMilliseconds(300),
		]));
	}

	[Test]
	public void Jitter_DelaysFallWithinExpectedRange()
	{
		var clock = new FakeClock();
		const int basMs = 200;
		var cmd = WithClock(BuildCommand(TestExe.ExitWith(1)), clock)
			.WithRetry(new RetryPolicy(3, TimeSpan.FromMilliseconds(basMs))
			{
				BackoffFactor = 1.0,  // disable exponential growth for predictable bounds
				Jitter = true,
				RetryIf = _ => true,
			});

		Assert.ThrowsAsync<ProcessExitException>(() => cmd.RunAsync());
		foreach (var delay in clock.RecordedDelays)
			Assert.That(delay.TotalMilliseconds, Is.InRange(basMs * 0.5, basMs * 1.5));
	}

	[Test]
	public void OneShotStdin_ThrowsInvalidOperation()
	{
		var clock = new FakeClock();
		using var memStream = new MemoryStream([1, 2, 3]);
		var cmd = WithClock(BuildCommand(TestExe.ExitWith(0)), clock)
			.WithStandardInput(StandardInput.FromStream(memStream))
			.WithRetry(new RetryPolicy(3, TimeSpan.FromMilliseconds(50)) { Jitter = false });

		Assert.ThrowsAsync<InvalidOperationException>(() => cmd.RunAsync());
	}

	[Test]
	public void EmitsActivitySpansPerAttempt()
	{
		var activities = new ConcurrentBag<Activity>();
		using var listener = new ActivityListener
		{
			ShouldListenTo = src => src.Name == ProcessKitActivitySource.Name,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
			ActivityStopped = activities.Add,
		};
		ActivitySource.AddActivityListener(listener);

		var clock = new FakeClock();
		var cmd = WithClock(BuildCommand(TestExe.ExitWith(1)), clock)
			.WithRetry(new RetryPolicy(3, TimeSpan.FromMilliseconds(50)) { Jitter = false, RetryIf = _ => true });

		Assert.ThrowsAsync<ProcessExitException>(() => cmd.RunAsync());

		var retrySpans = activities
			.Where(a => a.OperationName == "processkit.retry.attempt")
			.OrderBy(a => (int)(a.GetTagItem("attempt") ?? 0))
			.ToList();
		Assert.That(retrySpans, Has.Count.EqualTo(3));
		for (var i = 0; i < retrySpans.Count; i++)
			Assert.That(retrySpans[i].GetTagItem("attempt"), Is.EqualTo(i + 1));
	}

	// --- Helpers -----------------------------------------------------------------------------

	static Command BuildCommand(ProcessStartInfo psi)
	{
		var cmd = Command.Create(psi.FileName);
		if (psi.ArgumentList.Count > 0)
			cmd = cmd.Args([.. psi.ArgumentList]);
		if (psi.CreateNoWindow)
			cmd = cmd.WithCreateNoWindow();
		return cmd;
	}

	static Command WithClock(Command cmd, IClock clock) => cmd with { Clock = clock };
}
