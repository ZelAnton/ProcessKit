namespace ProcessKit;

/// <summary>
/// Stdout transport for a <see cref="ProcessSession"/>. The session owns the lifecycle and starts
/// the sink's pump; the sink decides how to consume stdout — decoded lines vs raw bytes — without
/// the session knowing which. Stderr is always line-oriented and handled by the session directly.
/// </summary>
interface IStdOutSink
{
	/// <summary>Drain the handle's stdout. Started inside the session's construction try-block.</summary>
	Task PumpAsync(IProcessHandle handle, ProcessRunOptions? options, CancellationToken cancellationToken);

	/// <summary>Signal end-of-stream during teardown (idempotent).</summary>
	void Complete();
}

/// <summary>
/// Line-decoding stdout sink: pumps decoded lines into an <see cref="ILineBuffer"/>, tracks a live
/// line count, and tees each line to <see cref="ProcessRunOptions.StandardOutputHandler"/>. Backs
/// <see cref="IRunningProcess.StdOut"/>.
/// </summary>
sealed class LineChannelStdOutSink(OutputBufferPolicy? bufferPolicy) : IStdOutSink
{
	readonly ILineBuffer _buffer = ILineBuffer.Create(bufferPolicy);
	int _lineCount;

	public int LineCount => Volatile.Read(ref _lineCount);

	public IAsyncEnumerable<string> ReadAllAsync() => _buffer.ReadAllAsync();

	public Task PumpAsync(IProcessHandle handle, ProcessRunOptions? options, CancellationToken cancellationToken)
		=> PipePumpHelpers.PumpLinesAsync(
			handle.StandardOutput,
			_buffer,
			options?.StandardOutputHandler,
			() => Interlocked.Increment(ref _lineCount),
			cancellationToken);

	public void Complete() => _buffer.Complete();
}

/// <summary>
/// Raw-bytes stdout sink: copies the undecoded stdout base stream into a <see cref="MemoryStream"/>.
/// Backs <c>GetBytesOutputAsync</c>. Never goes through line decoding, preserving exact bytes.
/// </summary>
sealed class ByteBufferStdOutSink : IStdOutSink, IDisposable
{
	readonly MemoryStream _buffer = new();

	public async Task PumpAsync(IProcessHandle handle, ProcessRunOptions? options, CancellationToken cancellationToken)
	{
		try
		{
			await handle.StandardOutput.BaseStream.CopyToAsync(_buffer, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception e) when (e is OperationCanceledException or IOException)
		{
			// Killed via the kill token (timeout or cancellation) — the pipe closed mid-copy.
			// Whatever bytes were captured before the kill are kept; nothing else to do.
		}
	}

	public byte[] ToArray() => _buffer.ToArray();

	public void Complete()
	{
		// No channel to complete — the byte buffer is read synchronously via ToArray after exit.
	}

	public void Dispose() => _buffer.Dispose();
}
