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
	/// Working directory for the convenience <c>Start(executable, arguments, …)</c> overloads.
	/// Ignored when a full <see cref="System.Diagnostics.ProcessStartInfo"/> is supplied — set
	/// <see cref="System.Diagnostics.ProcessStartInfo.WorkingDirectory"/> on the PSI instead.
	/// <c>null</c> inherits the current directory.
	/// </summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>
	/// Environment variables for the convenience <c>Start(executable, arguments, …)</c> overloads,
	/// applied over the inherited environment; a <c>null</c> value removes the variable. Ignored
	/// when a full <see cref="System.Diagnostics.ProcessStartInfo"/> is supplied — set
	/// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> on the PSI instead.
	/// </summary>
	public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}
