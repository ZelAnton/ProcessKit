using System.Diagnostics;
using System.Text;

namespace ProcessKit;

/// <summary>
/// The single owner of a started process's lifecycle: group ownership, kill/timeout cancellation,
/// exit handling, diagnostics caching, and pump orchestration. Each stream's transport is delegated
/// to an injected <see cref="IStdOutSink"/> (line channel / raw bytes / faithful text). stdout always
/// has a sink; stderr defaults to a line buffer (exposed via <see cref="StdErr"/>) unless a stderr
/// sink is supplied. Both <see cref="RunningProcess"/> (line handle) and the bulk capture paths build
/// on this, so the lifecycle exists in exactly one place.
/// </summary>
sealed class ProcessSession : IAsyncDisposable
{
	readonly IProcessHandle _handle;
	readonly ProcessGroup _group;
	readonly bool _ownsGroup;
	readonly IStdOutSink _stdOutSink;
	readonly IStdOutSink? _stdErrSink;
	readonly ILineBuffer? _stderrBuffer;
	readonly Task _stdinTask;
	readonly ProcessStandardInputWriter? _interactiveInput;
	readonly TaskCompletionSource<int> _completionTcs;
	readonly CancellationTokenSource _exitedCts;
	readonly CancellationTokenSource _killCts;
	readonly CancellationTokenSource? _timeoutCts;
	readonly Stopwatch _stopwatch;
	readonly TimeSpan _pumpTeardownTimeout;
	int _stderrLineCount;
	int _disposed;
	int _exitedHandled;
	// Backing storage for Duration:
	//   * `long` to give atomic 64-bit reads/writes on every platform via Interlocked.
	//   * `-1` sentinel for "still running" — TimeSpan.Ticks is never negative.
	// Without this, `Nullable<TimeSpan>` (bool + long) tears across the bool/Ticks pair.
	long _durationTicks = -1L;
	// Cached on exit; sentinel `-1` = not cached, sample live.
	long _finalCpuTimeTicks = -1L;
	long _finalPeakMemory = -1L;

	// Valid only in line mode (no stderr sink supplied) — i.e. for the streaming RunningProcess handle.
	public IAsyncEnumerable<string> StdErr => _stderrBuffer!.ReadAllAsync();

	public int StdErrLineCount => Volatile.Read(ref _stderrLineCount);

	// Captured in the constructor so it remains observable after _handle.Dispose() runs in
	// DisposeAsync — symmetric with StartTime. Reading the id post-dispose throws
	// InvalidOperationException, which would be a surprising failure mode for callers logging
	// the PID after handle teardown.
	public int Pid { get; }

	public DateTime StartTime { get; }

	public TimeSpan? Duration
	{
		get
		{
			var t = Interlocked.Read(ref _durationTicks);
			return t < 0 ? null : TimeSpan.FromTicks(t);
		}
	}

	public TimeSpan? CpuTime
	{
		get
		{
			var cached = Interlocked.Read(ref _finalCpuTimeTicks);
			if (cached >= 0)
				return TimeSpan.FromTicks(cached);
			try
			{
				// Refresh first — Process caches its counters, so without this repeated reads return
				// the same stale snapshot rather than a live sample.
				_handle.Refresh();
				return _handle.TotalProcessorTime;
			}
			catch
			{
				// Counter unavailable (e.g. platform doesn't support it, or the process is gone);
				// the best effort — return null rather than throwing from a diagnostics getter.
				return null;
			}
		}
	}

	public long? PeakMemoryBytes
	{
		get
		{
			var cached = Interlocked.Read(ref _finalPeakMemory);
			if (cached >= 0)
				return cached;
			try
			{
				// Refresh first — see CpuTime; Process caches PeakWorkingSet64 between reads.
				_handle.Refresh();
				return _handle.PeakWorkingSet64;
			}
			catch
			{
				// Counter unavailable (e.g. platform doesn't support it, or the process is gone);
				// the best effort — return null rather than throwing from a diagnostics getter.
				return null;
			}
		}
	}

	public bool WasTimedOut => _timeoutCts?.IsCancellationRequested ?? false;

	public CancellationToken Exited => _exitedCts.Token;

	public Task<int> Completion => _completionTcs.Task;

	// Awaitable completion of the stdout pump. The byte-capture path awaits this before reading the
	// captured bytes: Completion can resolve (on Process.Exited) before the OS pipe is fully drained,
	// so the raw copy may still be in flight. The line path doesn't need it — draining the line
	// channel already waits for the pump to complete the buffer.
	public Task StdOutPumpCompletion { get; }

	// Awaitable completion of the stderr pump — the bulk text/byte paths await this before reading a
	// stderr sink's captured content (Completion can resolve before the pipe is fully drained).
	public Task StdErrPumpCompletion { get; }

	// Writer for interactive stdin; non-null only when KeepStandardInputOpen was set. Surfaced via
	// IRunningProcess.StandardInput.
	public IProcessStandardInput? InteractiveInput => _interactiveInput;

	internal ProcessSession(
		ProcessStartInfo startInfo,
		ProcessRunOptions? options,
		IStdOutSink stdOutSink,
		IProcessHandleFactory handleFactory,
		CancellationToken cancellationToken,
		IStdOutSink? stdErrSink = null)
	{
		if (options is { KeepStandardInputOpen: true, StandardInput: not null and not StandardInput.EmptyInput })
			throw new ArgumentException(
				"Set either StandardInput or KeepStandardInputOpen, not both. For interactive stdin, write all input (including any initial data) via IRunningProcess.StandardInput.",
				nameof(options));

		var psi = PipePumpHelpers.PrepareStartInfo(startInfo, options);

		_stdOutSink = stdOutSink;
		_stdErrSink = stdErrSink;
		_ownsGroup = options?.ProcessGroup is null;
		_group = options?.ProcessGroup ?? new ProcessGroup(options?.ProcessGroupOptions ?? ProcessGroupOptions.Default);
		// Line mode (no stderr sink): own a line buffer exposed via StdErr. Sink mode: the sink owns capture.
		_stderrBuffer = stdErrSink is null ? ILineBuffer.Create(options?.OutputBuffer) : null;
		_pumpTeardownTimeout = options?.PumpTeardownTimeout ?? TimeSpan.FromSeconds(5);

		_killCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		if (options?.Timeout is { } timeout)
		{
			// Separate CTS for the timeout — gives WasTimedOut a precise signal distinct from
			// the caller's cancellationToken. A registered callback propagates the cancellation
			// into _killCts so the unified "kill the process" path remains the same.
			_timeoutCts = new CancellationTokenSource(timeout);
			_timeoutCts.Token.UnsafeRegister(static state =>
			{
				try
				{
					((CancellationTokenSource)state!).Cancel();
				}
				catch (ObjectDisposedException)
				{
					// ignored - runner already disposed; the kill path is moot.
				}
			}, _killCts);
		}

		_completionTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		_exitedCts = new CancellationTokenSource();
		_stopwatch = new Stopwatch();

		try
		{
			_stopwatch.Start();
			_handle = handleFactory.Start(_group, psi, _killCts.Token);
			Pid = _handle.Id;
			StartTime = SafeStartTime(_handle);

			_handle.EnableRaisingEvents = true;
			_handle.Exited += OnProcessExited;

			// If the process already exited between Start and EnableRaisingEvents/subscription,
			// fire synchronously so consumers don't hang.
			if (_handle.HasExited)
				OnProcessExited(this, EventArgs.Empty);

			StdOutPumpCompletion = _stdOutSink.PumpAsync(_handle.StandardOutput, options?.StandardOutputHandler, _killCts.Token);

			StdErrPumpCompletion = _stdErrSink is not null
				? _stdErrSink.PumpAsync(_handle.StandardError, options?.StandardErrorHandler, _killCts.Token)
				: PipePumpHelpers.PumpLinesAsync(
					_handle.StandardError,
					_stderrBuffer!,
					options?.StandardErrorHandler,
					() => Interlocked.Increment(ref _stderrLineCount),
					_killCts.Token);

			if (options?.KeepStandardInputOpen == true)
			{
				// Leave stdin open; the caller drives it via IRunningProcess.StandardInput and signals
				// EOF with CompleteAsync. No pump task — there is no fixed source to drain.
				_interactiveInput = new ProcessStandardInputWriter(_handle.StandardInput, Encoding.UTF8);
				_stdinTask = Task.CompletedTask;
			}
			else
			{
				_stdinTask = PipePumpHelpers.WriteStandardInputAsync(_handle, options?.StandardInput, _killCts.Token);
			}
		}
		catch
		{
			if (_handle is not null)
			{
				// Unsubscribe so the Process delegate stops keeping this half-constructed instance alive
				// after we throw. Safe even if the += never ran — Delegate.Remove on a missing handler is a no-op.
				_handle.Exited -= OnProcessExited;

				try
				{
					if (!_handle.HasExited)
						_handle.Kill(entireProcessTree: true);
				}
				catch
				{
					// ignored - best effort to clean up; if it fails we must not throw from a catch and
					// lose the original exception.
				}

				try
				{
					_handle.Dispose();
				}
				catch
				{
					// ignored - best effort.
				}
			}

			// Complete the output buffers so any pump we managed to spin up before the throw observes
			// completion and tears down quickly instead of looping until the OS pipe is gone.
			_stdOutSink.Complete();
			(_stdOutSink as IDisposable)?.Dispose();
			_stdErrSink?.Complete();
			(_stdErrSink as IDisposable)?.Dispose();
			_stderrBuffer?.Complete();

			if (_ownsGroup)
				_group.Dispose();
			// Dispose _timeoutCts first: it owns a callback that cancels _killCts. Stopping the
			// timer before the target CTS goes away avoids the callback racing into a disposed
			// _killCts (the callback catches ODE anyway, but ordering is cleaner).
			_timeoutCts?.Dispose();
			_killCts.Dispose();
			_exitedCts.Dispose();
			throw;
		}
	}

	void OnProcessExited(object? sender, EventArgs e)
	{
		// Handler can be invoked from three places: the Process.Exited event (thread pool), the
		// synchronous HasExited check in the constructor, and the synchronous fallback in
		// DisposeAsync. Two of them can race (event vs. HasExited check during construction), so
		// the body must run exactly once — otherwise concurrent Stopwatch.Stop()/Duration writes
		// tear the observable state.
		if (Interlocked.Exchange(ref _exitedHandled, 1) != 0)
			return;

		_stopwatch.Stop();
		Interlocked.Exchange(ref _durationTicks, _stopwatch.ElapsedTicks);

		// Cache final stats now while the handle is still alive — keeps these observable even after
		// _handle.Dispose() runs in DisposeAsync. Refresh first so the cached values reflect the
		// final state, not a stale snapshot from an earlier live read.
		try
		{
			_handle.Refresh();
		}
		catch
		{
			// counters unavailable — the reads below fall back to their own sentinels
		}
		try
		{
			Interlocked.Exchange(ref _finalCpuTimeTicks, _handle.TotalProcessorTime.Ticks);
		}
		catch
		{
			// counter unavailable — leave sentinel
		}
		try
		{
			Interlocked.Exchange(ref _finalPeakMemory, _handle.PeakWorkingSet64);
		}
		catch
		{
			// counter unavailable — leave sentinel
		}

		int code;
		try
		{
			code = _handle.ExitCode;
		}
		catch (InvalidOperationException)
		{
			// Process exited but ExitCode is inaccessible (e.g. on some platforms). The completion
			// task's purpose is to signal exit; the precise code is best-effort here.
			code = -1;
		}

		_completionTcs.TrySetResult(code);
		try
		{
			_exitedCts.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// ignored - already disposed, so pumps should be winding down or dead.
		}
	}

	static DateTime SafeStartTime(IProcessHandle handle)
	{
		try
		{
			return handle.StartTime;
		}
		catch (SystemException e) when (e
			is InvalidOperationException
			or System.ComponentModel.Win32Exception)
		{
			// best effort fallback if StartTime is inaccessible (e.g. process already exited, or a
			// platform that doesn't support it); not perfect, but better than throwing and losing
			// the rest of the process info.
			return DateTime.Now;
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		// CancelAsync (vs Cancel) offloads registered callbacks (notably ProcessGroup's kill-on-cancel,
		// which calls Process.Kill(entireProcessTree)) to the thread pool instead of running them on
		// the caller's await-resume context.
		try
		{
			await _killCts.CancelAsync().ConfigureAwait(false);
		}
		catch (ObjectDisposedException)
		{
			// ignored - already disposed, so pumps should be winding down or dead.
		}

		// Bounded wait for pumps and stdin to wind down. We use a token-based timeout (vs WaitAsync(TimeSpan))
		// so the timeout surfaces as OperationCanceledException — caught by ObserveAsync along with the
		// other expected teardown exceptions, keeping the semantics uniform.
		using var teardownCts = new CancellationTokenSource(_pumpTeardownTimeout);
		var pumpTask = Task.WhenAll(StdOutPumpCompletion, StdErrPumpCompletion, _stdinTask);
		await PipePumpHelpers.ObserveAsync(pumpTask.WaitAsync(teardownCts.Token)).ConfigureAwait(false);

		_stdOutSink.Complete();
		_stdErrSink?.Complete();
		_stderrBuffer?.Complete();

		try
		{
			await _handle.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
		}
		catch (InvalidOperationException)
		{
			// ignored - never started
		}

		if (!_completionTcs.Task.IsCompleted)
			OnProcessExited(this, EventArgs.Empty);

		if (_ownsGroup)
			await _group.DisposeAsync().ConfigureAwait(false);

		try
		{
			_handle.Dispose();
		}
		catch
		{
			// ignored - best effort to clean up; we must not throw from DisposeAsync.
		}

		(_stdOutSink as IDisposable)?.Dispose();
		(_stdErrSink as IDisposable)?.Dispose();
		_interactiveInput?.Dispose();

		// Same ordering as the constructor catch — see explanation there.
		_timeoutCts?.Dispose();
		_killCts.Dispose();
		_exitedCts.Dispose();
	}
}
