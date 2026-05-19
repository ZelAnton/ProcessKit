using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace ProcessKit;

/// <summary>
/// Convenience helpers built on top of <see cref="IProcessRunner.Start(ProcessStartInfo, ProcessRunOptions?, CancellationToken)"/>.
/// </summary>
[SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case",
	Justification = "False positive on C# 14 'extension(...)' blocks — the analyzer mistakes the per-block synthesised type names for case-only differences.")]
public static class ProcessRunnerExtensions
{
	// ──────────────────────────────────────────────────────────────────────────────────────
	// Start convenience overload
	// ──────────────────────────────────────────────────────────────────────────────────────

	extension(IProcessRunner runner)
	{
		/// <summary>
		/// Convenience overload that builds a <see cref="ProcessStartInfo"/> from
		/// <paramref name="executable"/> and <paramref name="arguments"/> and starts it.
		/// </summary>
		public IRunningProcess Start(
			string executable,
			IEnumerable<string> arguments,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return runner.Start(BuildStartInfo(executable, arguments), options, cancellationToken);
		}

		public IAsyncEnumerable<string> GetOutputAsync(
			ProcessStartInfo startInfo,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetOutputAsyncCore(runner, startInfo, options, cancellationToken);
		}

		public IAsyncEnumerable<string> GetOutputAsync(
			string executable,
			IEnumerable<string> arguments,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetOutputAsyncCore(runner, BuildStartInfo(executable, arguments), options, cancellationToken);
		}
	}

	// ──────────────────────────────────────────────────────────────────────────────────────
	// Streaming stdout (line by line)
	// ──────────────────────────────────────────────────────────────────────────────────────

	static async IAsyncEnumerable<string> GetOutputAsyncCore(
		IProcessRunner runner,
		ProcessStartInfo psi,
		ProcessRunOptions? options,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var p = runner.Start(psi, options, cancellationToken);
		await foreach (var line in p.StdOut.WithCancellation(cancellationToken).ConfigureAwait(false))
			yield return line;
	}

	// ──────────────────────────────────────────────────────────────────────────────────────
	// First matching line
	// ──────────────────────────────────────────────────────────────────────────────────────

	extension(IProcessRunner runner)
	{
		public Task<string?> GetFirstLineOutputAsync(
			ProcessStartInfo startInfo,
			Func<string, bool>? predicate = null,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetFirstLineOutputAsyncCore(runner, startInfo, predicate, options, cancellationToken);
		}

		public Task<string?> GetFirstLineOutputAsync(
			string executable,
			IEnumerable<string> arguments,
			Func<string, bool>? predicate = null,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetFirstLineOutputAsyncCore(runner, BuildStartInfo(executable, arguments), predicate, options, cancellationToken);
		}
	}

	static async Task<string?> GetFirstLineOutputAsyncCore(
		IProcessRunner runner,
		ProcessStartInfo psi,
		Func<string, bool>? predicate,
		ProcessRunOptions? options,
		CancellationToken cancellationToken)
	{
		predicate ??= static _ => true;
		await foreach (var line in GetOutputAsyncCore(runner, psi, options, cancellationToken).ConfigureAwait(false))
			if (predicate(line))
				return line;
		return null;
	}

	// ──────────────────────────────────────────────────────────────────────────────────────
	// Bulk: full stdout + stderr + exit code
	// ──────────────────────────────────────────────────────────────────────────────────────

	extension(IProcessRunner runner)
	{
		public Task<ProcessResult<string>> GetFullOutputAsync(
			ProcessStartInfo startInfo,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetFullOutputAsyncCore(runner, startInfo, options, cancellationToken);
		}

		public Task<ProcessResult<string>> GetFullOutputAsync(
			string executable,
			IEnumerable<string> arguments,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetFullOutputAsyncCore(runner, BuildStartInfo(executable, arguments), options, cancellationToken);
		}
	}

	static async Task<ProcessResult<string>> GetFullOutputAsyncCore(
		IProcessRunner runner,
		ProcessStartInfo psi,
		ProcessRunOptions? options,
		CancellationToken cancellationToken)
	{
		await using var p = runner.Start(psi, options, cancellationToken);
		var stdoutTask = AccumulateLinesAsync(p.StdOut, cancellationToken);
		var stderrTask = AccumulateLinesAsync(p.StdErr, cancellationToken);

		// Drain first via Task.WhenAll so both drain tasks are always awaited together —
		// otherwise a cancellation OCE from Completion.WaitAsync would leave them orphaned
		// and any exception they throw would become unobserved. By the time both drains
		// complete, the channels have closed, which means the process has exited and
		// _completionTcs has been resolved — so the subsequent await returns immediately.
		await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
		var exit = await p.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
		return new ProcessResult<string>(stdoutTask.Result, stderrTask.Result, exit) { WasTimedOut = p.WasTimedOut };
	}

	static async Task<string> AccumulateLinesAsync(IAsyncEnumerable<string> lines, CancellationToken cancellationToken)
	{
		var sb = new StringBuilder();
		await foreach (var line in lines.WithCancellation(cancellationToken).ConfigureAwait(false))
		{
			if (sb.Length > 0)
				sb.AppendLine();
			sb.Append(line);
		}
		return sb.ToString();
	}

	// ──────────────────────────────────────────────────────────────────────────────────────
	// Bulk: bytes (special-cased — bypasses line-based handle)
	// ──────────────────────────────────────────────────────────────────────────────────────

	extension(IProcessRunner runner)
	{
		public Task<ProcessResult<byte[]>> GetBytesOutputAsync(
			ProcessStartInfo startInfo,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);
			ArgumentNullException.ThrowIfNull(startInfo);

			return GetBytesOutputAsyncCore(startInfo, options, cancellationToken);
		}

		public Task<ProcessResult<byte[]>> GetBytesOutputAsync(
			string executable,
			IEnumerable<string> arguments,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetBytesOutputAsyncCore(BuildStartInfo(executable, arguments), options, cancellationToken);
		}
	}

	static async Task<ProcessResult<byte[]>> GetBytesOutputAsyncCore(
		ProcessStartInfo source,
		ProcessRunOptions? options,
		CancellationToken cancellationToken)
	{
		var psi = PipePumpHelpers.PrepareStartInfo(source, options);

		var ownsGroup = options?.ProcessGroup is null;
		var group = options?.ProcessGroup ?? new ProcessGroup();

		using var killCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		// Separate timeout CTS so we can distinguish "kill due to Options.Timeout" from
		// "kill due to caller cancellation" — needed for ProcessResult.WasTimedOut.
		using var timeoutCts = options?.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
		CancellationTokenRegistration timeoutRegistration = default;
		if (timeoutCts is not null)
		{
			timeoutRegistration = timeoutCts.Token.UnsafeRegister(static state =>
			{
				try
				{
					((CancellationTokenSource)state!).Cancel();
				}
				catch (ObjectDisposedException)
				{
					// runner is being torn down concurrently — killCts already disposed; nothing to cancel.
				}
			}, killCts);
		}

		try
		{
			using var process = group.Start(psi, killCts.Token);

			var stdinTask = PipePumpHelpers.WriteStandardInputAsync(process, options?.StandardInput, killCts.Token);

			var stderrBuffer = new StringBuilder();
			var stderrTask = PipePumpHelpers.ReadIntoBufferAsync(
				process.StandardError, stderrBuffer, options?.StandardErrorHandler, killCts.Token);

			using var stdoutBuffer = new MemoryStream();
			try
			{
				await process.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer, killCts.Token).ConfigureAwait(false);
			}
			catch (SystemException e) when (e is OperationCanceledException or IOException)
			{
				// ignored - killed via token
			}

			await PipePumpHelpers.ObserveAsync(stderrTask).ConfigureAwait(false);
			await PipePumpHelpers.ObserveAsync(stdinTask).ConfigureAwait(false);
			await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

			int exitCode;
			try
			{
				exitCode = process.ExitCode;
			}
			catch (InvalidOperationException)
			{
				exitCode = -1;
			}

			var wasTimedOut = timeoutCts?.IsCancellationRequested ?? false;
			return new ProcessResult<byte[]>(stdoutBuffer.ToArray(), stderrBuffer.ToString(), exitCode) { WasTimedOut = wasTimedOut };
		}
		finally
		{
			timeoutRegistration.Dispose();
			if (ownsGroup)
				await group.DisposeAsync().ConfigureAwait(false);
		}
	}

	// ──────────────────────────────────────────────────────────────────────────────────────
	// Exit-only
	// ──────────────────────────────────────────────────────────────────────────────────────

	extension(IProcessRunner runner)
	{
		public Task<int> GetExitCodeAsync(
			ProcessStartInfo startInfo,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetExitCodeAsyncCore(runner, startInfo, options, cancellationToken);
		}

		public Task<int> GetExitCodeAsync(
			string executable,
			IEnumerable<string> arguments,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetExitCodeAsyncCore(runner, BuildStartInfo(executable, arguments), options, cancellationToken);
		}
	}

	static async Task<int> GetExitCodeAsyncCore(
		IProcessRunner runner,
		ProcessStartInfo psi,
		ProcessRunOptions? options,
		CancellationToken cancellationToken)
	{
		await using var p = runner.Start(psi, options, cancellationToken);

		var drainStdOut = Task.Run(async () =>
		{
			await foreach (var _ in p.StdOut.WithCancellation(cancellationToken).ConfigureAwait(false))
			{
				// discard
			}
		}, cancellationToken);

		var drainStdErr = Task.Run(async () =>
		{
			await foreach (var _ in p.StdErr.WithCancellation(cancellationToken).ConfigureAwait(false))
			{
				// discard
			}
		}, cancellationToken);

		await Task.WhenAll(drainStdOut, drainStdErr).ConfigureAwait(false);

		return await p.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	// ──────────────────────────────────────────────────────────────────────────────────────
	// Sync wrappers (convenience form only)
	// ──────────────────────────────────────────────────────────────────────────────────────

	extension(IProcessRunner runner)
	{
		public string GetOutput(
			string executable,
			IEnumerable<string> arguments,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
			=> runner.GetFullOutputAsync(executable, arguments, options, cancellationToken).GetAwaiter().GetResult().StdOut;

		public string? GetFirstLineOutput(
			string executable,
			IEnumerable<string> arguments,
			Func<string, bool>? predicate = null,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
			=> runner.GetFirstLineOutputAsync(executable, arguments, predicate, options, cancellationToken).GetAwaiter().GetResult();
	}

	// ──────────────────────────────────────────────────────────────────────────────────────
	// Fluent error handling for Task<ProcessResult<T>>
	// ──────────────────────────────────────────────────────────────────────────────────────

	extension<T>(Task<ProcessResult<T>> task)
	{
		/// <summary>
		/// Awaits the task and calls <see cref="ProcessResult{T}.EnsureSuccess"/> on the result —
		/// throws <see cref="ProcessExitException"/> when the process exited with a non-zero code.
		/// Enables fluent chains like
		/// <c>(await runner.GetFullOutputAsync(...).EnsureSuccessAsync()).StdOut</c>.
		/// </summary>
		public async Task<ProcessResult<T>> EnsureSuccessAsync()
			=> (await task.ConfigureAwait(false)).EnsureSuccess();
	}

	// ──────────────────────────────────────────────────────────────────────────────────────

	static ProcessStartInfo BuildStartInfo(string executable, IEnumerable<string> arguments)
	{
		ArgumentException.ThrowIfNullOrEmpty(executable);
		ArgumentNullException.ThrowIfNull(arguments);

		var psi = new ProcessStartInfo(executable);
		foreach (var arg in arguments)
			psi.ArgumentList.Add(arg);
		return psi;
	}
}
