using System.Diagnostics;

namespace ProcessKit;

/// <summary>
/// Default <see cref="IProcessRunner"/> implementation. Stateless — safe to share as a singleton.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
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

		return new RunningProcess(startInfo, options, cancellationToken);
	}
}
