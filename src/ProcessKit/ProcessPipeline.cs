using System.Diagnostics;
using ProcessKit.Diagnostics;

namespace ProcessKit;

/// <summary>
/// Shell-free <c>a | b | c</c> pipeline composing N <see cref="Command"/> stages. Each stage is a
/// separate OS process; stdout of stage N is wired into stdin of stage N+1 at the byte level (no
/// shell quoting / injection). All stages live inside one private <see cref="ProcessGroup"/>, so
/// kill-on-dispose tears the whole chain down atomically.
/// </summary>
/// <remarks>
/// <para>
/// Build by chaining: <c>Command.Create("a").Pipe(Command.Create("b")).Pipe(...).RunAsync()</c>.
/// Terminal verbs are <see cref="OutputStringAsync"/> and <see cref="RunAsync"/>.
/// </para>
/// <para>
/// Pipefail semantics (matches Rust v0.7.1 <c>Pipeline::pipefail</c>): the first inner stage with
/// <see cref="ProcessResult{T}.ExitCode"/> != 0 attributes the result's <c>ExitCode</c>,
/// <c>StdErr</c>, and program name; otherwise the last stage. Stdout always comes from the LAST
/// stage (the pipeline's final output).
/// </para>
/// <para>
/// Stage caveats: <see cref="Command.StandardInput"/> is honored only on stage 0 (middle/last
/// stages receive stdin from the upstream pipe). Inner stages' stdout cannot be observed — their
/// data flows into the next stage. <see cref="Command.WithProcessGroup"/> is incompatible with a
/// pipeline (the pipeline owns the containment group) and triggers
/// <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
public sealed record ProcessPipeline
{
	public IReadOnlyList<Command> Stages { get; init; } = [];

	public TimeSpan? Timeout { get; init; }

	public CancellationToken Cancellation { get; init; }

	/// <summary>Adds another stage to the chain. Each new stage's stdin is wired to the previous
	/// stage's stdout at run time.</summary>
	public ProcessPipeline Pipe(Command next)
	{
		ArgumentNullException.ThrowIfNull(next);
		return this with { Stages = [.. Stages, next] };
	}

	public ProcessPipeline WithTimeout(TimeSpan timeout)
	{
		if (timeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
		return this with { Timeout = timeout };
	}

	public ProcessPipeline WithCancellation(CancellationToken token) => this with { Cancellation = token };

	/// <summary>Runs the pipeline and captures stdout (decoded text) from the last stage, plus
	/// the winning stage's stderr and exit code (see pipefail semantics on the type).</summary>
	/// <remarks>
	/// <see cref="Command.StandardOutputHandler"/> on the LAST stage is NOT honoured (the last
	/// stage's stdout is consumed as raw text by this verb). <see cref="Command.StandardOutputHandler"/>
	/// on inner stages is meaningless — their stdout goes into the next stage's stdin, not into a
	/// channel. <see cref="Command.StandardErrorHandler"/> is currently ignored on every stage
	/// (pipeline drains stderr via <c>ReadToEndAsync</c>). To observe per-stage stderr, inspect the
	/// returned <see cref="ProcessResult{T}.StdErr"/> after the run.
	/// </remarks>
	public Task<ProcessResult<string>> OutputStringAsync() => OutputStringCoreAsync();

	/// <summary>Runs the pipeline, requires every stage to exit 0 (pipefail), and returns the last
	/// stage's stdout with trailing newline stripped.</summary>
	public async Task<string> RunAsync()
	{
		var result = await OutputStringCoreAsync().ConfigureAwait(false);
		result.EnsureSuccess();
		return result.StdOut.TrimEnd('\r', '\n');
	}

	async Task<ProcessResult<string>> OutputStringCoreAsync()
	{
		if (Stages.Count < 2)
			throw new InvalidOperationException("Pipeline requires at least 2 stages.");
		foreach (var stage in Stages)
		{
			if (stage.ProcessGroup is not null)
				throw new InvalidOperationException(
					"Pipeline stages may not use WithProcessGroup — the pipeline owns the containment group.");
			if (stage.KeepStandardInputOpen)
				throw new InvalidOperationException(
					"Pipeline stages may not use WithKeepStandardInputOpen — stage stdin is wired to the upstream pipe (stage 0) or driven from the pipeline harness.");
		}

		var pipelineName = PipelineName();
		using var activity = ProcessKitActivitySource.Source.StartActivity(
			"processkit.pipeline.run",
			ActivityKind.Internal);
		activity?.SetTag("stage_count", Stages.Count);
		if (Timeout is { } configured)
			activity?.SetTag("timeout_ms", (long)configured.TotalMilliseconds);

		// Pre-cancel translates to ProcessCancelledException up front so callers see the same
		// distinct type whether the token was cancelled before or after the spawn loop.
		if (Cancellation.IsCancellationRequested)
		{
			activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
			throw new ProcessCancelledException(pipelineName, Cancellation);
		}

		await using var group = new ProcessGroup();
		using var timeoutCts = Timeout is { } timeoutValue ? new CancellationTokenSource(timeoutValue) : null;
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
			Cancellation,
			timeoutCts?.Token ?? CancellationToken.None);
		var ct = linkedCts.Token;

		var processes = new Process[Stages.Count];
		var stderrTasks = new Task<string>[Stages.Count];
		var relayTasks = new Task[Stages.Count - 1];
		Task<string>? lastStdoutTask = null;
		Task? firstStdinTask = null;

		try
		{
			try
			{
				for (var i = 0; i < Stages.Count; i++)
				{
					var psi = BuildStagePsi(Stages[i]);
					processes[i] = group.Start(psi, ct);
				}

				for (var i = 0; i < Stages.Count - 1; i++)
				{
					var src = processes[i].StandardOutput.BaseStream;
					var dst = processes[i + 1].StandardInput.BaseStream;
					relayTasks[i] = RelayAsync(src, dst, processes[i + 1].StandardInput, ct);
				}

				firstStdinTask = FeedFirstStdinAsync(Stages[0].StandardInput, processes[0].StandardInput, ct);

				for (var i = 0; i < Stages.Count; i++)
					stderrTasks[i] = processes[i].StandardError.ReadToEndAsync(ct);

				lastStdoutTask = processes[^1].StandardOutput.ReadToEndAsync(ct);

				var waits = new Task[Stages.Count];
				for (var i = 0; i < Stages.Count; i++)
					waits[i] = processes[i].WaitForExitAsync(ct);
				await Task.WhenAll(waits).ConfigureAwait(false);
				await Task.WhenAll(relayTasks).ConfigureAwait(false);
				await firstStdinTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
			{
				await ObserveTeardownAsync(group, stderrTasks, lastStdoutTask, relayTasks, firstStdinTask).ConfigureAwait(false);
				activity?.SetTag("timed_out", true);
				activity?.SetStatus(ActivityStatusCode.Error, "timed out");
				return new ProcessResult<string>(string.Empty, string.Empty, -1) { WasTimedOut = true };
			}
			catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
			{
				await ObserveTeardownAsync(group, stderrTasks, lastStdoutTask, relayTasks, firstStdinTask).ConfigureAwait(false);
				activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
				throw new ProcessCancelledException(pipelineName, Cancellation);
			}

			var stderrs = new string[Stages.Count];
			var codes = new int[Stages.Count];
			for (var i = 0; i < Stages.Count; i++)
			{
				stderrs[i] = await stderrTasks[i].ConfigureAwait(false);
				codes[i] = processes[i].ExitCode;
			}
			var lastStdout = await lastStdoutTask!.ConfigureAwait(false);

			// Pipefail: the first non-zero inner stage (index 0..N-2) attributes the result. If all
			// inner stages succeeded, the last stage attributes (covers both success and final-stage
			// failure).
			var winner = Stages.Count - 1;
			for (var i = 0; i < Stages.Count - 1; i++)
			{
				if (codes[i] != 0)
				{
					winner = i;
					break;
				}
			}

			activity?.SetTag("timed_out", false);
			activity?.SetTag("winner_index", winner);
			activity?.SetTag("winner_program", Path.GetFileName(Stages[winner].Program));
			activity?.SetTag("exit_code", codes[winner]);

			return new ProcessResult<string>(lastStdout, stderrs[winner], codes[winner]);
		}
		finally
		{
			// Dispose every Process we successfully spawned so its three redirected pipe handles
			// (stdin/stdout/stderr) and the OS process handle are released immediately rather than
			// at the next GC pass. The group's own Dispose only kills the children — it does not
			// own the Process objects.
			foreach (var p in processes)
			{
				if (p is null)
					continue;
				try
				{
					p.Dispose();
				}
				catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
					// Process.Dispose's contract doesn't throw under normal conditions; the
					// observable exception types are InvalidOperationException (already disposed /
					// no associated process) and Win32Exception (OS handle close fails on Windows).
					// Both are benign during teardown — we must not propagate from a finally that
					// would mask the pipeline's actual result.
				}
			}
		}
	}

	static ProcessStartInfo BuildStagePsi(Command stage)
	{
		var psi = new ProcessStartInfo(stage.Program)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = stage.CreateNoWindow,
		};
		foreach (var arg in stage.Arguments)
			psi.ArgumentList.Add(arg);
		if (stage.WorkingDirectory is not null)
			psi.WorkingDirectory = stage.WorkingDirectory;

		// Apply InheritEnvironment allow-list (matches Command.BuildStartInfo).
		if (stage.InheritEnvironmentVariables is not null)
		{
			psi.Environment.Clear();
			foreach (var name in stage.InheritEnvironmentVariables)
			{
				var value = Environment.GetEnvironmentVariable(name);
				if (value is not null)
					psi.Environment[name] = value;
			}
		}

		// Per-stage Environment dict overrides (per-key null removes).
		if (stage.Environment is { } envOverrides)
		{
			foreach (var (key, value) in envOverrides)
			{
				if (value is null)
					psi.Environment.Remove(key);
				else
					psi.Environment[key] = value;
			}
		}

		// Encoding overrides: use the Command-supplied encoding when set, otherwise default to
		// UTF-8. PipePumpHelpers.PrepareStartInfo applies an additional layer of PSI-level
		// fallback that's irrelevant here because the pipeline builds PSI directly from Command,
		// which has no PSI to fall back to.
		psi.StandardOutputEncoding = stage.StdOutEncoding ?? System.Text.Encoding.UTF8;
		psi.StandardErrorEncoding = stage.StdErrEncoding ?? System.Text.Encoding.UTF8;

		return psi;
	}

	static async Task RelayAsync(Stream src, Stream dst, StreamWriter dstWriter, CancellationToken ct)
	{
		try
		{
			await src.CopyToAsync(dst, ct).ConfigureAwait(false);
		}
		catch (IOException)
		{
			// Pipe closed by the OS (downstream stage exited early, or kill mid-relay). Treat as
			// natural EOF — the next stage's stdin is closed in finally below and downstream
			// observes EOF on its read.
		}
		catch (OperationCanceledException)
		{
			// Pipeline teardown — propagate so the outer try/catch flips activity status.
			throw;
		}
		finally
		{
			// Close BOTH ends. Closing the destination signals EOF to the next stage's stdin so it
			// can finish cleanly. Closing the source (our read end of the upstream's stdout pipe)
			// ensures the upstream gets EPIPE on its next write — otherwise it would block
			// indefinitely on a full pipe buffer if the downstream stopped consuming.
			try { dstWriter.Close(); }
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				// StreamWriter.Close → underlying pipe close. Expected exceptions: IOException if
				// the next stage tore the pipe down between our write and close (racing kill);
				// ObjectDisposedException if Process.Dispose already finalised the writer. Both
				// mean the close already happened or never can — safe to drop.
			}
			try { src.Close(); }
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				// Same exception set as dstWriter.Close — closing our read end of the upstream's
				// pipe twice (we close here, Process.Dispose closes again on outer teardown) is a
				// semantic no-op.
			}
		}
	}

	static async Task FeedFirstStdinAsync(StandardInput? input, StreamWriter firstStdin, CancellationToken ct)
	{
		try
		{
			if (input is not null)
				await input.WriteToAsync(firstStdin.BaseStream, ct).ConfigureAwait(false);
		}
		catch (IOException)
		{
			// First stage closed stdin before we finished — common for `head`/`grep -q` style
			// processes that exit early. Treat as a graceful upstream EOF.
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		finally
		{
			try
			{
				firstStdin.Close();
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				// First stage exited before we finished writing → IOException on close, or
				// Process.Dispose already closed the writer → ObjectDisposedException. Both are
				// benign — the child observed our last write or got EOF when we stopped.
			}
		}
	}

	static async Task ObserveTeardownAsync(
		ProcessGroup group,
		Task<string>[] stderrTasks,
		Task<string>? lastStdoutTask,
		Task[] relayTasks,
		Task? firstStdinTask)
	{
		// Kill survivors and let in-flight reads/copies see EOF or OperationCanceledException.
		await group.DisposeAsync().ConfigureAwait(false);
		// Observe outstanding tasks to absorb exceptions silently — caller has already decided to
		// surface the teardown via WasTimedOut / ProcessCancelledException; suppressing inner
		// task exceptions here prevents UnobservedTaskException finalisation.
		foreach (var task in stderrTasks)
			await Observe(task).ConfigureAwait(false);
		foreach (var task in relayTasks)
			await Observe(task).ConfigureAwait(false);
		if (lastStdoutTask is not null)
			await Observe(lastStdoutTask).ConfigureAwait(false);
		if (firstStdinTask is not null)
			await Observe(firstStdinTask).ConfigureAwait(false);

		static async Task Observe(Task t)
		{
			try
			{
				await t.ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
			{
				// Expected teardown faults: OperationCanceledException from the linked-CTS firing,
				// IOException when the OS tears down a pipe we were reading, and
				// ObjectDisposedException if Process.Dispose closed a stream mid-read. The outer
				// caller has already committed to a synthetic result; we only need to observe the
				// task so the finaliser doesn't escalate to UnobservedTaskException.
			}
		}
	}

	string PipelineName() => string.Join(" | ", Stages.Select(s => Path.GetFileName(s.Program)));
}
