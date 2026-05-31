namespace ProcessKit;

/// <summary>
/// Configures how a <see cref="ProcessGroup"/> tears down its child processes.
/// </summary>
/// <remarks>
/// <para>
/// These settings apply to <strong>Unix</strong> process groups (Linux/macOS/FreeBSD), where
/// shutdown is a graceful <c>SIGTERM</c>-then-wait-then-<c>SIGKILL</c> sequence. On
/// <strong>Windows</strong> they are ignored: a Job Object terminates all of its members
/// atomically when its handle is closed, so there is no soft-signal grace window to tune and
/// <see cref="EscalateToKill"/> cannot be honored (closing the handle kills the job regardless).
/// </para>
/// </remarks>
public sealed record ProcessGroupOptions
{
	/// <summary>
	/// How long to wait after sending <c>SIGTERM</c> for children to exit on their own before
	/// force-killing survivors (when <see cref="EscalateToKill"/> is <c>true</c>). This is a
	/// <strong>shared</strong> deadline across all processes in the group, not per-process.
	/// Defaults to 2 seconds. Zero or negative means "do not wait — escalate immediately".
	/// </summary>
	public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

	/// <summary>
	/// When <c>true</c> (default), any child still alive after <see cref="ShutdownTimeout"/> is
	/// force-killed (<c>Process.Kill(entireProcessTree)</c>). When <c>false</c>, survivors are left
	/// running after the polite <c>SIGTERM</c> and grace window elapse (Unix only — see remarks).
	/// </summary>
	public bool EscalateToKill { get; init; } = true;

	/// <summary>Shared default instance (2 s grace, escalate to kill).</summary>
	public static ProcessGroupOptions Default { get; } = new();
}
