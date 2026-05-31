using System.Text;

namespace ProcessKit.Tests;

/// <summary>
/// In-memory <see cref="IProcessHandle"/> for unit-testing <see cref="ProcessSession"/> without
/// spawning a real OS process. stdout/stderr are pre-filled MemoryStreams; exit is driven manually
/// via <see cref="RaiseExited"/> (or the kill path). Counters are settable and can be made to throw.
/// </summary>
sealed class FakeProcessHandle : IProcessHandle
{
	readonly MemoryStream _stdoutStream;
	readonly MemoryStream _stderrStream;
	readonly MemoryStream _stdinStream = new();
	int _hasExited;

	internal FakeProcessHandle(byte[]? stdout = null, string stderr = "", int exitCode = 0)
	{
		_stdoutStream = new MemoryStream(stdout ?? []);
		_stderrStream = new MemoryStream(Encoding.UTF8.GetBytes(stderr));
		StandardOutput = new StreamReader(_stdoutStream, Encoding.UTF8);
		StandardError = new StreamReader(_stderrStream, Encoding.UTF8);
		StandardInput = new StreamWriter(_stdinStream, Encoding.UTF8);
		ExitCode = exitCode;
	}

	public StreamReader StandardOutput { get; }
	public StreamReader StandardError { get; }
	public StreamWriter StandardInput { get; }

	public event EventHandler? Exited;

	public bool EnableRaisingEvents { get; set; }
	public bool HasExited => Volatile.Read(ref _hasExited) != 0;
	public int ExitCode { get; set; }
	public int Id { get; set; } = 4242;
	public DateTime StartTime { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

	public bool ThrowOnCounters { get; set; }
	public TimeSpan TotalProcessorTime => ThrowOnCounters
		? throw new InvalidOperationException("counter unavailable")
		: TimeSpan.FromMilliseconds(5);
	public long PeakWorkingSet64 => ThrowOnCounters
		? throw new InvalidOperationException("counter unavailable")
		: 4096;

	public int KillCount { get; private set; }
	public int RefreshCount { get; private set; }

	public void Refresh() => RefreshCount++;

	public void Kill(bool entireProcessTree)
	{
		KillCount++;
		RaiseExited();
	}

	public Task WaitForExitAsync(CancellationToken cancellationToken)
	{
		RaiseExited();
		return Task.CompletedTask;
	}

	/// <summary>Bytes written to stdin (e.g. by the interactive writer). Readable after the stream is closed.</summary>
	internal byte[] CapturedStdin() => _stdinStream.ToArray();

	/// <summary>Marks the process exited without firing the event — for the "already exited at construction" race.</summary>
	internal void PresetExited() => Volatile.Write(ref _hasExited, 1);

	/// <summary>Fires the <see cref="Exited"/> event (idempotent on HasExited, but the event may fire repeatedly to exercise the session's own guard).</summary>
	internal void RaiseExited()
	{
		Volatile.Write(ref _hasExited, 1);
		Exited?.Invoke(this, EventArgs.Empty);
	}

	public void Dispose()
	{
		StandardOutput.Dispose();
		StandardError.Dispose();
		StandardInput.Dispose();
	}
}

/// <summary>Hands a pre-built <see cref="FakeProcessHandle"/> to the session and replicates the
/// kill-on-cancel wiring that <see cref="ProcessGroup.Start"/> provides for real processes.</summary>
sealed class FakeProcessHandleFactory(FakeProcessHandle handle) : IProcessHandleFactory
{
	public IProcessHandle Start(ProcessGroup group, System.Diagnostics.ProcessStartInfo startInfo, CancellationToken killToken)
	{
		// Mirror ProcessGroup.Start's kill-on-cancel registration so cancellation/timeout terminate the fake.
		killToken.Register(static state => ((FakeProcessHandle)state!).Kill(entireProcessTree: true), handle);
		return handle;
	}
}
