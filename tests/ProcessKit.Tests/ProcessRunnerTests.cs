namespace ProcessKit.Tests;

public class ProcessRunnerTests
{
	[Test]
	public async Task GetOutputAsync_StreamsLines()
	{
		var runner = new ProcessRunner();
		var lines = new List<string>();
		await foreach (var line in runner.GetOutputAsync(TestExe.MultiLineEcho("first", "second", "third")))
			lines.Add(line);

		Assert.That(lines, Is.EqualTo((string[])["first", "second", "third"]));
	}

	[Test]
	public async Task GetFullOutputAsync_CapturesStdOutStdErrAndExitCode()
	{
		var runner = new ProcessRunner();
		var result = await runner.GetFullOutputAsync(TestExe.BothStreams("hello", "world"));

		Assert.That(result.StdOut, Does.Contain("hello"));
		Assert.That(result.StdErr, Does.Contain("world"));
		Assert.That(result.ExitCode, Is.Zero);
		Assert.That(result.IsSuccess, Is.True);
	}

	[Test]
	public async Task GetFullOutputAsync_NonZeroExit_ExposesExitCode()
	{
		var runner = new ProcessRunner();
		var result = await runner.GetFullOutputAsync(TestExe.ExitWith(7));

		Assert.That(result.ExitCode, Is.EqualTo(7));
		Assert.That(result.IsSuccess, Is.False);
	}

	[Test]
	public async Task EnsureSuccess_NonZero_ThrowsProcessExitException()
	{
		var runner = new ProcessRunner();
		var result = await runner.GetFullOutputAsync(TestExe.ExitWith(3));

		var ex = Assert.Throws<ProcessExitException>(() => result.EnsureSuccess());
		Assert.That(ex!.ExitCode, Is.EqualTo(3));
	}

	[Test]
	public async Task GetExitCodeAsync_DrainsStreamsAndReturnsCode()
	{
		var runner = new ProcessRunner();
		var code = await runner.GetExitCodeAsync(TestExe.ExitWith(0));
		Assert.That(code, Is.Zero);

		code = await runner.GetExitCodeAsync(TestExe.ExitWith(42));
		Assert.That(code, Is.EqualTo(42));
	}

	[Test]
	public async Task GetFirstLineOutputAsync_Predicate_ReturnsMatchAndKillsProcess()
	{
		var runner = new ProcessRunner();
		var line = await runner.GetFirstLineOutputAsync(
			TestExe.MultiLineEcho("alpha", "beta", "gamma"),
			predicate: l => l.StartsWith('b'));

		Assert.That(line, Is.EqualTo("beta"));
	}

	[Test]
	public async Task LineCounters_AreUpdated()
	{
		var runner = new ProcessRunner();
		await using var p = runner.Start(TestExe.MultiLineEcho("one", "two", "three"));
		await foreach (var _ in p.StdOut) { }
		await p.Completion;

		Assert.That(p.StdOutLineCount, Is.EqualTo(3));
		Assert.That(p.StdErrLineCount, Is.Zero);
	}

	[Test]
	public async Task Pid_StartTime_Duration_AreObservable()
	{
		var runner = new ProcessRunner();
		await using var p = runner.Start(TestExe.Echo("x"));

		Assert.That(p.Pid, Is.GreaterThan(0));
		Assert.That(p.StartTime, Is.GreaterThan(DateTime.Now.AddMinutes(-1)));

		await p.Completion;
		Assert.That(p.Duration, Is.Not.Null);
		Assert.That(p.Duration!.Value.TotalSeconds, Is.LessThan(30));
	}

	[Test]
	public async Task Exited_Token_FiresOnExit()
	{
		var runner = new ProcessRunner();
		await using var p = runner.Start(TestExe.Echo("x"));
		await p.Completion;
		Assert.That(p.Exited.IsCancellationRequested, Is.True);
	}

	[Test]
	public Task Cancellation_KillsProcess()
	{
		var runner = new ProcessRunner();
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

		var psi = TestExe.Sleep(30);
		var ex = Assert.ThrowsAsync<OperationCanceledException>(async () =>
		{
			await foreach (var _ in runner.GetOutputAsync(psi, cancellationToken: cts.Token)) { }
		});
		Assert.That(ex, Is.Not.Null);
		return Task.CompletedTask;
	}

	[Test]
	public async Task Timeout_KillsProcess()
	{
		var runner = new ProcessRunner();
		var options = new ProcessRunOptions { Timeout = TimeSpan.FromMilliseconds(300) };

		var sw = System.Diagnostics.Stopwatch.StartNew();
		await using var p = runner.Start(TestExe.Sleep(30), options);
		var code = await p.Completion;
		sw.Stop();

		Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(10));
		Assert.That(code, Is.Not.Zero); // killed → non-zero
	}

	[Test]
	public async Task StandardInput_FromString_DeliversToStdin()
	{
		var runner = new ProcessRunner();
		var options = new ProcessRunOptions { StandardInput = StandardInput.FromString("hello\n") };
		var result = await runner.GetFullOutputAsync(TestExe.EchoStdin(), options);

		Assert.That(result.StdOut.Trim(), Is.EqualTo("hello"));
	}

	[Test]
	public async Task StandardInput_FromLines_DeliversEachLine()
	{
		var runner = new ProcessRunner();
		async IAsyncEnumerable<string> Lines()
		{
			yield return "a";
			await Task.Yield();
			yield return "b";
			yield return "c";
		}
		var options = new ProcessRunOptions { StandardInput = StandardInput.FromLines(Lines()) };
		var result = await runner.GetFullOutputAsync(TestExe.EchoStdin(), options);

		var lines = result.StdOut.Split((string[])["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
		Assert.That(lines, Is.EqualTo((string[])["a", "b", "c"]));
	}

	[Test]
	public async Task GetBytesOutputAsync_ReturnsRawBytes()
	{
		var runner = new ProcessRunner();
		var payload = new byte[] { 0x00, 0x01, 0x02, 0x03, 0xff, 0xfe, 0xfd };

		var result = await runner.GetBytesOutputAsync(TestExe.Binary(payload));
		Assert.That(result.StdOut, Is.EqualTo(payload));
		Assert.That(result.ExitCode, Is.Zero);
	}

	[Test]
	public async Task StandardOutputHandler_FiresPerLine()
	{
		var runner = new ProcessRunner();
		var captured = new List<string>();
		var options = new ProcessRunOptions { StandardOutputHandler = captured.Add };

		await runner.GetExitCodeAsync(TestExe.MultiLineEcho("L1", "L2", "L3"), options);

		Assert.That(captured, Is.EqualTo((string[])["L1", "L2", "L3"]));
	}

	[Test]
	public async Task StandardErrorHandler_FiresPerLine()
	{
		var runner = new ProcessRunner();
		var captured = new List<string>();
		var options = new ProcessRunOptions { StandardErrorHandler = captured.Add };

		await runner.GetExitCodeAsync(TestExe.ErrEcho("err"), options);

		Assert.That(captured.Count, Is.GreaterThanOrEqualTo(1));
		Assert.That(captured.Any(l => l.Contains("err", StringComparison.Ordinal)), Is.True);
	}

	[Test]
	public async Task SharedProcessGroup_IsNotDisposedByRunner()
	{
		using var group = new ProcessGroup();
		var runner = new ProcessRunner();
		var options = new ProcessRunOptions { ProcessGroup = group };

		var result = await runner.GetFullOutputAsync(TestExe.Echo("x"), options);
		Assert.That(result.ExitCode, Is.Zero);

		// Group is still alive: starting another process succeeds (no ObjectDisposedException).
		Assert.DoesNotThrow(() => group.GetStats());
	}

	[Test]
	public void GetOutputSync_ReturnsStdOut()
	{
		var runner = new ProcessRunner();
		// Pick the shell up front — running cmd on Linux throws Win32Exception before any fallback.
		var output = OperatingSystem.IsWindows()
			? runner.GetOutput("cmd", ["/c", "echo sync-test"])
			: runner.GetOutput("sh", ["-c", "echo sync-test"]);
		Assert.That(output.Trim(), Is.EqualTo("sync-test"));
	}

	[Test]
	public async Task DefensivePsiCopy_MutationAfterStart_DoesNotAffect()
	{
		var runner = new ProcessRunner();
		var psi = TestExe.Echo("snapshot");

		await using var p = runner.Start(psi);

		// Mutate after start
		psi.FileName = "nonexistent";
		psi.ArgumentList.Clear();

		var lines = new List<string>();
		await foreach (var line in p.StdOut) lines.Add(line);

		Assert.That(lines.Any(l => l.Contains("snapshot", StringComparison.Ordinal)), Is.True);
	}

	[Test]
	public async Task StandardInput_FromEnumerable_DeliversEachLine()
	{
		var runner = new ProcessRunner();
		var options = new ProcessRunOptions { StandardInput = StandardInput.FromEnumerable(["a", "b", "c"]) };
		var result = await runner.GetFullOutputAsync(TestExe.EchoStdin(), options);

		var lines = result.StdOut.Split((string[])["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
		Assert.That(lines, Is.EqualTo((string[])["a", "b", "c"]));
	}

	[Test]
	public async Task StandardInput_FromFile_DeliversFileContents()
	{
		var runner = new ProcessRunner();
		var tempFile = Path.Combine(Path.GetTempPath(), $"processkit-test-{Guid.NewGuid():N}.txt");
		await File.WriteAllTextAsync(tempFile, "file-contents\n");
		try
		{
			var options = new ProcessRunOptions { StandardInput = StandardInput.FromFile(tempFile) };
			var result = await runner.GetFullOutputAsync(TestExe.EchoStdin(), options);
			Assert.That(result.StdOut.Trim(), Is.EqualTo("file-contents"));
		}
		finally
		{
			File.Delete(tempFile);
		}
	}

	[Test]
	public void StandardInput_FromFile_MissingPath_Throws()
	{
		var bogusPath = Path.Combine(Path.GetTempPath(), $"processkit-missing-{Guid.NewGuid():N}.txt");
		Assert.Throws<FileNotFoundException>(() => StandardInput.FromFile(bogusPath));
	}

	[Test]
	public async Task WasTimedOut_True_OnTimeout()
	{
		var runner = new ProcessRunner();
		var options = new ProcessRunOptions { Timeout = TimeSpan.FromMilliseconds(300) };

		var result = await runner.GetFullOutputAsync(TestExe.Sleep(30), options);

		Assert.That(result.WasTimedOut, Is.True);
		Assert.That(result.IsSuccess, Is.False);
	}

	[Test]
	public async Task WasTimedOut_False_OnNormalExit()
	{
		var runner = new ProcessRunner();
		var result = await runner.GetFullOutputAsync(TestExe.Echo("hi"));

		Assert.That(result.WasTimedOut, Is.False);
		Assert.That(result.IsSuccess, Is.True);
	}

	[Test]
	public async Task IRunningProcess_WasTimedOut_OnTimeout()
	{
		var runner = new ProcessRunner();
		var options = new ProcessRunOptions { Timeout = TimeSpan.FromMilliseconds(300) };

		await using var p = runner.Start(TestExe.Sleep(30), options);
		await p.Completion;

		Assert.That(p.WasTimedOut, Is.True);
	}

	[Test]
	public async Task CpuTime_PeakMemoryBytes_DoNotThrow()
	{
		var runner = new ProcessRunner();
		await using var p = runner.Start(TestExe.Echo("stats"));
		await p.Completion;

		// Counters are best-effort: some platforms return values after exit, others (Unix)
		// release per-process kernel state immediately. We only assert the getters don't throw —
		// the contract allows null for "unavailable".
		Assert.DoesNotThrow(() => { _ = p.CpuTime; _ = p.PeakMemoryBytes; });
	}

	[Test]
	public void ProcessRunOptions_WithExpression_OverridesOneField()
	{
		var basic = new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(1) };
		var modified = basic with { Timeout = TimeSpan.FromSeconds(10) };

		Assert.That(basic.Timeout, Is.EqualTo(TimeSpan.FromSeconds(1)));
		Assert.That(modified.Timeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
		Assert.That(modified, Is.Not.EqualTo(basic));
	}

	[Test]
	public async Task ProcessRunner_Default_IsUsable()
	{
		var result = await ProcessRunner.Default.GetFullOutputAsync(TestExe.Echo("via-default"));
		Assert.That(result.IsSuccess, Is.True);
		Assert.That(result.StdOut, Does.Contain("via-default"));
	}

	[Test]
	public async Task EnsureSuccessAsync_PassesThroughOnSuccess()
	{
		var runner = new ProcessRunner();
		var result = await runner.GetFullOutputAsync(TestExe.Echo("ok")).EnsureSuccessAsync();
		Assert.That(result.StdOut, Does.Contain("ok"));
	}

	[Test]
	public void EnsureSuccessAsync_ThrowsOnNonZero()
	{
		var runner = new ProcessRunner();
		var ex = Assert.ThrowsAsync<ProcessExitException>(async () =>
			await runner.GetFullOutputAsync(TestExe.ExitWith(2)).EnsureSuccessAsync());
		Assert.That(ex!.ExitCode, Is.EqualTo(2));
	}
}
