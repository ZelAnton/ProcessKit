using System.Text;

namespace ProcessKit;

/// <summary>
/// Runtime-level configuration for a single <see cref="IProcessRunner"/> invocation —
/// orthogonal to <see cref="System.Diagnostics.ProcessStartInfo"/>, which describes
/// <em>what</em> to run. <see cref="ProcessRunOptions"/> describes <em>how</em> the runner
/// observes and supervises the started process.
/// </summary>
/// <remarks>
/// Declared as a <c>record class</c>, so callers can derive new option sets via
/// <c>with</c>-expressions: <c>var slow = fast with { Timeout = TimeSpan.FromMinutes(5) };</c>.
/// Equality is structural: <see cref="ProcessGroup"/> is compared by reference, handler
/// delegates by <see cref="Delegate.Equals(object?)"/> (method + target), and the other
/// scalar properties by value.
/// </remarks>
public sealed record ProcessRunOptions
{
	/// <summary>
	/// Source of data piped into the child process's stdin. <c>null</c> or
	/// <see cref="ProcessKit.StandardInput.Empty"/> means stdin is closed right after start.
	/// </summary>
	public StandardInput? StandardInput { get; init; }

	/// <summary>
	/// Optional push-style consumer of stdout lines. Invoked synchronously as each stdout line
	/// is read. Works in parallel to <see cref="IRunningProcess.StdOut"/> and
	/// <see cref="ProcessResult{T}.StdOut"/>; the caller can use any combination. Useful in bulk
	/// methods for teeing output into a logger as it arrives.
	/// </summary>
	public Action<string>? StandardOutputHandler { get; init; }

	/// <summary>
	/// Optional push-style consumer of stderr lines. Invoked synchronously as each stderr line
	/// is read. Works in parallel to <see cref="IRunningProcess.StdErr"/> and
	/// <see cref="ProcessResult{T}.StdErr"/>; the caller can use any combination.
	/// </summary>
	public Action<string>? StandardErrorHandler { get; init; }

	/// <summary>
	/// Optional shared <see cref="ProcessKit.ProcessGroup"/> the new process should join. When
	/// <c>null</c> (default), the runner creates a private <see cref="ProcessKit.ProcessGroup"/>
	/// and disposes it when the operation completes. When non-<c>null</c>, the runner adds the
	/// process to the supplied group and does <strong>not</strong> dispose the group — the caller
	/// retains ownership.
	/// </summary>
	public ProcessGroup? ProcessGroup { get; init; }

	/// <summary>
	/// Optional auto-kill timer. When set, the runner will terminate the process after the
	/// specified duration elapses, as if the caller had cancelled
	/// <c>cancellationToken</c>. The timer is linked to the method's
	/// <see cref="System.Threading.CancellationToken"/> — whichever fires first wins.
	/// </summary>
	public TimeSpan? Timeout { get; init; }

	/// <summary>
	/// Overrides the stdout encoding. Takes precedence over
	/// <see cref="System.Diagnostics.ProcessStartInfo.StandardOutputEncoding"/>. Defaults to UTF-8
	/// when both are <c>null</c>.
	/// </summary>
	public Encoding? StdOutEncoding { get; init; }

	/// <summary>
	/// Overrides the stderr encoding. Takes precedence over
	/// <see cref="System.Diagnostics.ProcessStartInfo.StandardErrorEncoding"/>. Defaults to UTF-8
	/// when both are <c>null</c>.
	/// </summary>
	public Encoding? StdErrEncoding { get; init; }

	/// <summary>
	/// Bounds how much unconsumed stdout/stderr is buffered in memory. <c>null</c> (default) keeps
	/// the historical unbounded behavior for callers that actively consume the streams; set a cap
	/// to protect the "never consumed" case. See <see cref="OutputBufferPolicy"/>.
	/// </summary>
	public OutputBufferPolicy? OutputBuffer { get; init; }

	/// <summary>
	/// Working directory for the process. When set, it takes precedence over any
	/// <see cref="System.Diagnostics.ProcessStartInfo.WorkingDirectory"/> on the supplied PSI (same
	/// override precedence as the encoding options). <c>null</c> leaves the PSI's value as-is.
	/// </summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>
	/// Environment variables applied over the process environment after it is cloned from the PSI;
	/// a <c>null</c> value removes the variable. Entries here take precedence over the PSI's own
	/// environment. <c>null</c> leaves the PSI's environment unchanged.
	/// </summary>
	public IReadOnlyDictionary<string, string?>? Environment { get; init; }

	/// <summary>
	/// How long <see cref="IRunningProcess.DisposeAsync"/> waits for the stdout/stderr/stdin pump
	/// tasks to wind down before giving up. Bounds teardown so a stuck OS pipe cannot hang dispose
	/// forever. <c>null</c> uses the 5-second default.
	/// </summary>
	public TimeSpan? PumpTeardownTimeout { get; init; }

	/// <summary>
	/// Shutdown behavior for the <strong>private</strong> <see cref="ProcessKit.ProcessGroup"/> the
	/// runner creates when <see cref="ProcessGroup"/> is <c>null</c>. Ignored when a caller-owned
	/// <see cref="ProcessGroup"/> is supplied — that group already carries its own options and the
	/// runner never reconfigures it. <c>null</c> uses <see cref="ProcessGroupOptions.Default"/>.
	/// </summary>
	public ProcessGroupOptions? ProcessGroupOptions { get; init; }

	/// <summary>
	/// When <c>true</c>, the child's stdin is left <strong>open</strong> after start so the caller
	/// can write to it over time via <see cref="IRunningProcess.StandardInput"/> (interactive /
	/// REPL processes). The caller must signal end-of-input (<see cref="IProcessStandardInput.CompleteAsync"/>)
	/// or dispose the handle. Default <c>false</c> keeps the "stdin closed at start" contract.
	/// Honored only by <see cref="IProcessRunner.Start(System.Diagnostics.ProcessStartInfo, ProcessRunOptions?, CancellationToken)"/>;
	/// the bulk helpers (which expose no writer) force it off to avoid hanging a stdin-reading child.
	/// </summary>
	public bool KeepStandardInputOpen { get; init; }
}
