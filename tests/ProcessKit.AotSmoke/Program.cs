using System.Diagnostics;
using ProcessKit;

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
Console.WriteLine(
	$"AOT smoke OK. exit={child.ExitCode} active={stats.ActiveProcessCount} " +
	$"cpu={stats.TotalCpuTime} peakMem={stats.PeakMemoryBytes}");
return 0;
