using System.Diagnostics;

namespace ProcessKit;

/// <summary>
/// Runs an external executable and exposes its stdout, stderr and exit code via an
/// <see cref="IRunningProcess"/> handle. This interface contains the single low-level primitive;
/// all higher-level helpers (<c>GetOutputAsync</c>, <c>GetFullOutputAsync</c>,
/// <c>GetExitCodeAsync</c>, sync wrappers, etc.) are extension methods built on top of it and
/// live in <c>ProcessRunnerExtensions</c>.
/// </summary>
/// <remarks>
/// <para>
/// The runner never inspects the process exit code as success or failure — it is exposed via the
/// returned handle (and, for bulk extension helpers, via <see cref="ProcessResult{T}.ExitCode"/>).
/// </para>
/// <para>
/// The runner takes a defensive copy of the supplied <see cref="ProcessStartInfo"/> and forces
/// <see cref="ProcessStartInfo.RedirectStandardOutput"/>,
/// <see cref="ProcessStartInfo.RedirectStandardError"/> to <c>true</c> and
/// <see cref="ProcessStartInfo.UseShellExecute"/> to <c>false</c>. Caller-provided redirection
/// flags on those streams are ignored. The original PSI instance is not mutated.
/// </para>
/// <para>
/// Decoded text defaults to UTF-8. Override via
/// <see cref="ProcessRunOptions.StdOutEncoding"/> / <see cref="ProcessRunOptions.StdErrEncoding"/>
/// or the corresponding <see cref="ProcessStartInfo"/> encoding properties.
/// </para>
/// </remarks>
public interface IProcessRunner
{
	/// <summary>
	/// Starts the process described by <paramref name="startInfo"/> and returns a handle that
	/// exposes stdout and stderr as streams of lines plus an awaitable exit code.
	/// </summary>
	/// <remarks>
	/// Always dispose the returned handle (typically with <c>await using</c>) so the process is
	/// terminated even on early exit.
	/// </remarks>
	IRunningProcess Start(
		ProcessStartInfo startInfo,
		ProcessRunOptions? options = null,
		CancellationToken cancellationToken = default);
}
