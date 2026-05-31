using System.Diagnostics;

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

	public ValueTask DisposeAsync() => _session.DisposeAsync();
}
