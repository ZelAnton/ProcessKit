using System.Threading.Channels;

namespace ProcessKit;

/// <summary>
/// Backlog buffer for a line-oriented output stream (stdout-in-line-mode and stderr). Writes never
/// block — the pump must keep draining the OS pipe regardless of how fast the consumer reads — so
/// bounded implementations <em>drop</em> rather than throttle. Used by both the line stdout sink
/// and the session's stderr handling.
/// </summary>
interface ILineBuffer
{
	/// <summary>Offer a line. Never blocks; a bounded buffer may drop per its overflow policy.</summary>
	void Write(string line);

	/// <summary>Signal end-of-stream so an in-progress <see cref="ReadAllAsync"/> completes.</summary>
	void Complete();

	/// <summary>Enumerate buffered lines until <see cref="Complete"/> is called. At most one reader.</summary>
	IAsyncEnumerable<string> ReadAllAsync();

	/// <summary>Selects an implementation from the policy: unbounded (null), discard (0), or bounded.</summary>
	static ILineBuffer Create(OutputBufferPolicy? policy)
	{
		if (policy?.MaxBufferedLines is not { } cap)
			return new UnboundedLineBuffer();

		return cap <= 0 ? new DiscardLineBuffer() : new BoundedLineBuffer(cap, policy.Overflow);
	}
}

/// <summary>Unbounded backlog — the historical default. Writes always succeed.</summary>
sealed class UnboundedLineBuffer : ILineBuffer
{
	readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
	{
		SingleWriter = true,
		SingleReader = true,
		AllowSynchronousContinuations = false,
	});

	public void Write(string line) => _channel.Writer.TryWrite(line);
	public void Complete() => _channel.Writer.TryComplete();
	public IAsyncEnumerable<string> ReadAllAsync() => _channel.Reader.ReadAllAsync();
}

/// <summary>
/// Fixed-capacity backlog backed by a bounded channel in a drop mode. <c>TryWrite</c> on a
/// drop-mode bounded channel returns synchronously and silently drops — it never blocks the pump,
/// preserving the OS-pipe-drain guarantee.
/// </summary>
sealed class BoundedLineBuffer : ILineBuffer
{
	readonly Channel<string> _channel;

	public BoundedLineBuffer(int capacity, OutputOverflowMode overflow)
		=> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
		{
			SingleWriter = true,
			SingleReader = true,
			AllowSynchronousContinuations = false,
			FullMode = overflow == OutputOverflowMode.DropNewest
				? BoundedChannelFullMode.DropWrite
				: BoundedChannelFullMode.DropOldest,
		});

	public void Write(string line) => _channel.Writer.TryWrite(line);
	public void Complete() => _channel.Writer.TryComplete();
	public IAsyncEnumerable<string> ReadAllAsync() => _channel.Reader.ReadAllAsync();
}

/// <summary>Retains nothing — every line is discarded. Used for <c>MaxBufferedLines == 0</c>.</summary>
sealed class DiscardLineBuffer : ILineBuffer
{
	readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public void Write(string line)
	{
		// Intentionally discarded — the caller opted out of buffering (e.g. exit-code-only path).
	}

	public void Complete() => _completed.TrySetResult();

	public async IAsyncEnumerable<string> ReadAllAsync()
	{
		// Yields nothing, but does NOT complete until the pump signals Complete() (in its finally,
		// after reading EOF and firing every per-line handler). A consumer draining this buffer must
		// still wait for the pump — returning an already-completed empty sequence would let dispose
		// cancel the pump mid-read, dropping handler callbacks (and the last lines off the pipe).
		await _completed.Task.ConfigureAwait(false);
		yield break;
	}
}
