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
		await using var p = runner.Start(psi, CloseStdinForBulk(options), cancellationToken);
		return await ToResultAsyncCore(p, cancellationToken).ConfigureAwait(false);
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

			return GetBytesOutputAsyncCore(runner, startInfo, options, cancellationToken);
		}

		public Task<ProcessResult<byte[]>> GetBytesOutputAsync(
			string executable,
			IEnumerable<string> arguments,
			ProcessRunOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(runner);

			return GetBytesOutputAsyncCore(runner, BuildStartInfo(executable, arguments), options, cancellationToken);
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

		// Bytes path runs on the same ProcessSession lifecycle as the line handle, but with a raw
		// byte sink for stdout instead of the line channel. The session disposes the sink.
		var sink = new ByteBufferStdOutSink();
		await using var session = new ProcessSession(source, CloseStdinForBulk(options), sink, RealProcessHandleFactory.Instance, cancellationToken);

		// Await the raw stdout copy before reading the buffer — Completion can resolve before the
		// pipe is fully drained. stderr is always line-oriented; accumulate it into a string.
		await session.StdOutPumpCompletion.ConfigureAwait(false);
		var stderr = await AccumulateLinesAsync(session.StdErr, cancellationToken).ConfigureAwait(false);
		var exit = await session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);

		return new ProcessResult<byte[]>(sink.ToArray(), stderr, exit) { WasTimedOut = session.WasTimedOut };
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
		/// Drains stdout and stderr to completion and returns the captured result — the same work
		/// <see cref="GetFullOutputAsync(IProcessRunner, ProcessStartInfo, ProcessRunOptions?, CancellationToken)"/>
		/// does, but from an already-started handle. Does <strong>not</strong> dispose the handle;
		/// the caller still owns it.
		/// </summary>
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
			if (process.WasTimedOut)
				throw new TimeoutException("The process was killed because its configured timeout elapsed.");
			return code;
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
