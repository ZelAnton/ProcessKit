namespace ProcessKit;

/// <summary>
/// Retry policy for a <see cref="Command"/>'s success-checking verbs (<see cref="Command.RunAsync"/>,
/// <see cref="Command.ExitCodeAsync"/>, <see cref="Command.ProbeAsync"/>). Bulk verbs
/// (<see cref="Command.OutputStringAsync"/> etc.) do NOT retry — they return
/// <see cref="ProcessResult{T}"/> where a non-zero exit code is data, not an exception.
/// </summary>
/// <remarks>
/// <para>
/// Cancellation is ALWAYS terminal: a <see cref="ProcessCancelledException"/> /
/// <see cref="OperationCanceledException"/> skips retry regardless of <see cref="RetryIf"/>.
/// </para>
/// <para>
/// A retried command must use a replayable <see cref="StandardInput"/>
/// (<see cref="StandardInput.FromString"/> / <see cref="StandardInput.FromBytes"/> /
/// <see cref="StandardInput.FromFile"/> / <see cref="StandardInput.FromEnumerable"/>). One-shot
/// sources (<see cref="StandardInput.FromStream"/> / <see cref="StandardInput.FromLines"/>) are
/// consumed on the first attempt; the verb throws <see cref="System.InvalidOperationException"/>
/// before the first attempt when paired with a <see cref="RetryPolicy"/>.
/// </para>
/// </remarks>
public sealed record RetryPolicy
{
	/// <summary>Total attempts (initial + retries). Must be at least 1.</summary>
	public int MaxAttempts { get; init; }

	/// <summary>Base delay BEFORE the second attempt. Successive attempts scale by
	/// <see cref="BackoffFactor"/> and are capped at <see cref="MaxBackoff"/>.</summary>
	public TimeSpan Backoff { get; init; }

	/// <summary>Multiplier per retry. Default 2.0 (classic exponential).</summary>
	public double BackoffFactor { get; init; } = 2.0;

	/// <summary>Cap on the computed backoff. <c>null</c> means no cap.</summary>
	public TimeSpan? MaxBackoff { get; init; }

	/// <summary>When true, multiply each delay by a random factor in <c>[0.5, 1.5)</c> — helps a
	/// fleet of retriers avoid synchronized stampedes. Default true.</summary>
	public bool Jitter { get; init; } = true;

	/// <summary>
	/// Optional classifier deciding whether a given exception should trigger a retry. Default
	/// <c>null</c> retries on every non-cancellation exception. Cancellation
	/// (<see cref="OperationCanceledException"/> / <see cref="ProcessCancelledException"/>) is
	/// ALWAYS terminal regardless of what this predicate returns.
	/// </summary>
	public Predicate<Exception>? RetryIf { get; init; }

	public RetryPolicy(int maxAttempts, TimeSpan backoff)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(backoff, TimeSpan.Zero);
		MaxAttempts = maxAttempts;
		Backoff = backoff;
	}

	/// <summary>
	/// Computes the delay before the (<paramref name="previousAttempts"/> + 1)-th attempt — i.e.
	/// the delay observed AFTER attempt N is awaited. <paramref name="previousAttempts"/>=1 means
	/// the result is <see cref="Backoff"/> (delay before attempt 2); =2 means
	/// <c>Backoff × BackoffFactor</c> (before attempt 3); etc. Capped at <see cref="MaxBackoff"/>
	/// when configured, and optionally jittered.
	/// </summary>
	internal TimeSpan ComputeDelay(int previousAttempts)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(previousAttempts, 1);

		// Base = Backoff × Factor^(previousAttempts - 1). Use Math.Pow rather than a manual loop
		// to keep AOT-friendly and overflow-aware (TimeSpan.MaxValue clamping).
		var multiplier = Math.Pow(BackoffFactor, previousAttempts - 1);
		var baseDelayTicks = Backoff.Ticks * multiplier;

		// Clamp against MaxBackoff before applying jitter so jitter's [0.5, 1.5) is anchored to
		// the cap — otherwise a jittered delay could exceed the cap by 50%.
		if (MaxBackoff is { } cap)
			baseDelayTicks = Math.Min(baseDelayTicks, cap.Ticks);

		if (Jitter)
		{
			// [0.5, 1.5) — same range as Rust supervisor defaults / Polly's "DecorrelatedJitter".
			var jitter = Random.Shared.NextDouble() + 0.5;
			baseDelayTicks *= jitter;
		}

		// Defensive clamp on overflow (e.g. Factor=10, attempts=20 → Math.Pow overflows TimeSpan).
		if (baseDelayTicks <= 0)
			return TimeSpan.Zero;
		if (baseDelayTicks >= long.MaxValue)
			return TimeSpan.MaxValue;
		return TimeSpan.FromTicks((long)baseDelayTicks);
	}
}
