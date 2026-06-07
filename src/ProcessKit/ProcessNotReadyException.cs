namespace ProcessKit;

/// <summary>
/// Thrown by a readiness probe (<see cref="IRunningProcess.WaitForLineAsync"/>,
/// <see cref="IRunningProcess.WaitForAsync"/>, <see cref="IRunningProcess.WaitForPortAsync"/>) when
/// the configured deadline elapses, or the child exits before the probe's condition is satisfied.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ProcessRunOptions.Timeout"/>-driven termination — a probe failure does
/// NOT kill the child. The caller decides what happens next (retry the probe, dispose the handle,
/// keep the process running).
/// </remarks>
public sealed class ProcessNotReadyException : Exception
{
	/// <summary>The basename of the program that did not become ready.</summary>
	public string Program { get; }

	/// <summary>The probe deadline that elapsed (or would have — an early child exit fails immediately).</summary>
	public TimeSpan Timeout { get; }

	public ProcessNotReadyException(string program, TimeSpan timeout)
		: base($"`{program}` was not ready after {timeout}.")
	{
		Program = program;
		Timeout = timeout;
	}
}
