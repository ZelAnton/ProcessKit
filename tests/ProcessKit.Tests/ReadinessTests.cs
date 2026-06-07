using System.Net;
using System.Net.Sockets;

namespace ProcessKit.Tests;

/// <summary>
/// Integration tests for Phase 3 readiness probes — drive a real <see cref="ProcessRunner"/> and
/// `IRunningProcess` against the OS so the platform code paths (line pump, immutable subscriber
/// list, Task races) are exercised end-to-end. Probes must NOT kill the child on failure.
/// </summary>
public class ReadinessTests
{
	[Test]
	public async Task WaitForLineAsync_MatchesPredicate_ReturnsLineAndLeavesChildRunning()
	{
		await using var process = ProcessRunner.Default.Start(TestExe.PeriodicEcho("tick", 0.05, 30));

		var line = await process.WaitForLineAsync(l => l.Contains("tick", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

		Assert.That(line, Does.Contain("tick"));
		Assert.That(process.Completion.IsCompleted, Is.False, "Probe must not terminate the child.");
	}

	[Test]
	public async Task WaitForLineAsync_DeadlineElapses_ThrowsProcessNotReady_ChildStillAlive()
	{
		await using var process = ProcessRunner.Default.Start(TestExe.Sleep(5));

		// Use a marker the child will never emit — on Windows `Sleep` is `ping`, which is chatty,
		// so a true-match predicate would resolve immediately on ping's banner line.
		var ex = Assert.ThrowsAsync<ProcessNotReadyException>(() =>
			process.WaitForLineAsync(l => l.Contains("phase3-marker-never-emitted", StringComparison.Ordinal),
				TimeSpan.FromMilliseconds(200)));

		Assert.That(ex!.Timeout, Is.EqualTo(TimeSpan.FromMilliseconds(200)));
		Assert.That(process.Completion.IsCompleted, Is.False, "Failed probe must not kill the child.");
	}

	[Test]
	public async Task WaitForLineAsync_ChildExits_ThrowsFastNotReady()
	{
		await using var process = ProcessRunner.Default.Start(TestExe.Echo("hi"));

		// Probe for a line that never appears; with a deadline of 30s, the child exits quickly so
		// we should fail in well under 5s by detecting Completion.
		var swStart = DateTime.UtcNow;
		var ex = Assert.ThrowsAsync<ProcessNotReadyException>(() =>
			process.WaitForLineAsync(l => l.Contains("bye", StringComparison.Ordinal), TimeSpan.FromSeconds(30)));
		var elapsed = DateTime.UtcNow - swStart;

		Assert.That(ex, Is.Not.Null);
		Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), $"Expected fast failure on child exit; took {elapsed}.");
	}

	[Test]
	public async Task WaitForLineAsync_CoexistsWithStdOutEnumeration()
	{
		await using var process = ProcessRunner.Default.Start(TestExe.PeriodicEcho("tick", 0.05, 30));

		// Probe fires in the background; meanwhile the test enumerates StdOut. Both consumers must
		// observe the matching line.
		var probeTask = process.WaitForLineAsync(l => l.Contains("tick", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

		var enumLines = new List<string>();
		await foreach (var line in process.StdOut)
		{
			enumLines.Add(line);
			if (enumLines.Count >= 3)
				break;
		}

		var probeLine = await probeTask;

		Assert.That(probeLine, Does.Contain("tick"));
		Assert.That(enumLines.Any(l => l.Contains("tick", StringComparison.Ordinal)), Is.True,
			"StdOut enumeration must also see the line — probe is a tee, not a consume.");
	}

	[Test]
	public async Task WaitForAsync_PassesOnceCheckTurnsTrue()
	{
		await using var process = ProcessRunner.Default.Start(TestExe.Sleep(5));

		var callCount = 0;
		await process.WaitForAsync(
			check: _ =>
			{
				callCount++;
				return Task.FromResult(callCount >= 3);
			},
			within: TimeSpan.FromSeconds(2),
			poll: TimeSpan.FromMilliseconds(20));

		Assert.That(callCount, Is.GreaterThanOrEqualTo(3));
	}

	[Test]
	public async Task WaitForAsync_ChildExitsBeforeCheckPasses_ThrowsNotReady()
	{
		await using var process = ProcessRunner.Default.Start(TestExe.Echo("hi"));

		var swStart = DateTime.UtcNow;
		var ex = Assert.ThrowsAsync<ProcessNotReadyException>(() =>
			process.WaitForAsync(_ => Task.FromResult(false), TimeSpan.FromSeconds(30)));
		var elapsed = DateTime.UtcNow - swStart;

		Assert.That(ex, Is.Not.Null);
		Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
	}

	[Test]
	public async Task WaitForPortAsync_SucceedsAgainstLateListener()
	{
		// Allocate a TcpListener and bind via the underlying socket — TcpListener's constructor
		// does NOT bind (it binds on Start). We need the port reserved BEFORE Start so the kernel
		// rejects probe connects with ECONNREFUSED until the test calls Listen() on the delayed
		// path; the unused (Loopback, 0) ctor args are only for shape.
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
		var endpoint = (IPEndPoint)listener.Server.LocalEndPoint!;

		await using var process = ProcessRunner.Default.Start(TestExe.Sleep(5));

		var acceptedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var listenerTask = Task.Run(async () =>
		{
			await Task.Delay(300);
			listener.Server.Listen(8);
			try
			{
				using var client = await listener.Server.AcceptAsync();
				acceptedTcs.TrySetResult();
			}
			catch
			{
				// Accept failed — typically because the test torn down the listener via Close()
				// while we were awaiting (SocketException) or the listener was already disposed
				// (ObjectDisposedException). Signal completion regardless so the main test path
				// doesn't deadlock waiting for the helper task; the probe success is what's
				// actually being asserted.
				acceptedTcs.TrySetResult();
			}
		});

		await process.WaitForPortAsync(endpoint, TimeSpan.FromSeconds(5));

		// Allow the listener task to finish or unblock; closing the socket below also cancels accept.
		listener.Server.Close();
		await Task.WhenAny(acceptedTcs.Task, Task.Delay(1000));
	}

	[Test]
	public async Task WaitForPortAsync_NoListener_ThrowsNotReady()
	{
		// Bind a TcpListener to reserve an ephemeral port, but DO NOT Start it — the kernel rejects
		// connects to a non-listening port with ECONNREFUSED. Probe deadline 300ms.
		using var reservation = new TcpListener(IPAddress.Loopback, 0);
		reservation.Server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
		var endpoint = (IPEndPoint)reservation.Server.LocalEndPoint!;

		await using var process = ProcessRunner.Default.Start(TestExe.Sleep(5));

		Assert.ThrowsAsync<ProcessNotReadyException>(() =>
			process.WaitForPortAsync(endpoint, TimeSpan.FromMilliseconds(300)));
	}
}
