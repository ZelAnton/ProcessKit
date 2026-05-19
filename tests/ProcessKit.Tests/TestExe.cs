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
			: Shell($"printf '%b' '{string.Concat(bytes.Select(b => $"\\x{b:x2}"))}'");
	}

	static ProcessStartInfo Shell(string command) => _sIsWindows
		? new ProcessStartInfo("cmd", ["/c", command]) { CreateNoWindow = true, UseShellExecute = false }
		: new ProcessStartInfo("sh", ["-c", command]) { UseShellExecute = false };

	static string EscapeForCmd(string text) => text.Replace("\"", "\\\"");
	static string EscapeForSh(string text) => $"'{text.Replace("'", "'\\''")}'";
	static string EscapeForShRaw(string text) => text.Replace("'", "'\\''");
}
