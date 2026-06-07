using System.Diagnostics;

namespace ProcessKit.Tests;

/// <summary>
/// Cross-platform helpers for spawning trivial child processes from tests.
/// Picks <c>cmd /c ...</c> on Windows, <c>sh -c '...'</c> on Linux/macOS/FreeBSD.
/// </summary>
static class TestExe
{
	static readonly bool _sIsWindows = OperatingSystem.IsWindows();

	internal static ProcessStartInfo Echo(string text) => Shell(_sIsWindows
		? $"echo {EscapeForCmd(text)}"
		: $"printf '%s\\n' {EscapeForSh(text)}");

	internal static ProcessStartInfo MultiLineEcho(params string[] lines)
	{
		if (_sIsWindows)
			// `(echo X)&(echo Y)` — parens isolate echo args (no trailing-space-before-& quirk).
			return Shell(string.Join("&", lines.Select(l => $"(echo {EscapeForCmd(l)})")));
		var joined = string.Join("\\n", lines.Select(EscapeForShRaw));
		return Shell($"printf '{joined}\\n'");
	}

	internal static ProcessStartInfo ErrEcho(string text) => Shell(_sIsWindows
		? $"(echo {EscapeForCmd(text)})1>&2"
		: $"printf '%s\\n' {EscapeForSh(text)} 1>&2");

	internal static ProcessStartInfo BothStreams(string stdoutText, string stderrText) => Shell(_sIsWindows
		? $"(echo {EscapeForCmd(stdoutText)})&(echo {EscapeForCmd(stderrText)})1>&2"
		: $"printf '%s\\n' {EscapeForSh(stdoutText)}; printf '%s\\n' {EscapeForSh(stderrText)} 1>&2");

	internal static ProcessStartInfo ExitWith(int code) => Shell($"exit {code}");

	internal static ProcessStartInfo Sleep(double seconds) => _sIsWindows
		? new ProcessStartInfo("ping", ["-n", ((int)Math.Ceiling(seconds) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "127.0.0.1"]) { CreateNoWindow = true, UseShellExecute = false }
		: Shell($"sleep {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

	/// <summary>
	/// Spawns a process that emits <paramref name="text"/> + newline every <paramref name="intervalSeconds"/>
	/// for <paramref name="count"/> iterations. Used by suspend/resume tests to measure flow with
	/// vs without freeze.
	/// </summary>
	internal static ProcessStartInfo PeriodicEcho(string text, double intervalSeconds, int count)
	{
		ProcessStartInfo psi;
		if (_sIsWindows)
		{
			// PowerShell loop — cmd's `ping -w` timing is too unreliable at sub-second granularity.
			// Use [Console]::Out.WriteLine + Flush instead of Write-Output: when stdout is a
			// redirected pipe (as it is here), pwsh's host applies block buffering and short lines
			// like "tick" never flush until process exit, which would make suspend/resume tests
			// non-deterministic. Console + explicit Flush forces per-line visibility.
			var script = $"for($i=0;$i -lt {count};$i++){{[Console]::Out.WriteLine('{text}');[Console]::Out.Flush();Start-Sleep -Milliseconds {(int)(intervalSeconds * 1000)}}}";
			psi = new ProcessStartInfo("powershell", ["-NoProfile", "-Command", script]) { CreateNoWindow = true, UseShellExecute = false };
		}
		else
		{
			var sleepArg = intervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
			psi = Shell($"i=0; while [ $i -lt {count} ]; do printf '%s\\n' {EscapeForSh(text)}; sleep {sleepArg}; i=$((i+1)); done");
		}
		psi.RedirectStandardOutput = true;
		return psi;
	}

	internal static ProcessStartInfo EchoStdin() => _sIsWindows
		// 'findstr "^"' echoes stdin line-by-line; cleaner than 'more'.
		? new ProcessStartInfo("findstr", ["^"]) { CreateNoWindow = true, UseShellExecute = false }
		: Shell("cat");

	internal static ProcessStartInfo Binary(byte[] bytes)
	{
		// Encode bytes as hex, then decode via shell.
		var hex = Convert.ToHexString(bytes);
		return _sIsWindows
			? new ProcessStartInfo("powershell", ["-NoProfile", "-Command", $"[Console]::OpenStandardOutput().Write([byte[]] (0..({bytes.Length}-1) | %{{ [Convert]::ToByte('{hex}'.Substring($_*2,2),16) }}), 0, {bytes.Length})"])
			{
				CreateNoWindow = true,
				UseShellExecute = false,
			}
			// Octal escapes (`\NNN`) are POSIX printf — supported by dash/ash on Ubuntu/Alpine.
			// `\xNN` is a bash/GNU extension and outputs the literal text on dash, breaking the test.
			: Shell($"printf '{string.Concat(bytes.Select(b => $"\\{Convert.ToString(b, 8).PadLeft(3, '0')}"))}'");
	}

	static ProcessStartInfo Shell(string command) => _sIsWindows
		? new ProcessStartInfo("cmd", ["/c", command]) { CreateNoWindow = true, UseShellExecute = false }
		: new ProcessStartInfo("sh", ["-c", command]) { UseShellExecute = false };

	static string EscapeForCmd(string text) => text.Replace("\"", "\\\"");
	static string EscapeForSh(string text) => $"'{text.Replace("'", "'\\''")}'";
	static string EscapeForShRaw(string text) => text.Replace("'", "'\\''");
}
