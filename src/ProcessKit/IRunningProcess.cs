namespace ProcessKit;

/// <summary>
/// Handle to a running external process, exposing its stdout and stderr as streams of lines and
/// its exit code as an awaitable task. Disposing the handle kills the process if it is still
/// running and releases all associated resources.
/// </summary>
public interface IRunningProcess : IAsyncDisposable
{
	/// <summary>
	/// Stdout lines, streamed as the process produces them. Enumeration completes when the
	/// process closes its stdout (typically on exit). Stdout is decoded as UTF-8 by default.
	/// </summary>
	/// <remarks>
	/// The stream may be enumerated at most once. Breaking out of the <c>foreach</c> early does
	/// not kill the process — dispose the handle (or use <c>await using</c>) to terminate.
	/// </remarks>
	IAsyncEnumerable<string> StdOut { get; }

	/// <summary>
	/// Stderr lines, streamed as the process produces them. Enumeration completes when the
	/// process closes its stderr. Stderr is decoded as UTF-8 by default.
	/// </summary>
	/// <remarks>
	/// The implementation always drains stderr in the background to prevent the process from
	/// blocking on a full stderr pipe. If the caller subscribes neither to this stream nor to the
	/// optional <see cref="ProcessRunOptions.StandardErrorHandler"/>, stderr lines accumulate in
	/// memory until the handle is disposed — potential OOM on extremely chatty processes. Set
	/// <see cref="ProcessRunOptions.OutputBuffer"/> to cap the backlog and remove that risk. The
	/// stream may be enumerated at most once.
	/// </remarks>
	IAsyncEnumerable<string> StdErr { get; }

	/// <summary>
	/// Number of stdout lines observed so far. Updated atomically as each line is read off the pipe.
	/// Stable after the process exits. Counts every line read even if a configured
	/// <see cref="ProcessRunOptions.OutputBuffer"/> later dropped it from the replay buffer — so a
	/// count greater than the number of lines received from <see cref="StdOut"/> indicates dropped lines.
	/// </summary>
	int StdOutLineCount { get; }

	/// <summary>
	/// Number of stderr lines observed so far. Updated atomically as each line is read off the pipe.
	/// Stable after the process exits. <c>0</c> means the process wrote nothing to stderr. Like
	/// <see cref="StdOutLineCount"/>, counts every line even if <see cref="ProcessRunOptions.OutputBuffer"/>
	/// dropped it from the replay buffer.
	/// </summary>
	int StdErrLineCount { get; }

	/// <summary>The OS process id of the running process.</summary>
	int Pid { get; }

	/// <summary>The moment the process was started (local time).</summary>
	DateTime StartTime { get; }

	/// <summary>
	/// The total wall-clock time the process ran for, or <c>null</c> while it is still running.
	/// Once set, the value is stable.
	/// </summary>
	TimeSpan? Duration { get; }

	/// <summary>
	/// Total CPU time consumed by the process, or <c>null</c> if currently unavailable
	/// (for example the OS no longer exposes the counter after the process exited on Unix).
	/// Sampled live while the process is running; cached to the final value after exit so
	/// it stays accessible after the underlying <see cref="System.Diagnostics.Process"/> is disposed.
	/// </summary>
	TimeSpan? CpuTime { get; }

	/// <summary>
	/// Peak resident memory (working-set) observed for the process in bytes, or <c>null</c> if
	/// currently unavailable. Same live-then-cached semantics as <see cref="CpuTime"/>.
	/// </summary>
	long? PeakMemoryBytes { get; }

	/// <summary>
	/// <c>true</c> if the process was terminated specifically because
	/// <see cref="ProcessRunOptions.Timeout"/> elapsed. <c>false</c> for natural exit and for
	/// cancellation through the caller's external <see cref="System.Threading.CancellationToken"/>.
	/// </summary>
	bool WasTimedOut { get; }

	/// <summary>
	/// A <see cref="System.Threading.CancellationToken"/> that fires when the process exits.
	/// Useful for tying dependent work to the process's lifetime without manually awaiting
	/// <see cref="Completion"/>.
	/// </summary>
	CancellationToken Exited { get; }

	/// <summary>
	/// A task that completes with the raw process exit code once the process exits. Multiple
	/// callers may await it; the result is cached after the first resolution. To wait with a
	/// caller-supplied cancellation token, use <c>await runningProcess.Completion.WaitAsync(ct)</c>.
	/// </summary>
	Task<int> Completion { get; }
}
