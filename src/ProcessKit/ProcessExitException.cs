using JetBrains.Annotations;

namespace ProcessKit;

/// <summary>
/// Thrown by <see cref="ProcessResult{T}.EnsureSuccess"/> when the wrapped process exited with a
/// non-zero exit code. Carries the exit code and the captured stderr for diagnostics.
/// </summary>
public sealed class ProcessExitException(int exitCode, string stdErr, string message)
	: Exception(message)
{
	/// <summary>The raw process exit code.</summary>
	public int ExitCode { get; } = exitCode;

	/// <summary>The captured stderr (possibly truncated for the exception message).</summary>
	[UsedImplicitly]
	public string StdErr { get; } = stdErr;
}
