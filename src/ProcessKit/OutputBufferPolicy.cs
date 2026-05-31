namespace ProcessKit;

/// <summary>
/// Bounds how much unconsumed stdout/stderr the runner buffers in memory when the caller reads
/// slower than the child produces — or never reads at all. The pump <strong>always</strong> keeps
/// draining the OS pipe so the child never deadlocks on a full buffer; this policy only caps the
/// in-memory backlog of lines waiting to be replayed through <see cref="IRunningProcess.StdOut"/> /
/// <see cref="IRunningProcess.StdErr"/>.
/// </summary>
/// <remarks>
/// Applied independently to stdout and stderr. When unset (the default), buffering is unbounded —
/// matching historical behavior for callers that actively consume. Set a cap to protect the
/// "never consumed" case (the documented OOM risk on chatty processes). The cap is by
/// <em>line count</em>; a single pathological multi-megabyte line still costs that much memory.
/// </remarks>
public sealed record OutputBufferPolicy
{
	/// <summary>
	/// Maximum number of unconsumed lines retained per stream before <see cref="Overflow"/>
	/// applies. <c>null</c> means unbounded (current behavior). <c>0</c> means "retain nothing" —
	/// every line is drained from the pipe and discarded (useful when the exit code is all you want).
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">Set to a negative value.</exception>
	public int? MaxBufferedLines
	{
		get => _maxBufferedLines;
		init
		{
			if (value is < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, "MaxBufferedLines must be null, zero, or positive.");
			_maxBufferedLines = value;
		}
	}

	readonly int? _maxBufferedLines;

	/// <summary>What to do when <see cref="MaxBufferedLines"/> is reached. Defaults to <see cref="OutputOverflowMode.DropOldest"/>.</summary>
	public OutputOverflowMode Overflow { get; init; } = OutputOverflowMode.DropOldest;
}
