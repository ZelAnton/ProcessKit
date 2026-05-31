using System.Diagnostics;

namespace ProcessKit;

/// <summary>
/// Default <see cref="IProcessRunner"/> implementation. Stateless apart from optional baseline
/// options — safe to share as a singleton.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
	/// <summary>Creates a runner with no baseline options (per-call options used as-is).</summary>
	public ProcessRunner() { }

	/// <summary>
	/// Creates a runner with baseline <paramref name="defaults"/> applied to every call. Per-call
	/// options override field-by-field (the environment is unioned, per-call key winning). Useful
	/// for DI: register one runner with org-wide defaults (timeout, encoding, buffer policy, …).
	/// </summary>
	/// <remarks>
	/// Defaults flow to every overload (including <see cref="ProcessRunOptions.WorkingDirectory"/> /
	/// <see cref="ProcessRunOptions.Environment"/>, which are applied during start-info preparation).
	/// <see cref="ProcessRunOptions.KeepStandardInputOpen"/> is the one exception — it is never
	/// inherited from defaults, since interactive stdin is a per-call decision.
	/// </remarks>
	public ProcessRunner(ProcessRunOptions defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);
		Defaults = defaults;
	}

	// Baseline options, exposed so the bytes path (which bypasses Start) can apply them too.
	internal ProcessRunOptions? Defaults { get; }

	/// <summary>
	/// A shared, ready-to-use <see cref="IProcessRunner"/> instance for callers that don't use
	/// dependency injection. Thread-safe (the runner itself is stateless; each
	/// <see cref="Start"/> call produces an independent <see cref="IRunningProcess"/>).
	/// </summary>
	public static IProcessRunner Default { get; } = new ProcessRunner();

	public IRunningProcess Start(
		ProcessStartInfo startInfo,
		ProcessRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(startInfo);

		return new RunningProcess(startInfo, ProcessRunOptionsMerge.Merge(Defaults, options), cancellationToken);
	}
}
