using System.Text;

namespace ProcessKit;

/// <summary>
/// Output transport for one of a <see cref="ProcessSession"/>'s streams (stdout or stderr). The
/// session owns the lifecycle and starts the sink's pump against a specific <see cref="StreamReader"/>;
/// the sink decides how to consume it — decoded lines, raw bytes, or faithful decoded text.
/// </summary>
interface IStdOutSink
{
	/// <summary>Drain <paramref name="reader"/>, optionally teeing each decoded line to <paramref name="handler"/>.</summary>
	Task PumpAsync(StreamReader reader, Action<string>? handler, CancellationToken cancellationToken);

	/// <summary>Signal end-of-stream during teardown (idempotent).</summary>
	void Complete();
}

/// <summary>
/// Line-decoding sink: pumps decoded lines into an <see cref="ILineBuffer"/>, tracks a live line
/// count, and tees each line to the supplied handler. Backs the streaming <see cref="IRunningProcess"/>
/// stdout/stderr. Lines are terminator-free (the line terminators are not preserved).
/// </summary>
sealed class LineChannelStdOutSink(OutputBufferPolicy? bufferPolicy) : IStdOutSink
{
	readonly ILineBuffer _buffer = ILineBuffer.Create(bufferPolicy);
	int _lineCount;

	public int LineCount => Volatile.Read(ref _lineCount);

	public IAsyncEnumerable<string> ReadAllAsync() => _buffer.ReadAllAsync();

	public Task PumpAsync(StreamReader reader, Action<string>? handler, CancellationToken cancellationToken)
		=> PipePumpHelpers.PumpLinesAsync(
			reader: reader,
			buffer: _buffer,
			handler: handler,
			incrementCounter: () => Interlocked.Increment(ref _lineCount),
			cancellationToken);

	public void Complete() => _buffer.Complete();
}

/// <summary>
/// Raw-bytes sink: copies the undecoded base stream into a <see cref="MemoryStream"/>. Backs
/// per-line handler does not apply to raw bytes and is ignored.
/// </summary>
sealed class ByteBufferStdOutSink : IStdOutSink, IDisposable
{
	readonly MemoryStream _buffer = new();

	public async Task PumpAsync(StreamReader reader, Action<string>? handler, CancellationToken cancellationToken)
	{
		try
		{
			await reader.BaseStream.CopyToAsync(_buffer, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception e) when (e
			is OperationCanceledException
			or IOException
			or ObjectDisposedException)
		{
			// Killed via the kill token (timeout or cancellation) — the pipe closed mid-copy, or the
			// CTS was disposed because dispose raced ahead past the teardown timeout. Whatever bytes
			// were captured before the kill are kept; nothing else to do.
		}
	}

	public byte[] ToArray() => _buffer.ToArray();

	public void Complete()
	{
		// No channel to complete — the byte buffer is read synchronously via ToArray after exit.
	}

	public void Dispose() => _buffer.Dispose();
}

/// <summary>
/// Faithful-text sink: accumulates the exact decoded text of the stream (all characters, including
/// the original line endings and any trailing newline — nothing is normalized or dropped). Backs the
/// bulk text helpers (<c>GetFullOutputAsync</c>, and <c>GetBytesOutputAsync</c>'s stderr). When a
/// per-line handler is supplied, each decoded line is teed to it as it arrives (line terminators
/// stripped, matching the streaming line semantics) while the faithful text is captured in full.
/// </summary>
sealed class TextBufferSink : IStdOutSink
{
	public string Text { get; private set; } = string.Empty;

	public async Task PumpAsync(StreamReader reader, Action<string>? handler, CancellationToken cancellationToken)
	{
		var all = new StringBuilder();
		var line = handler is null ? null : new StringBuilder();
		var pendingCr = false;
		var buffer = new char[4096];

		try
		{
			int read;
			while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
			{
				all.Append(buffer, 0, read);

				if (handler is null)
					continue;

				// Split into lines matching StreamReader.ReadLine semantics (\n, \r, and \r\n are each
				// a single terminator), carrying a pending '\r' across read boundaries.
				for (var i = 0; i < read; i++)
				{
					var c = buffer[i];
					if (pendingCr)
					{
						pendingCr = false;
						if (c == '\n')
							continue; // second half of a \r\n terminator; the line was emitted on the \r
					}

					switch (c)
					{
						case '\r':
							Emit();
							pendingCr = true;
							break;
						case '\n':
							Emit();
							break;
						default:
							line!.Append(c);
							break;
					}
				}
			}

			// Final unterminated line (only on clean EOF — on cancellation the trailing data is partial).
			if (line is { Length: > 0 })
				Emit();
		}
		catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
		{
			// Killed via the kill token — the pipe closed, or the CTS was disposed because dispose
			// raced past the teardown timeout. Keep whatever text was captured; do not emit the
			// partial trailing line as if it were complete.
		}

		Text = all.ToString();
		return;

		void Emit()
		{
			var text = line!.ToString();
			line.Clear();
			try
			{
				handler!(text);
			}
			catch
			{
				// ignored - a user handler must not break the pump.
			}
		}
	}

	public void Complete()
	{
		// Nothing to complete — Text is read synchronously after the pump finishes.
	}
}
