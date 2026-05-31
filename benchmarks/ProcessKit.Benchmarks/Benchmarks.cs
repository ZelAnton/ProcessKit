using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace ProcessKit.Benchmarks;

/// <summary>Process start→exit overhead: ProcessKit vs a raw <see cref="Process"/> baseline.</summary>
[MemoryDiagnoser]
public class ProcessStartExitBenchmarks
{
	[Benchmark(Baseline = true)]
	public async Task RawProcess()
	{
		using var process = Process.Start(BenchExe.ExitWith(0))!;
		await process.WaitForExitAsync();
	}

	[Benchmark]
	public async Task<int> ProcessKit_GetExitCode()
		=> await ProcessRunner.Default.GetExitCodeAsync(BenchExe.ExitWith(0));
}

/// <summary>The three bulk capture shapes against the same chatty child.</summary>
[MemoryDiagnoser]
public class OutputCaptureBenchmarks
{
	[Params(100, 1000)]
	public int Lines;

	ProcessStartInfo _psi = null!;

	[GlobalSetup]
	public void Setup() => _psi = BenchExe.ChattyLines(Lines);

	[Benchmark]
	public async Task<int> FullOutput() => (await ProcessRunner.Default.GetFullOutputAsync(_psi)).StdOut.Length;

	[Benchmark]
	public async Task<int> ExitCodeDiscard() => await ProcessRunner.Default.GetExitCodeAsync(_psi);

	[Benchmark]
	public async Task<int> Bytes() => (await ProcessRunner.Default.GetBytesOutputAsync(_psi)).StdOut.Length;
}

/// <summary>Line-streaming throughput: enumerate <see cref="IRunningProcess.StdOut"/>.</summary>
[MemoryDiagnoser]
public class StreamingThroughputBenchmarks
{
	[Params(1000)]
	public int Lines;

	ProcessStartInfo _psi = null!;

	[GlobalSetup]
	public void Setup() => _psi = BenchExe.ChattyLines(Lines);

	[Benchmark]
	public async Task<int> StreamStdOut()
	{
		var count = 0;
		await using var process = ProcessRunner.Default.Start(_psi);
		await foreach (var _ in process.StdOut)
			count++;
		await process.Completion;
		return count;
	}
}

/// <summary>
/// Per-line pump + channel overhead with NO real process (in-memory fake), across buffer policies —
/// isolates the library's hot path from OS fork/exec noise.
/// </summary>
[MemoryDiagnoser]
public class LinePumpBenchmarks
{
	[Params(10_000)]
	public int Lines;

	byte[] _stdout = null!;

	[GlobalSetup]
	public void Setup()
	{
		var sb = new StringBuilder();
		for (var i = 0; i < Lines; i++)
			sb.Append("line").Append(i).Append('\n');
		_stdout = Encoding.UTF8.GetBytes(sb.ToString());
	}

	[Benchmark(Baseline = true)]
	public Task<int> Unbounded() => PumpAsync(null);

	[Benchmark]
	public Task<int> BoundedDropOldest() => PumpAsync(new OutputBufferPolicy { MaxBufferedLines = 1000 });

	[Benchmark]
	public Task<int> Discard() => PumpAsync(new OutputBufferPolicy { MaxBufferedLines = 0 });

	async Task<int> PumpAsync(OutputBufferPolicy? policy)
	{
		var handle = new BenchFakeHandle(_stdout);
		var sink = new LineChannelStdOutSink(policy);
		await using var session = new ProcessSession(
			new ProcessStartInfo("bench"),
			new ProcessRunOptions { OutputBuffer = policy },
			sink,
			new BenchFakeHandleFactory(handle),
			CancellationToken.None);

		await session.StdOutPumpCompletion; // pump read & buffered/dropped every line
		handle.RaiseExited();
		await session.Completion;
		return sink.LineCount;
	}
}
