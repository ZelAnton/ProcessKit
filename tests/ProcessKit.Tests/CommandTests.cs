namespace ProcessKit.Tests;

/// <summary>
/// Integration tests for the Phase 4 <see cref="Command"/> fluent builder. Drives a real
/// <see cref="ProcessRunner"/> against the OS through each terminal verb.
/// </summary>
public class CommandTests
{
	[Test]
	public async Task Create_And_Run_ReturnsTrimmedStdOut()
	{
		// Run a shell that echoes a constant; assert the trimmed text round-trips.
		var output = await ShellCommand("echo hi").RunAsync();
		Assert.That(output, Is.EqualTo("hi"));
	}

	[Test]
	public void Run_OnNonZeroExit_ThrowsProcessExitException()
	{
		var cmd = ShellCommand("exit 7");
		var ex = Assert.ThrowsAsync<ProcessExitException>(() => cmd.RunAsync());
		Assert.That(ex!.ExitCode, Is.EqualTo(7));
	}

	[Test]
	public async Task ExitCodeAsync_ReturnsRawCode()
	{
		var code = await ShellCommand("exit 42").ExitCodeAsync();
		Assert.That(code, Is.EqualTo(42));
	}

	[Test]
	public async Task ProbeAsync_Exit0_ReturnsTrue()
	{
		var ok = await ShellCommand("exit 0").ProbeAsync();
		Assert.That(ok, Is.True);
	}

	[Test]
	public async Task ProbeAsync_Exit1_ReturnsFalse()
	{
		var ok = await ShellCommand("exit 1").ProbeAsync();
		Assert.That(ok, Is.False);
	}

	[Test]
	public void ProbeAsync_OtherExit_ThrowsProcessExitException()
	{
		var cmd = ShellCommand("exit 2");
		Assert.ThrowsAsync<ProcessExitException>(() => cmd.ProbeAsync());
	}

	[Test]
	public async Task OutputStringAsync_CapturesStdOutStdErrAndExitCode()
	{
		var psiBuilder = TestExe.BothStreams("out-text", "err-text");
		var cmd = WrapBuiltCommand(psiBuilder);
		var result = await cmd.OutputStringAsync();

		Assert.That(result.StdOut, Does.Contain("out-text"));
		Assert.That(result.StdErr, Does.Contain("err-text"));
		Assert.That(result.ExitCode, Is.Zero);
	}

	[Test]
	public async Task OutputBytesAsync_RetainsRawBytes()
	{
		var bytes = new byte[] { 0, 255, 42 };
		var psiBuilder = TestExe.Binary(bytes);
		var cmd = WrapBuiltCommand(psiBuilder);
		var result = await cmd.OutputBytesAsync();

		Assert.That(result.StdOut, Is.EqualTo(bytes));
		Assert.That(result.ExitCode, Is.Zero);
	}

	[Test]
	public async Task FirstLineAsync_FindsMatchingLine()
	{
		var psiBuilder = TestExe.MultiLineEcho("alpha", "beta", "gamma");
		var cmd = WrapBuiltCommand(psiBuilder);
		var line = await cmd.FirstLineAsync(l => l == "beta");
		Assert.That(line, Is.EqualTo("beta"));
	}

	[Test]
	public async Task WithTimeout_Captures_WasTimedOut()
	{
		var psiBuilder = TestExe.Sleep(5);
		var cmd = WrapBuiltCommand(psiBuilder).WithTimeout(TimeSpan.FromMilliseconds(200));
		var result = await cmd.OutputStringAsync();
		Assert.That(result.WasTimedOut, Is.True);
	}

	[Test]
	public void WithCancellation_ThrowsProcessCancelledException()
	{
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var psiBuilder = TestExe.Sleep(5);
		var cmd = WrapBuiltCommand(psiBuilder).WithCancellation(cts.Token);

		var ex = Assert.ThrowsAsync<ProcessCancelledException>(() => cmd.OutputStringAsync());
		Assert.That(ex, Is.InstanceOf<OperationCanceledException>(),
			"ProcessCancelledException must derive from OperationCanceledException for back-compat.");
		Assert.That(ex!.Program, Is.Not.Empty);
	}

	[Test]
	public async Task WithWorkingDirectory_AppliesToChild()
	{
		var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var cmd = ShellCommand(OperatingSystem.IsWindows() ? "cd" : "pwd").WithWorkingDirectory(tmp);
		var output = await cmd.RunAsync();
		Assert.That(NormalisePath(output), Does.StartWith(NormalisePath(tmp)));
	}

	[Test]
	public async Task WithEnvironment_AppliesToChild()
	{
		var cmd = ShellCommand(OperatingSystem.IsWindows() ? "echo %PHASE4_VAR%" : "printf '%s' \"$PHASE4_VAR\"")
			.WithEnvironment("PHASE4_VAR", "phase4-value");
		var output = await cmd.RunAsync();
		Assert.That(output, Does.Contain("phase4-value"));
	}

	[Test]
	public async Task InheritEnvironment_OnlyKeepsNamedVars()
	{
		// Seed two variables in the current process, then inherit ONE only.
		Environment.SetEnvironmentVariable("PHASE4_ONE", "one-value");
		Environment.SetEnvironmentVariable("PHASE4_TWO", "two-value");
		try
		{
			// Child prints both variables. The non-inherited one should expand to empty.
			var script = OperatingSystem.IsWindows()
				? "echo [%PHASE4_ONE%]&echo [%PHASE4_TWO%]"
				: "printf '[%s]\\n[%s]\\n' \"$PHASE4_ONE\" \"$PHASE4_TWO\"";
			var cmd = ShellCommand(script).InheritEnvironment("PHASE4_ONE");
			var output = await cmd.RunAsync();
			Assert.That(output, Does.Contain("[one-value]"));
			Assert.That(output, Does.Not.Contain("[two-value]"));
		}
		finally
		{
			Environment.SetEnvironmentVariable("PHASE4_ONE", null);
			Environment.SetEnvironmentVariable("PHASE4_TWO", null);
		}
	}

	// --- Helpers -----------------------------------------------------------------------------

	/// <summary>Builds a <see cref="Command"/> targeting <c>cmd /c</c> on Windows or <c>sh -c</c>
	/// elsewhere with the given shell <paramref name="script"/>.</summary>
	static Command ShellCommand(string script)
	{
		if (OperatingSystem.IsWindows())
			return Command.Create("cmd").Args("/c", script).WithCreateNoWindow();
		return Command.Create("sh").Args("-c", script);
	}

	/// <summary>Adapts a <see cref="ProcessStartInfo"/> built by <see cref="TestExe"/> helpers into
	/// an equivalent <see cref="Command"/>.</summary>
	static Command WrapBuiltCommand(System.Diagnostics.ProcessStartInfo psi)
	{
		var cmd = Command.Create(psi.FileName);
		if (psi.ArgumentList.Count > 0)
			cmd = cmd.Args([.. psi.ArgumentList]);
		if (psi.CreateNoWindow)
			cmd = cmd.WithCreateNoWindow();
		return cmd;
	}

	static string NormalisePath(string p) =>
		p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
		 .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
}
