namespace ProcessKit;

/// <summary>
/// Time abstraction for retry/backoff loops (and, in a later phase, the supervisor's restart
/// scheduling). Production code uses the implicit <see cref="SystemClock"/>; tests can inject a
/// fake clock to drive virtual time without real waits.
/// </summary>
/// <remarks>
/// Currently only the retry path in <see cref="Command"/> verbs consumes this seam — existing
/// callers of <see cref="Task.Delay(System.TimeSpan, System.Threading.CancellationToken)"/> and
/// <see cref="System.Diagnostics.Stopwatch"/> in <see cref="IRunningProcess"/> probes and the
/// internal session intentionally remain on BCL primitives. Phase 10 (Supervisor) will extend the
/// seam to cover restart scheduling.
/// </remarks>
public interface IClock
{
	/// <summary>The current UTC time according to this clock.</summary>
	DateTimeOffset UtcNow { get; }

	/// <summary>
	/// Asynchronously waits for <paramref name="duration"/> to elapse (or honours
	/// <paramref name="cancellationToken"/>). A <see cref="SystemClock"/> implementation defers
	/// to <see cref="Task.Delay(System.TimeSpan, System.Threading.CancellationToken)"/>.
	/// </summary>
	Task Delay(TimeSpan duration, CancellationToken cancellationToken);
}

internal sealed class SystemClock : IClock
{
	public static readonly IClock Instance = new SystemClock();

	SystemClock() { }

	public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

	public Task Delay(TimeSpan duration, CancellationToken cancellationToken) =>
		Task.Delay(duration, cancellationToken);
}
