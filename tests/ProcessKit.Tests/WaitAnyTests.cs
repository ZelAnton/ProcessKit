namespace ProcessKit.Tests;

/// <summary>
/// Tests for the Phase 4 <c>WaitAnyAsync</c> extension over <see cref="IEnumerable{IRunningProcess}"/>.
/// Verifies index reporting, exit-code extraction, loser preservation, and cancellation semantics.
/// </summary>
public class WaitAnyTests
{
	[Test]
	public void WaitAnyAsync_Empty_ThrowsArgumentException()
	{
		Assert.ThrowsAsync<ArgumentException>(() => ((IRunningProcess[])[]).WaitAnyAsync());
	}

	[Test]
	public async Task WaitAnyAsync_SingleProcess_ReturnsZeroAndExitCode()
	{
		await using var process = ProcessRunner.Default.Start(TestExe.ExitWith(5));
		var (index, code) = await new[] { process }.WaitAnyAsync();
		Assert.That(index, Is.Zero);
		Assert.That(code, Is.EqualTo(5));
	}

	[Test]
	public async Task WaitAnyAsync_FastestFirst()
	{
		// Two sleepers: the first is fast (0.1s), the second is slow (5s). The fast one must win.
		await using var fast = ProcessRunner.Default.Start(TestExe.Sleep(0.1));
		await using var slow = ProcessRunner.Default.Start(TestExe.Sleep(5));

		var (index, code) = await new[] { fast, slow }.WaitAnyAsync();
		Assert.That(index, Is.Zero, "The 0.1s sleeper must win.");
		Assert.That(code, Is.Zero);
	}

	[Test]
	public async Task WaitAnyAsync_LosersRemainUsable()
	{
		await using var fast = ProcessRunner.Default.Start(TestExe.Sleep(0.1));
		await using var slow = ProcessRunner.Default.Start(TestExe.Sleep(5));

		await new[] { fast, slow }.WaitAnyAsync();

		// The loser's Completion is still awaitable (we dispose slow at scope end, which kills it).
		// Verify by reading its Pid — that property must remain valid post-race.
		Assert.That(slow.Pid, Is.GreaterThan(0));
	}

	[Test]
	public async Task WaitAnyAsync_PreCancelledToken_ThrowsOperationCanceled()
	{
		await using var process = ProcessRunner.Default.Start(TestExe.Sleep(5));
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		Assert.ThrowsAsync<OperationCanceledException>(() => new[] { process }.WaitAnyAsync(cts.Token));
	}
}
