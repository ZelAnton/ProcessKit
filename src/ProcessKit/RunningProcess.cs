using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProcessKit.Diagnostics;

namespace ProcessKit;

/// <summary>
/// Line-oriented <see cref="IRunningProcess"/> handle. A thin adapter over <see cref="ProcessSession"/>
/// (which owns the whole lifecycle) plus a <see cref="LineChannelStdOutSink"/> for stdout.
/// </summary>
sealed class RunningProcess : IRunningProcess
{
	readonly ProcessSession _session;
	readonly LineChannelStdOutSink _stdOutSink;

	internal RunningProcess(
		ProcessStartInfo startInfo,
		ProcessRunOptions? options,
		CancellationToken cancellationToken)
	{
		_stdOutSink = new LineChannelStdOutSink(options?.OutputBuffer);
		_session = new ProcessSession(startInfo, options, _stdOutSink, RealProcessHandleFactory.Instance, cancellationToken);
	}

	public IAsyncEnumerable<string> StdOut => _stdOutSink.ReadAllAsync();
	public IAsyncEnumerable<string> StdErr => _session.StdErr;
	public IProcessStandardInput? StandardInput => _session.InteractiveInput;
	public int StdOutLineCount => _stdOutSink.LineCount;
	public int StdErrLineCount => _session.StdErrLineCount;
	public int Pid => _session.Pid;
	public DateTime StartTime => _session.StartTime;
	public TimeSpan? Duration => _session.Duration;
	public TimeSpan? CpuTime => _session.CpuTime;
	public long? PeakMemoryBytes => _session.PeakMemoryBytes;
	public bool WasTimedOut => _session.WasTimedOut;
	public CancellationToken Exited => _session.Exited;
	public Task<int> Completion => _session.Completion;

	public async Task<string> WaitForLineAsync(Predicate<string> match, TimeSpan within, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(match);

		using var activity = ProcessKitActivitySource.Source.StartActivity(
			"processkit.probe.line",
			ActivityKind.Internal);
		activity?.SetTag("program", _session.Program);
		activity?.SetTag("within_ms", (long)within.TotalMilliseconds);

		var matchTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var subscription = _session.SubscribeStdOutLine(line =>
		{
			if (match(line))
				matchTcs.TrySetResult(line);
		});

		try
		{
			// Combine deadline + caller cancellation into a single linked CTS so a single Task.Delay
			// covers both signals — avoids leaking a separate `Task.Delay(Infinite, cancellationToken)`
			// (whose CT registration would live for the caller's CT lifetime, indefinitely when the
			// caller passed CancellationToken.None).
			using var deadlineCts = new CancellationTokenSource(within);
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(deadlineCts.Token, cancellationToken);
			var deadlineOrCancelTask = Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);

			await Task.WhenAny(matchTcs.Task, deadlineOrCancelTask, _session.Completion).ConfigureAwait(false);

			// Re-check matchTcs BEFORE deciding to fail: the pump may have dispatched the matching
			// line while Process.Exited fired concurrently, completing both matchTcs and
			// _session.Completion in the same scheduling window. WhenAny only reports ONE winner,
			// so picking _session.Completion would otherwise mask a valid match.
			if (matchTcs.Task.IsCompletedSuccessfully)
				return matchTcs.Task.Result;

			cancellationToken.ThrowIfCancellationRequested();
			// Either deadline elapsed or child exited before a match — both surface as NotReady.
			activity?.SetStatus(ActivityStatusCode.Error, "not ready");
			throw new ProcessNotReadyException(_session.Program, within);
		}
		catch (OperationCanceledException)
		{
			activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
			throw;
		}
	}

	public async Task WaitForAsync(Func<CancellationToken, Task<bool>> check, TimeSpan within, TimeSpan poll = default, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(check);
		if (poll <= TimeSpan.Zero)
			poll = TimeSpan.FromMilliseconds(50);

		using var activity = ProcessKitActivitySource.Source.StartActivity(
			"processkit.probe.custom",
			ActivityKind.Internal);
		activity?.SetTag("program", _session.Program);
		activity?.SetTag("within_ms", (long)within.TotalMilliseconds);
		activity?.SetTag("poll_ms", (long)poll.TotalMilliseconds);

		var deadline = Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * Stopwatch.Frequency);

		try
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();

				bool ready;
				try
				{
					ready = await check(cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
					// Treat a check exception like a "not ready yet" — caller may have a transient
					// failure (e.g. TcpClient throws on refused). Re-evaluate next poll tick.
					ready = false;
				}
				if (ready)
					return;

				if (_session.Completion.IsCompleted)
				{
					activity?.SetStatus(ActivityStatusCode.Error, "child exited");
					throw new ProcessNotReadyException(_session.Program, within);
				}

				var remainingTicks = deadline - Stopwatch.GetTimestamp();
				if (remainingTicks <= 0)
				{
					activity?.SetStatus(ActivityStatusCode.Error, "not ready");
					throw new ProcessNotReadyException(_session.Program, within);
				}

				var remaining = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
				var sleep = remaining < poll ? remaining : poll;
				await Task.Delay(sleep, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
			throw;
		}
	}

	public async Task WaitForPortAsync(IPEndPoint endpoint, TimeSpan within, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(endpoint);

		using var activity = ProcessKitActivitySource.Source.StartActivity(
			"processkit.probe.port",
			ActivityKind.Internal);
		activity?.SetTag("program", _session.Program);
		activity?.SetTag("within_ms", (long)within.TotalMilliseconds);
		activity?.SetTag("endpoint", endpoint.ToString());

		var attemptCap = TimeSpan.FromSeconds(1);
		var deadline = Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * Stopwatch.Frequency);

		// Reuse the generic WaitForAsync loop semantics here by inlining the check — keeps the
		// activity span specifically tagged for the port case.
		try
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var remainingTicks = deadline - Stopwatch.GetTimestamp();
				if (remainingTicks <= 0)
				{
					activity?.SetStatus(ActivityStatusCode.Error, "not ready");
					throw new ProcessNotReadyException(_session.Program, within);
				}
				var remaining = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);

				var cap = remaining < attemptCap ? remaining : attemptCap;
				if (cap < TimeSpan.FromMilliseconds(1))
					cap = TimeSpan.FromMilliseconds(1);

				bool connected;
				using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
				{
					attemptCts.CancelAfter(cap);
					try
					{
						using var client = new TcpClient();
						await client.ConnectAsync(endpoint.Address, endpoint.Port, attemptCts.Token).ConfigureAwait(false);
						connected = true;
					}
					catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
					{
						// Per-attempt cap fired — endpoint stalled or refused. Retry next tick.
						connected = false;
					}
					catch (SocketException)
					{
						// Refused / unreachable — endpoint not ready yet. Retry next tick.
						connected = false;
					}
				}

				if (connected)
					return;

				if (_session.Completion.IsCompleted)
				{
					activity?.SetStatus(ActivityStatusCode.Error, "child exited");
					throw new ProcessNotReadyException(_session.Program, within);
				}

				// Small pause between attempts; matches Rust READINESS_POLL = 50ms.
				var pollRemainingTicks = deadline - Stopwatch.GetTimestamp();
				if (pollRemainingTicks <= 0)
				{
					activity?.SetStatus(ActivityStatusCode.Error, "not ready");
					throw new ProcessNotReadyException(_session.Program, within);
				}
				var pollRemaining = TimeSpan.FromSeconds((double)pollRemainingTicks / Stopwatch.Frequency);
				var poll = TimeSpan.FromMilliseconds(50);
				var sleep = pollRemaining < poll ? pollRemaining : poll;
				await Task.Delay(sleep, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
			throw;
		}
	}

	public ValueTask DisposeAsync() => _session.DisposeAsync();
}
