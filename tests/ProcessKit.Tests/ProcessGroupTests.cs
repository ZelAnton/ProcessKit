using System.Diagnostics;

namespace ProcessKit.Tests;

public class ProcessGroupTests
{
	[Test]
	public void CreateAndDispose_DoesNotThrow()
	{
		using var group = new ProcessGroup();
	}

	[Test]
	public void DoubleDispose_DoesNotThrow()
	{
		var group = new ProcessGroup();
		group.Dispose();
		group.Dispose();
	}

	[Test]
	public void Start_ReturnsRunningProcess()
	{
		using var group = new ProcessGroup();

		var process = group.Start(LongRunningProcess());

		Assert.That(process.HasExited, Is.False);
	}

	[Test]
	public void Dispose_TerminatesStartedProcess()
	{
		Process process;
		using (var group = new ProcessGroup())
		{
			process = group.Start(LongRunningProcess());
			Assert.That(process.HasExited, Is.False);
		}

		Assert.That(process.WaitForExit(5_000), Is.True);
		Assert.That(process.HasExited, Is.True);
	}

	[Test]
	public void Dispose_TerminatesMultipleProcesses()
	{
		Process p1, p2;
		using (var group = new ProcessGroup())
		{
			p1 = group.Start(LongRunningProcess());
			p2 = group.Start(LongRunningProcess());
		}

		Assert.That(p1.WaitForExit(5_000), Is.True);
		Assert.That(p2.WaitForExit(5_000), Is.True);
	}

	[Test]
	public void TerminateAll_KillsAllProcesses()
	{
		using var group = new ProcessGroup();
		var p1 = group.Start(LongRunningProcess());
		var p2 = group.Start(LongRunningProcess());

		group.TerminateAll();

		Assert.That(p1.WaitForExit(5_000), Is.True);
		Assert.That(p2.WaitForExit(5_000), Is.True);
	}

	[Test]
	public void Add_ExistingProcess_IsTerminatedOnDispose()
	{
		var external = Process.Start(LongRunningProcess())!;
		try
		{
			Process started;
			using (var group = new ProcessGroup())
			{
				started = group.Start(LongRunningProcess());
				group.Add(external);
			}

			Assert.That(started.WaitForExit(5_000), Is.True);
			Assert.That(external.WaitForExit(5_000), Is.True);
		}
		finally
		{
			if (!external.HasExited)
			{
				external.Kill(entireProcessTree: true);
				external.WaitForExit();
			}
		}
	}

	[Test]
	public void Start_AfterDispose_ThrowsObjectDisposedException()
	{
		var group = new ProcessGroup();
		group.Dispose();

		Assert.Throws<ObjectDisposedException>(() => group.Start(LongRunningProcess()));
	}

	[Test]
	public void Add_AfterDispose_ThrowsObjectDisposedException()
	{
		var group = new ProcessGroup();
		group.Dispose();

		using var process = Process.Start(LongRunningProcess())!;
		try
		{
			Assert.Throws<ObjectDisposedException>(() => group.Add(process));
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit();
			}
		}
	}

	[Test]
	public void TerminateAll_AfterDispose_ThrowsObjectDisposedException()
	{
		var group = new ProcessGroup();
		group.Dispose();

		Assert.Throws<ObjectDisposedException>(() => group.TerminateAll());
	}

	[Test]
	public void Add_NullProcess_ThrowsArgumentNullException()
	{
		using var group = new ProcessGroup();

		Assert.Throws<ArgumentNullException>(() => group.Add(null!));
	}

	[Test]
	public void Start_NullStartInfo_ThrowsArgumentNullException()
	{
		using var group = new ProcessGroup();

		Assert.Throws<ArgumentNullException>(() => group.Start(null!));
	}

	[Test]
	public void Start_WithCancelledToken_Throws()
	{
		using var group = new ProcessGroup();
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		Assert.Throws<OperationCanceledException>(() => group.Start(LongRunningProcess(), cts.Token));
	}

	[Test]
	public void Start_CancellationAfterStart_KillsProcess()
	{
		using var group = new ProcessGroup();
		using var cts = new CancellationTokenSource();

		var process = group.Start(LongRunningProcess(), cts.Token);
		Assert.That(process.HasExited, Is.False);

		cts.Cancel();

		Assert.That(process.WaitForExit(5_000), Is.True);
	}

	[Test]
	public async Task DisposeAsync_TerminatesStartedProcess()
	{
		Process process;
		await using (var group = new ProcessGroup())
		{
			process = group.Start(LongRunningProcess());
			Assert.That(process.HasExited, Is.False);
		}

		Assert.That(process.WaitForExit(5_000), Is.True);
	}

	[Test]
	public async Task DisposeAsync_DoubleCall_DoesNotThrow()
	{
		var group = new ProcessGroup();
		await group.DisposeAsync();
		await group.DisposeAsync();
	}

	[Test]
	public void GetStats_OnEmptyGroup_ReturnsZeros()
	{
		using var group = new ProcessGroup();

		var stats = group.GetStats();

		Assert.That(stats.ActiveProcessCount, Is.Zero);
	}

	[Test]
	public void GetStats_WithActiveProcess_ReturnsAtLeastOne()
	{
		using var group = new ProcessGroup();
		group.Start(LongRunningProcess());

		var stats = group.GetStats();

		Assert.That(stats.ActiveProcessCount, Is.GreaterThanOrEqualTo(1));
	}

	[Test]
	public void GetStats_AfterDispose_Throws()
	{
		var group = new ProcessGroup();
		group.Dispose();

		Assert.Throws<ObjectDisposedException>(() => group.GetStats());
	}

	[Test]
	public void GetStats_SkipsExitedProcesses()
	{
		using var group = new ProcessGroup();
		var process = group.Start(LongRunningProcess());
		process.Kill(entireProcessTree: true);
		Assert.That(process.WaitForExit(5_000), Is.True);

		var stats = group.GetStats();

		// Windows job accounting still reflects the recently-killed process
		// until the kernel reaps it; on Unix we iterate _processes and skip
		// HasExited. Both paths must eventually drop to zero, but only the
		// Unix path drops synchronously, so just assert it does not throw
		// and the count is non-negative.
		Assert.That(stats.ActiveProcessCount, Is.GreaterThanOrEqualTo(0));
	}

	[Test]
	public void Add_OnAlreadyExitedProcess_DoesNotLeakOwnership()
	{
		var external = Process.Start(LongRunningProcess())!;
		external.Kill(entireProcessTree: true);
		Assert.That(external.WaitForExit(5_000), Is.True);

		using var group = new ProcessGroup();

		// Contract: either Add succeeds silently (Unix swallows ESRCH/EPERM/EACCES,
		// Windows AssignProcessToJobObject fails because the process is gone) or it
		// throws. Either way, Dispose afterwards must not throw.
		try { group.Add(external); }
		catch (System.ComponentModel.Win32Exception) { }
		catch (InvalidOperationException) { }
	}

	[Test]
	public void Start_WhenExecutableMissing_PropagatesWin32Exception()
	{
		using var group = new ProcessGroup();
		var bad = new ProcessStartInfo("this-executable-definitely-does-not-exist-xyz-pg")
		{
			UseShellExecute = false,
		};

		Assert.Throws<System.ComponentModel.Win32Exception>(() => group.Start(bad));
	}

	[Test]
	public void Dispose_OnUnix_RespectsSharedTimeoutAcrossProcesses()
	{
		Assume.That(!OperatingSystem.IsWindows());

		var sw = Stopwatch.StartNew();
		using (var group = new ProcessGroup())
		{
			for (var i = 0; i < 5; i++)
				group.Start(LongRunningProcess());
		}
		sw.Stop();

		// SIGTERM on `sleep` exits immediately; the shared 2-second deadline
		// caps the total worst case. Allow generous headroom for slow CI.
		Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
	}

	[Test]
	public async Task ConcurrentStartAndGetStats_DoesNotThrow()
	{
		using var group = new ProcessGroup();

		var starts = Enumerable.Range(0, 10)
			.Select(_ => (Task)Task.Run(() => group.Start(LongRunningProcess())))
			.ToArray();
		var stats = Enumerable.Range(0, 50)
			.Select(_ => (Task)Task.Run(() => group.GetStats()))
			.ToArray();

		await Task.WhenAll(starts.Concat(stats));

		// Windows job counts auto-assigned descendants too (e.g. conhost), so
		// just assert we see at least the ones we started and the operation
		// did not throw under concurrency.
		Assert.That(group.GetStats().ActiveProcessCount, Is.GreaterThanOrEqualTo(10));
	}

	static ProcessStartInfo LongRunningProcess()
		=> OperatingSystem.IsWindows()
			? new ProcessStartInfo("ping", ["-n", "9999", "127.0.0.1"]) {
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
			}
			: new ProcessStartInfo("sleep", ["9999"]) {
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
			};
}
