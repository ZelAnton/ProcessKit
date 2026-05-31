using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using JetBrains.Annotations;

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

			return runner.Start(
				startInfo: BuildStartInfo(executable, arguments),
				options,
				cancellationToken);
		}

		public IAsyncEnumerable<string> GetOutputAsync(
			ProcessStartInfo startInfo,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetOutputAsyncCore(
				runner,
				psi: startInfo,
				options,
				cancellationToken);
		}

		public IAsyncEnumerable<string> GetOutputAsync(
			string executable,
			IEnumerable<string> arguments,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetOutputAsyncCore(
				runner,
				psi: BuildStartInfo(executable, arguments),
				options,
				cancellationToken);
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
		await using var p = runner.Start(psi, CloseStdinForBulk(options), cancellationToken);
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

			return GetFirstLineOutputAsyncCore(
				runner,
				psi: startInfo,
				predicate,
				options,
				cancellationToken);
		}

		public Task<string?> GetFirstLineOutputAsync(
			string executable,
			IEnumerable<string> arguments,
			Func<string, bool>? predicate = null,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetFirstLineOutputAsyncCore(
				runner,
				psi: BuildStartInfo(executable, arguments),
				predicate,
				options,
				cancellationToken);
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

			return GetFullOutputAsyncCore(
				runner,
				psi: BuildStartInfo(executable, arguments),
				options,
				cancellationToken);
		}
	}

	static async Task<ProcessResult<string>> GetFullOutputAsyncCore(
		IProcessRunner runner,
		ProcessStartInfo psi,
		ProcessRunOptions? options,
		CancellationToken cancellationToken)
	{
		// Faithful capture: decode the exact stdout/stderr text (preserving line endings and any
		// trailing newline) via TextBufferSinks, instead of reconstructing from the line channel.
		// Bypasses runner.Start (which yields the line-oriented handle), so apply runner defaults here.
		if (runner is ProcessRunner concrete)
			options = ProcessRunOptionsMerge.Merge(concrete.Defaults, options);
		options = CloseStdinForBulk(options);

		var stdoutSink = new TextBufferSink();
		var stderrSink = new TextBufferSink();
		await using var session = new ProcessSession(
			startInfo: psi,
			options,
			stdoutSink,
			handleFactory: RealProcessHandleFactory.Instance,
			cancellationToken,
			stderrSink);

		// Await both pumps before reading the captured text — Completion can resolve before the pipes
		// are fully drained.
		await session.StdOutPumpCompletion.ConfigureAwait(false);
		await session.StdErrPumpCompletion.ConfigureAwait(false);
		var exit = await session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);

		return new ProcessResult<string>(
			StdOut: stdoutSink.Text,
			StdErr: stderrSink.Text,
			ExitCode: exit)
		{
			WasTimedOut = session.WasTimedOut,
		};
	}

	static async Task<ProcessResult<string>> ToResultAsyncCore(IRunningProcess p, CancellationToken cancellationToken)
	{
		var stdoutTask = AccumulateLinesAsync(p.StdOut, cancellationToken);
		var stderrTask = AccumulateLinesAsync(p.StdErr, cancellationToken);

		// Drain first via Task.WhenAll so both drain tasks are always awaited together —
		// otherwise a cancellation OCE from Completion.WaitAsync would leave them orphaned
		// and any exception they throw would become unobserved. By the time both drains
		// complete, the channels have closed, which means the process has exited and
		// _completionTcs has been resolved — so the subsequent await returns immediately.
		await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
		var exit = await p.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
		return new ProcessResult<string>(
			StdOut: stdoutTask.Result,
			StdErr: stderrTask.Result,
			ExitCode: exit)
		{
			WasTimedOut = p.WasTimedOut,
		};
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

			return GetBytesOutputAsyncCore(
				runner,
				source: startInfo,
				options,
				cancellationToken);
		}

		public Task<ProcessResult<byte[]>> GetBytesOutputAsync(
			string executable,
			IEnumerable<string> arguments,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetBytesOutputAsyncCore(
				runner,
				source: BuildStartInfo(executable, arguments),
				options,
				cancellationToken);
		}
	}

	static async Task<ProcessResult<byte[]>> GetBytesOutputAsyncCore(
		IProcessRunner runner,
		ProcessStartInfo source,
		ProcessRunOptions? options,
		CancellationToken cancellationToken)
	{
		// This path bypasses runner.Start (it needs a raw byte sink, not the line handle), so apply
		// the runner's baseline options here too — otherwise GetBytesOutputAsync would silently
		// ignore them while the other bulk helpers honor them.
		if (runner is ProcessRunner concrete)
			options = ProcessRunOptionsMerge.Merge(concrete.Defaults, options);

		// Raw bytes for stdout; faithful decoded text for stderr (preserving its exact line endings).
		var stdoutSink = new ByteBufferStdOutSink();
		var stderrSink = new TextBufferSink();
		await using var session = new ProcessSession(
			startInfo: source,
			options: CloseStdinForBulk(options),
			stdOutSink: stdoutSink,
			handleFactory: RealProcessHandleFactory.Instance,
			cancellationToken: cancellationToken,
			stdErrSink: stderrSink);

		// Await both pumps before reading the captured output — Completion can resolve before the
		// pipes are fully drained.
		await session.StdOutPumpCompletion.ConfigureAwait(false);
		await session.StdErrPumpCompletion.ConfigureAwait(false);
		var exit = await session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);

		return new ProcessResult<byte[]>(
			StdOut: stdoutSink.ToArray(),
			StdErr: stderrSink.Text,
			ExitCode: exit)
		{
			WasTimedOut = session.WasTimedOut,
		};
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
		// Nobody consumes the output here, so default to discarding it (memory-flat) unless the
		// caller explicitly asked for a buffer policy. The pumps still drain the OS pipe. Also force
		// stdin closed — this path exposes no writer.
		options = CloseStdinForBulk(options) ?? new ProcessRunOptions();
		if (options.OutputBuffer is null)
			options = options with { OutputBuffer = new OutputBufferPolicy { MaxBufferedLines = 0 } };

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

		[UsedImplicitly]
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
	// Handle-level conveniences
	// ──────────────────────────────────────────────────────────────────────────────────────

	extension(IRunningProcess process)
	{
		/// <summary>
		/// Drains stdout and stderr from an already-started handle and returns the captured result.
		/// Does <strong>not</strong> dispose the handle; the caller still owns it.
		/// </summary>
		/// <remarks>
		/// Because it consumes the handle's line-oriented streams, the captured text is reconstructed
		/// from lines: line endings are normalized to <see cref="System.Environment.NewLine"/> and the
		/// trailing newline is dropped. For byte-exact / formatting-preserving capture, use
		/// <see cref="GetFullOutputAsync(IProcessRunner, ProcessStartInfo, ProcessRunOptions?, CancellationToken)"/>
		/// (faithful) or <see cref="GetBytesOutputAsync(IProcessRunner, ProcessStartInfo, ProcessRunOptions?, CancellationToken)"/>.
		/// </remarks>
		public Task<ProcessResult<string>> ToResultAsync(CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(process);

			return ToResultAsyncCore(process, cancellationToken);
		}

		/// <summary>
		/// Awaits <see cref="IRunningProcess.Completion"/> and returns the exit code, but throws
		/// <see cref="TimeoutException"/> if the process was killed because
		/// <see cref="ProcessRunOptions.Timeout"/> elapsed — removing the ambiguity between a timeout
		/// kill and a process that genuinely exited with a kill-like code.
		/// </summary>
		public async Task<int> CompletionOrThrowAsync(CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(process);

			var code = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
			return !process.WasTimedOut
				? code
				: throw new TimeoutException("The process was killed because its configured timeout elapsed.");
		}
	}

	// ──────────────────────────────────────────────────────────────────────────────────────

	// The bulk helpers expose no stdin writer, so leaving stdin open would hang a stdin-reading
	// child forever. Force KeepStandardInputOpen off — interactive stdin is only meaningful through
	// IProcessRunner.Start, which surfaces IRunningProcess.StandardInput.
	static ProcessRunOptions? CloseStdinForBulk(ProcessRunOptions? options)
		=> options is { KeepStandardInputOpen: true } ? options with { KeepStandardInputOpen = false } : options;

	static ProcessStartInfo BuildStartInfo(string executable, IEnumerable<string> arguments)
	{
		ArgumentException.ThrowIfNullOrEmpty(executable);
		ArgumentNullException.ThrowIfNull(arguments);

		// Only FileName + args here. WorkingDirectory / Environment from options are applied by
		// PrepareStartInfo so they take effect for every overload (and pick up runner defaults).
		var psi = new ProcessStartInfo(executable);
		foreach (var arg in arguments)
			psi.ArgumentList.Add(arg);
		return psi;
	}
}
