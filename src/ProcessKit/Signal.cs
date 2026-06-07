using System.Diagnostics.CodeAnalysis;

namespace ProcessKit;

/// <summary>
/// Canonical signals supported by <see cref="ProcessGroup.SignalAsync(Signal, System.Threading.CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the POSIX signal set that has cross-Unix portability. <c>SIGSTOP</c>/<c>SIGCONT</c> are
/// deliberately omitted from the public surface — use <see cref="ProcessGroup.SuspendAsync"/> /
/// <see cref="ProcessGroup.ResumeAsync"/> for the portable equivalent (Windows can't deliver Unix
/// signals but supports thread suspend/resume).
/// </para>
/// <para>
/// On Windows only <see cref="Kill"/> is honored (maps to <c>TerminateJobObject</c>); every other
/// variant throws <see cref="System.PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
	Justification = "Int mirrors the POSIX signal name SIGINT and the canonical Rust naming; renaming to Interrupt would diverge from the cross-language convention.")]
public enum Signal
{
	/// <summary><c>SIGTERM</c> — polite request to exit.</summary>
	Term,
	/// <summary><c>SIGKILL</c> — unblockable kill. On Windows: terminate the Job Object.</summary>
	Kill,
	/// <summary><c>SIGINT</c> — keyboard interrupt.</summary>
	Int,
	/// <summary><c>SIGHUP</c> — hangup; conventionally "reload configuration".</summary>
	Hup,
	/// <summary><c>SIGQUIT</c> — quit, typically with a core dump.</summary>
	Quit,
	/// <summary><c>SIGUSR1</c> — application-defined.</summary>
	Usr1,
	/// <summary><c>SIGUSR2</c> — application-defined.</summary>
	Usr2,
}

/// <summary>
/// Raw POSIX signal number for delivery via <see cref="ProcessGroup.SignalAsync(CustomSignal, System.Threading.CancellationToken)"/>.
/// Unix-only — throws <see cref="System.PlatformNotSupportedException"/> on Windows.
/// </summary>
public readonly record struct CustomSignal(int Number);

/// <summary>
/// Maps the portable <see cref="Signal"/> enum to OS-specific POSIX numbers. SIGTERM/SIGINT/SIGHUP/
/// SIGQUIT/SIGKILL share numbers across every Unix in scope, but SIGUSR1/SIGUSR2/SIGSTOP/SIGCONT
/// diverge between Linux and the BSD-derived family (macOS/FreeBSD).
/// </summary>
static class SignalNumbers
{
	// Numbers identical on every POSIX in scope (Linux/macOS/FreeBSD).
	internal const int SIGHUP = 1;
	internal const int SIGINT = 2;
	internal const int SIGQUIT = 3;
	internal const int SIGKILL = 9;
	internal const int SIGTERM = 15;

	internal static int ToPosix(Signal signal) => signal switch
	{
		Signal.Term => SIGTERM,
		Signal.Kill => SIGKILL,
		Signal.Int => SIGINT,
		Signal.Hup => SIGHUP,
		Signal.Quit => SIGQUIT,
		Signal.Usr1 => SigUsr1(),
		Signal.Usr2 => SigUsr2(),
		_ => throw new System.ArgumentOutOfRangeException(nameof(signal), signal, "Unknown Signal value."),
	};

	internal static int SigStop() => OperatingSystem.IsLinux() ? 19 : 17;
	internal static int SigCont() => OperatingSystem.IsLinux() ? 18 : 19;

	static int SigUsr1() => OperatingSystem.IsLinux() ? 10 : 30;
	static int SigUsr2() => OperatingSystem.IsLinux() ? 12 : 31;
}
