namespace ProcessKit;

/// <summary>
/// Result of a bulk process execution: the captured stdout, the captured stderr,
/// and the process exit code.
/// </summary>
/// <typeparam name="T">Type of the captured stdout (e.g. <see cref="string"/> or <c>byte[]</c>).</typeparam>
/// <param name="StdOut">Standard output as captured during the run.</param>
/// <param name="StdErr">Standard error as captured during the run, decoded as UTF-8.</param>
/// <param name="ExitCode">The raw process exit code. The runner does not interpret it as success or failure.</param>
public readonly record struct ProcessResult<T>(T StdOut, string StdErr, int ExitCode)
{
	/// <summary>
	/// <c>true</c> if the process was terminated because <see cref="ProcessRunOptions.Timeout"/>
	/// elapsed. <c>false</c> for natural exits or for cancellation via the caller's
	/// <see cref="CancellationToken"/>.
	/// </summary>
	public bool WasTimedOut { get; init; }

	/// <summary><c>true</c> when <see cref="ExitCode"/> is zero.</summary>
	public bool IsSuccess => ExitCode == 0;

	/// <summary>
	/// Returns <c>this</c> when <see cref="IsSuccess"/>; otherwise throws
	/// <see cref="ProcessExitException"/> containing the exit code and the captured stderr.
	/// Designed for fluent chaining: <c>var output = (await runner.GetFullOutputAsync(...)).EnsureSuccess().StdOut;</c>.
	/// </summary>
	public ProcessResult<T> EnsureSuccess()
	{
		if (ExitCode == 0)
			return this;

		const int maxStdErrInMessage = 4096;
		var truncated = StdErr.Length > maxStdErrInMessage
			? string.Concat(StdErr.AsSpan(0, maxStdErrInMessage), "...[truncated]")
			: StdErr;
		var message = $"Process exited with code {ExitCode}. StdErr: {truncated.Trim()}";
		throw new ProcessExitException(ExitCode, StdErr, message);
	}
}
