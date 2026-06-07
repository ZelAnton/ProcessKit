using System.Diagnostics;

namespace ProcessKit.Tests;

/// <summary>
/// Integration tests for Phase 2 process-control surface (Signal/Suspend/Resume/Members/Adopt) —
/// drive a real <see cref="ProcessGroup"/> against the OS so platform branches in
/// <see cref="WindowsJobObject"/> / <see cref="UnixProcessGroup"/> are exercised end-to-end.
/// </summary>
public class ProcessGroupSignalTests
{
	[Test]
	public void Mechanism_ReportsPlatformBackend()
	{
		using var group = new ProcessGroup();

		var expected = OperatingSystem.IsWindows() ? Mechanism.JobObject : Mechanism.ProcessGroup;
		Assert.That(group.Mechanism, Is.EqualTo(expected));
	}

	[Test]
	public async Task SignalAsync_OnWindows_NonKill_ThrowsPlatformNotSupported()
	{
		Assume.That(OperatingSystem.IsWindows(), "Windows-only path: only Signal.Kill maps to TerminateJobObject.");

		using var group = new ProcessGroup();

		Assert.ThrowsAsync<PlatformNotSupportedException>(() => group.SignalAsync(Signal.Term));
		Assert.ThrowsAsync<PlatformNotSupportedException>(() => group.SignalAsync(new CustomSignal(15)));
		await Task.CompletedTask;
	}

	[Test]
	public async Task SignalAsync_KillTerminatesGroup()
	{
		using var group = new ProcessGroup();
		var process = group.Start(TestExe.Sleep(10));

		await group.SignalAsync(Signal.Kill);

		Assert.That(process.WaitForExit(5000), Is.True, "Kill signal should terminate the process within the timeout.");
	}

	[Test]
	public async Task SignalAsync_OnUnix_TermDeliversToTree()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only: Windows can't deliver SIGTERM.");

		using var group = new ProcessGroup();
		var process = group.Start(TestExe.Sleep(10));

		await group.SignalAsync(Signal.Term);

		// Default SIGTERM handler exits — give it a generous window for a slow CI runner.
		Assert.That(process.WaitForExit(5000), Is.True);
	}

	[Test]
	public async Task SuspendResume_FreezesThenUnfreezesPeriodicEcho()
	{
		using var group = new ProcessGroup();
		// 30 iterations × 50ms ≈ 1.5 s of output if never suspended.
		var process = group.Start(TestExe.PeriodicEcho("tick", intervalSeconds: 0.05, count: 30));
		using var reader = process.StandardOutput;

		// Allow the child to start emitting. A generous settle window lets any startup buffering
		// drain BEFORE we suspend, so the post-suspend stillness check sees only the freeze, not
		// residual writes that hadn't reached us yet.
		await Task.Delay(500);

		await group.SuspendAsync();

		// Drain whatever was in flight before the freeze, then sleep through the freeze window so
		// any pipe-level buffering has time to surface what was already written.
		var beforeFreeze = await DrainAvailableLinesAsync(reader);
		await Task.Delay(500);
		var afterFreezeWindow = await DrainAvailableLinesAsync(reader);
		Assert.That(afterFreezeWindow, Is.Empty,
			$"No lines should arrive while suspended. Got: {string.Join(",", afterFreezeWindow)}. Pre-freeze drain: {beforeFreeze.Count}.");

		await group.ResumeAsync();

		// After resume, new lines should appear.
		var deadline = DateTime.UtcNow.AddSeconds(3);
		while (DateTime.UtcNow < deadline)
		{
			var more = await DrainAvailableLinesAsync(reader);
			if (more.Count > 0)
				return;
			await Task.Delay(50);
		}
		Assert.Fail("Expected at least one line after Resume but none arrived within 3 s.");
	}

	[Test]
	public async Task GetMembersAsync_EmptyGroup_ReturnsEmpty()
	{
		using var group = new ProcessGroup();

		var members = await group.GetMembersAsync();

		Assert.That(members, Is.Empty);
	}

	[Test]
	public async Task GetMembersAsync_AfterStart_IncludesProcessPid()
	{
		using var group = new ProcessGroup();
		var process = group.Start(TestExe.Sleep(5));

		var members = await group.GetMembersAsync();

		Assert.That(members, Does.Contain(process.Id));
	}

	[Test]
	public async Task AdoptAsync_BringsExternalProcessUnderContainment()
	{
		using var group = new ProcessGroup();

		// Start the process OUTSIDE the group — it is not yet a member.
		var external = Process.Start(TestExe.Sleep(15))
			?? throw new InvalidOperationException("Process.Start returned null.");

		try
		{
			await group.AdoptAsync(external);

			var members = await group.GetMembersAsync();
			Assert.That(members, Does.Contain(external.Id));
		}
		finally
		{
			// Best-effort cleanup if the test threw before group.Dispose killed it.
			try
			{
				if (!external.HasExited)
					external.Kill(entireProcessTree: true);
			}
			catch
			{
				// Process already exited or was disposed concurrently — the goal here is "gone",
				// which is exactly the current state. Nothing to surface.
			}
			external.Dispose();
		}
	}

	static async Task<List<string>> DrainAvailableLinesAsync(StreamReader reader)
	{
		var lines = new List<string>();
		// The Process stream is line-buffered by the child, but we read non-blocking by checking
		// Peek/ReadLineAsync with a short cancellation. Use ReadLineAsync with a per-line timeout
		// to avoid hanging if no output is buffered.
		while (true)
		{
			using var cts = new CancellationTokenSource(50);
			try
			{
				var line = await reader.ReadLineAsync(cts.Token);
				if (line is null)
					break;
				lines.Add(line);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
		return lines;
	}
}
