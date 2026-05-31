using System.Text;

namespace ProcessKit;

/// <summary>
/// Default <see cref="IProcessStandardInput"/>: writes to the child's stdin stream behind a
/// <see cref="SemaphoreSlim"/> so concurrent writes are serialized and line integrity is preserved.
/// Writes go to the underlying <see cref="Stream"/> directly (not the buffered <see cref="StreamWriter"/>
/// text API) and are flushed each time so the child receives input promptly.
/// </summary>
sealed class ProcessStandardInputWriter : IProcessStandardInput, IDisposable
{
	readonly StreamWriter _standardInput;
	readonly Stream _stream;
	readonly SemaphoreSlim _gate = new(1, 1);
	readonly byte[] _newline;
	bool _completed;

	public Encoding Encoding { get; }

	internal ProcessStandardInputWriter(StreamWriter standardInput, Encoding encoding)
	{
		_standardInput = standardInput;
		_stream = standardInput.BaseStream;
		Encoding = encoding;
		_newline = encoding.GetBytes(Environment.NewLine);
	}

	public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfCompleted();
			await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
			await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask WriteLineAsync(string text, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfCompleted();
			await _stream.WriteAsync(Encoding.GetBytes(text), cancellationToken).ConfigureAwait(false);
			await _stream.WriteAsync(_newline, cancellationToken).ConfigureAwait(false);
			await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfCompleted();
			await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask CompleteAsync()
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_completed)
				return;
			_completed = true;
			try
			{
				_standardInput.Close();
			}
			catch (Exception e) when (e is IOException or ObjectDisposedException)
			{
				// Child already exited and the OS reclaimed the pipe — there is no reader left to
				// signal EOF to; closing is moot.
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	void ThrowIfCompleted()
	{
		if (_completed)
			throw new InvalidOperationException("Standard input has already been completed.");
	}

	public void Dispose() => _gate.Dispose();
}
