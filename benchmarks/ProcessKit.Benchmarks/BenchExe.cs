using System.Diagnostics;

namespace ProcessKit.Benchmarks;

/// <summary>
/// Cross-platform trivial child processes for benchmarks (cmd on Windows, sh elsewhere) — a local
/// copy of the test project's <c>TestExe</c> shape so benchmarks don't depend on the test assembly.
/// </summary>
static class BenchExe
{
	static readonly bool _isWindows = OperatingSystem.IsWindows();

	internal static ProcessStartInfo Echo(string text) => Shell(_isWindows ? $"echo {text}" : $"printf '%s\\n' '{text}'");

	internal static ProcessStartInfo ExitWith(int code) => Shell($"exit {code}");

	/// <summary>A child that prints <paramref name="lines"/> numbered lines to stdout.</summary>
	internal static ProcessStartInfo ChattyLines(int lines) => _isWindows
		? Shell($"for /L %i in (1,1,{lines}) do @echo line%i")
		: Shell($"i=1; while [ $i -le {lines} ]; do echo line$i; i=$((i+1)); done");

	internal static ProcessStartInfo EchoStdin() => _isWindows
		? new ProcessStartInfo("findstr", ["^"]) { CreateNoWindow = true, UseShellExecute = false }
		: Shell("cat");

	static ProcessStartInfo Shell(string command) => _isWindows
		? new ProcessStartInfo("cmd", ["/c", command]) { CreateNoWindow = true, UseShellExecute = false }
		: new ProcessStartInfo("sh", ["-c", command]) { UseShellExecute = false };
}
