using System.Diagnostics;
using System.Text;

namespace ProcessKit.Benchmarks;

/// <summary>
/// Minimal in-memory <see cref="IProcessHandle"/> for the line-pump microbenchmark — drives
/// <see cref="ProcessSession"/> with a pre-filled stdout stream and no OS process, isolating the
/// library's per-line overhead from fork/exec cost.
/// </summary>
sealed class BenchFakeHandle : IProcessHandle
{
	int _hasExited;

	internal BenchFakeHandle(byte[] stdout)
	{
		StandardOutput = new StreamReader(new MemoryStream(stdout), Encoding.UTF8);
		StandardError = new StreamReader(new MemoryStream([]), Encoding.UTF8);
		StandardInput = new StreamWriter(new MemoryStream(), Encoding.UTF8);
	}

	public StreamReader StandardOutput { get; }
	public StreamReader StandardError { get; }
	public StreamWriter StandardInput { get; }
	public event EventHandler? Exited;
	public bool EnableRaisingEvents { get; set; }
	public bool HasExited => Volatile.Read(ref _hasExited) != 0;
	public int ExitCode => 0;
	public int Id => 1;
	public DateTime StartTime => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
	public TimeSpan TotalProcessorTime => TimeSpan.Zero;
	public long PeakWorkingSet64 => 0;
	public void Refresh() { }
	public void Kill(bool entireProcessTree) => RaiseExited();
	public Task WaitForExitAsync(CancellationToken cancellationToken) { RaiseExited(); return Task.CompletedTask; }

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

sealed class BenchFakeHandleFactory(BenchFakeHandle handle) : IProcessHandleFactory
{
	public IProcessHandle Start(ProcessGroup group, ProcessStartInfo startInfo, CancellationToken killToken) => handle;
}
