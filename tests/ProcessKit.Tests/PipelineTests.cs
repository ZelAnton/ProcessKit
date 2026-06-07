namespace ProcessKit.Tests;

/// <summary>
/// Integration tests for the Phase 5 <see cref="ProcessPipeline"/>. Drives real OS processes per
/// stage (shell-free wiring) and asserts pipefail attribution, timeout teardown, and stdin flow.
/// Most tests are Unix-only because they rely on common pipeline tools (<c>echo</c>, <c>sort</c>,
/// <c>grep</c>, <c>cat</c>, <c>head</c>) that have idiosyncratic Windows equivalents.
/// </summary>
public class PipelineTests
{
	[Test]
	public async Task Pipe_TwoStages_FlowsData()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only — relies on echo + grep.");

		var output = await Command.Create("echo").Args("hello world")
			.Pipe(Command.Create("grep").Args("-o", "ello"))
			.RunAsync();

		Assert.That(output, Is.EqualTo("ello"));
	}

	[Test]
	public async Task Pipe_ThreeStages_EndToEnd()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only — relies on printf + sort + head.");

		var output = await Command.Create("printf").Args(@"delta\nalpha\nbravo\n")
			.Pipe(Command.Create("sort"))
			.Pipe(Command.Create("head").Args("-n", "1"))
			.RunAsync();

		Assert.That(output, Is.EqualTo("alpha"));
	}

	[Test]
	public async Task Pipe_Pipefail_AttributesFirstFailingInnerStage()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only — relies on sh + cat.");

		// echo X | (cat; exit 3) | cat   → middle stage exits 3, attribution wins.
		var result = await Command.Create("echo").Args("hello")
			.Pipe(Command.Create("sh").Args("-c", "cat; exit 3"))
			.Pipe(Command.Create("cat"))
			.OutputStringAsync();

		Assert.That(result.ExitCode, Is.EqualTo(3));
		Assert.That(result.IsSuccess, Is.False);
		// Stdout still flows from the LAST stage (Rust semantics).
		Assert.That(result.StdOut, Does.Contain("hello"));
	}

	[Test]
	public async Task Pipe_WithTimeout_KillsWholeChain()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only — relies on sh + sleep.");

		var swStart = DateTime.UtcNow;
		var result = await Command.Create("sh").Args("-c", "sleep 30")
			.Pipe(Command.Create("cat"))
			.WithTimeout(TimeSpan.FromMilliseconds(300))
			.OutputStringAsync();
		var elapsed = DateTime.UtcNow - swStart;

		Assert.That(result.WasTimedOut, Is.True);
		Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
			$"Timeout must tear the chain down quickly; took {elapsed}.");
	}

	[Test]
	public async Task Pipe_FirstStageStdin_FlowsThroughChain()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only — relies on cat + sort + head.");

		var result = await Command.Create("cat")
			.WithStandardInput(StandardInput.FromString("delta\nalpha\nbeta\n"))
			.Pipe(Command.Create("sort"))
			.Pipe(Command.Create("head").Args("-n", "1"))
			.OutputStringAsync();

		Assert.That(result.IsSuccess, Is.True);
		Assert.That(result.StdOut.TrimEnd(), Is.EqualTo("alpha"));
	}

	[Test]
	public async Task Pipe_AllSuccess_ReturnsLastStageDiagnostics()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only — relies on echo + cat.");

		var result = await Command.Create("echo").Args("payload")
			.Pipe(Command.Create("cat"))
			.OutputStringAsync();

		Assert.That(result.ExitCode, Is.Zero);
		Assert.That(result.StdOut, Does.Contain("payload"));
	}

	[Test]
	public void Pipe_RunAsyncOnNonZero_ThrowsProcessExitException()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only — relies on sh + cat.");

		var pipeline = Command.Create("echo").Args("hi")
			.Pipe(Command.Create("sh").Args("-c", "cat; exit 1"))
			.Pipe(Command.Create("cat"));

		var ex = Assert.ThrowsAsync<ProcessExitException>(() => pipeline.RunAsync());
		Assert.That(ex!.ExitCode, Is.EqualTo(1));
	}

	[Test]
	public async Task Pipe_WithCancellation_ThrowsProcessCancelledException()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only — relies on sh + sleep + cat.");

		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		var pipeline = Command.Create("sh").Args("-c", "sleep 5")
			.Pipe(Command.Create("cat"))
			.WithCancellation(cts.Token);

		var ex = Assert.ThrowsAsync<ProcessCancelledException>(() => pipeline.OutputStringAsync());
		Assert.That(ex, Is.InstanceOf<OperationCanceledException>(),
			"ProcessCancelledException must derive from OperationCanceledException.");
	}

	[Test]
	public void Pipe_StageWithProcessGroup_Throws()
	{
		Assume.That(!OperatingSystem.IsWindows(), "Unix-only test scope — covers the API guard.");

		using var sharedGroup = new ProcessGroup();
		var pipeline = Command.Create("echo").Args("x").WithProcessGroup(sharedGroup)
			.Pipe(Command.Create("cat"));

		Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.OutputStringAsync());
	}
}
