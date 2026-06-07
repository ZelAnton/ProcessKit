using System.Diagnostics;
using ProcessKit;
using ProcessKit.Diagnostics;

// Listener for ProcessKitActivitySource — verifies that Activity instrumentation survives AOT
// publish (the ActivitySource itself is BCL, but the source-name discovery and tag dispatch must
// stay reachable). If we get zero stopped activities by the end, instrumentation regressed.
var capturedActivities = 0;
using var listener = new ActivityListener
{
	ShouldListenTo = src => src.Name == ProcessKitActivitySource.Name,
	Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
	ActivityStopped = _ => Interlocked.Increment(ref capturedActivities),
};
ActivitySource.AddActivityListener(listener);

using var group = new ProcessGroup();

var psi = new ProcessStartInfo
{
	FileName = "/bin/sh",
	RedirectStandardOutput = true,
};
psi.ArgumentList.Add("-c");
psi.ArgumentList.Add("echo hello from child");

var child = group.Start(psi);
var output = child.StandardOutput.ReadToEnd().Trim();
child.WaitForExit();

if (output != "hello from child")
{
	Console.Error.WriteLine($"AOT smoke FAIL: unexpected stdout '{output}'");
	return 1;
}

if (child.ExitCode != 0)
{
	Console.Error.WriteLine($"AOT smoke FAIL: child exit code {child.ExitCode}");
	return 1;
}

var stats = group.GetStats();

// Exercise the ProcessRunner / ProcessSession path under Native AOT too (sinks, line channels,
// the IProcessHandle seam) — not just the ProcessGroup lifetime layer.
var runnerResult = await ProcessRunner.Default.GetFullOutputAsync("/bin/sh", ["-c", "echo runner-aot"]);
if (runnerResult.ExitCode != 0 || !runnerResult.StdOut.Contains("runner-aot", StringComparison.Ordinal))
{
	Console.Error.WriteLine($"AOT smoke FAIL: runner stdout '{runnerResult.StdOut}' exit {runnerResult.ExitCode}");
	return 1;
}

// Exercise the interactive-stdin path (ProcessStandardInputWriter, KeepStandardInputOpen) under AOT.
string? echoed = null;
await using (var interactive = ProcessRunner.Default.Start("/bin/sh", ["-c", "cat"], new ProcessRunOptions { KeepStandardInputOpen = true }))
{
	await interactive.StandardInput!.WriteLineAsync("interactive-aot");
	await interactive.StandardInput.CompleteAsync();
	await foreach (var line in interactive.StdOut)
	{
		echoed = line;
		break;
	}
}
if (echoed != "interactive-aot")
{
	Console.Error.WriteLine($"AOT smoke FAIL: interactive stdin echoed '{echoed}'");
	return 1;
}

// Dispose the group explicitly here so the processkit.group.shutdown span fires before we read
// capturedActivities — the `using` declaration above would otherwise dispose after the check.
group.Dispose();

if (capturedActivities == 0)
{
	Console.Error.WriteLine("AOT smoke FAIL: no ProcessKit activities were captured — diagnostics regressed under AOT.");
	return 1;
}

Console.WriteLine(
	$"AOT smoke OK. exit={child.ExitCode} active={stats.ActiveProcessCount} " +
	$"cpu={stats.TotalCpuTime} peakMem={stats.PeakMemoryBytes} runner='{runnerResult.StdOut.Trim()}' " +
	$"activities={capturedActivities}");
return 0;
