using System.Diagnostics;
using System.Threading.Channels;

namespace ProcessKit;

sealed class RunningProcess : IRunningProcess
{
	readonly Process _process;
	readonly ProcessGroup _group;
	readonly bool _ownsGroup;
	readonly Channel<string> _stdoutChannel;
	readonly Channel<string> _stderrChannel;
	readonly Task _stdoutPump;
	readonly Task _stderrPump;
	readonly Task _stdinTask;
	readonly TaskCompletionSource<int> _completionTcs;
	readonly CancellationTokenSource _exitedCts;
	readonly CancellationTokenSource _killCts;
	readonly CancellationTokenSource? _timeoutCts;
	readonly Stopwatch _stopwatch;
	int _stdoutLineCount;
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

	public IAsyncEnumerable<string> StdOut
		=> _stdoutChannel.Reader.ReadAllAsync();

	public IAsyncEnumerable<string> StdErr
		=> _stderrChannel.Reader.ReadAllAsync();

	public int StdOutLineCount
		=> Volatile.Read(ref _stdoutLineCount);

	public int StdErrLineCount
		=> Volatile.Read(ref _stderrLineCount);

	// Captured in the constructor so it remains observable after _process.Dispose() runs in
	// DisposeAsync — symmetric with StartTime. Reading _process.Id post-dispose throws
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
				_process.Refresh();
				return _process.TotalProcessorTime;
			}
			catch
			{
				// Counter unavailable (e.g. platform doesn't support it);
				// The best effort to provide some info, but not critical, so just return null.
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
				_process.Refresh();
				return _process.PeakWorkingSet64;
			}
			catch
			{
				// Counter unavailable (e.g. platform doesn't support it);
				// The best effort to provide some info, but not critical, so just return null.
				return null;
			}
		}
	}

	public bool WasTimedOut => _timeoutCts?.IsCancellationRequested ?? false;

	public CancellationToken Exited => _exitedCts.Token;

	public Task<int> Completion => _completionTcs.Task;

	internal RunningProcess(
		ProcessStartInfo startInfo,
		ProcessRunOptions? options,
		CancellationToken cancellationToken)
	{
		var psi = PipePumpHelpers.PrepareStartInfo(startInfo, options);

		_ownsGroup = options?.ProcessGroup is null;
		_group = options?.ProcessGroup ?? new ProcessGroup();

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
					// ignored - runner already disposed
				}
			}, _killCts);
		}

		_completionTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		_exitedCts = new CancellationTokenSource();
		_stopwatch = new Stopwatch();

		_stdoutChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
		{
			SingleWriter = true,
			SingleReader = true,
			AllowSynchronousContinuations = false,
		});
		_stderrChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
		{
			SingleWriter = true,
			SingleReader = true,
			AllowSynchronousContinuations = false,
		});

		try
		{
			_stopwatch.Start();
			_process = _group.Start(psi, _killCts.Token);
			Pid = _process.Id;
			StartTime = SafeStartTime(_process);

			_process.EnableRaisingEvents = true;
			_process.Exited += OnProcessExited;

			// If the process already exited between Start and EnableRaisingEvents/subscription,
			// fire synchronously so consumers don't hang.
			if (_process.HasExited)
				OnProcessExited(this, EventArgs.Empty);

			_stdoutPump = PipePumpHelpers.PumpLinesAsync(
				_process.StandardOutput,
				_stdoutChannel.Writer,
				options?.StandardOutputHandler,
				() => Interlocked.Increment(ref _stdoutLineCount),
				_killCts.Token);

			_stderrPump = PipePumpHelpers.PumpLinesAsync(
				_process.StandardError,
				_stderrChannel.Writer,
				options?.StandardErrorHandler,
				() => Interlocked.Increment(ref _stderrLineCount),
				_killCts.Token);

			_stdinTask = PipePumpHelpers.WriteStandardInputAsync(_process, options?.StandardInput, _killCts.Token);
		}
		catch
		{
			if (_process is not null)
			{
				// Unsubscribe so the Process delegate stops keeping this half-constructed instance alive
				// after we throw. Safe even if the +=  never ran — Delegate.Remove on a missing handler is a no-op.
				_process.Exited -= OnProcessExited;

				try
				{
					if (!_process.HasExited)
						_process.Kill(entireProcessTree: true);
				}
				catch
				{
					// ignored - best effort to clean up, but if it fails, there's not much we can do in a catch block, and we don't want to throw from it and lose the original exception.
				}

				try
				{
					_process.Dispose();
				}
				catch
				{
					// ignored - best effort.
				}
			}

			// Close channels so any pump task we managed to spin up before the throw observes the
			// completion and tears down quickly instead of looping until the OS pipe is gone.
			_stdoutChannel.Writer.TryComplete();
			_stderrChannel.Writer.TryComplete();

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

		// Cache final stats now while the Process wrapper is still alive — keeps these
		// observable even after _process.Dispose() runs in DisposeAsync. Refresh first so the
		// cached values reflect the final state, not a stale snapshot from an earlier live read.
		try
		{
			_process.Refresh();
		}
		catch
		{
			// counters unavailable — the reads below fall back to their own sentinels
		}
		try
		{
			Interlocked.Exchange(ref _finalCpuTimeTicks, _process.TotalProcessorTime.Ticks);
		}
		catch
		{
			// counter unavailable — leave sentinel
		}
		try
		{
			Interlocked.Exchange(ref _finalPeakMemory, _process.PeakWorkingSet64);
		}
		catch
		{
			// counter unavailable — leave sentinel
		}

		int code;
		try
		{
			code = _process.ExitCode;
		}
		catch (InvalidOperationException)
		{
			// process exited but ExitCode is inaccessible (e.g. on some platforms or if the process was never started);
			// The best effort to provide some exit code info, but not critical, since the main purpose of the completion task
			// is just to signal that the process has exited, and the actual code is often less important than the fact of exit itself.
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

	static DateTime SafeStartTime(Process process)
	{
		try
		{
			return process.StartTime;
		}
		catch (SystemException e) when (e
			is InvalidOperationException
			or System.ComponentModel.Win32Exception)
		{
			// best effort fallback if StartTime is inaccessible (e.g. process already exited by the time we get here, or we're on a platform that doesn't support it); not perfect,
			// but better than throwing and losing the rest of the process info
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
		using var teardownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var pumpTask = Task.WhenAll(_stdoutPump, _stderrPump, _stdinTask);
		await PipePumpHelpers.ObserveAsync(pumpTask.WaitAsync(teardownCts.Token)).ConfigureAwait(false);

		_stdoutChannel.Writer.TryComplete();
		_stderrChannel.Writer.TryComplete();

		try
		{
			await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
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
			_process.Dispose();
		}
		catch
		{
			// ignored - best effort to clean up, but if it fails, there's not much we can do, and we don't want to throw from DisposeAsync.
		}

		// Same ordering as the constructor catch — see explanation there.
		_timeoutCts?.Dispose();
		_killCts.Dispose();
		_exitedCts.Dispose();
	}
}
