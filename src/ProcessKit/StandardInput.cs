using System.Text;

namespace ProcessKit;

/// <summary>
/// Source of data to feed to a child process's standard input. Construct an instance via
/// one of the static factory methods (<see cref="FromString"/>, <see cref="FromBytes"/>,
/// <see cref="FromStream"/>, <see cref="FromLines"/>, <see cref="FromEnumerable"/>,
/// <see cref="FromFile"/>) or use <see cref="Empty"/>.
/// </summary>
/// <remarks>
/// The type is a closed hierarchy: only the implementations shipped with this library are valid.
/// Each subtype knows how to pump itself into the child's stdin via <c>WriteToAsync</c>; the
/// runner orchestrates that call and owns the surrounding exception-containment and stdin-close.
/// </remarks>
public abstract class StandardInput
{
	private protected StandardInput() { }

	/// <summary>
	/// True if this source can be replayed (re-pumped) for a retry attempt. Reusable factories
	/// (<see cref="FromString"/>, <see cref="FromBytes"/>, <see cref="FromFile"/>,
	/// <see cref="FromEnumerable"/>, <see cref="Empty"/>) return true; one-shot factories
	/// (<see cref="FromStream"/>, <see cref="FromLines"/>) override to false. Used by
	/// <see cref="Command.WithRetry"/> to reject incompatible inputs upfront — a second attempt
	/// against a one-shot source would see empty stdin.
	/// </summary>
	internal virtual bool IsReplayable => true;

	/// <summary>
	/// Pumps this input source into <paramref name="destination"/> (the child's redirected stdin
	/// base stream). Implementations must NOT close <paramref name="destination"/> — the runner
	/// closes it in one place so the "stdin is closed at start" contract holds uniformly. The base
	/// implementation writes nothing (used by <see cref="Empty"/>). Implementations may throw; the
	/// runner contains exceptions so a faulty user-supplied source never derails teardown.
	/// </summary>
	internal virtual Task WriteToAsync(Stream destination, CancellationToken cancellationToken)
		=> Task.CompletedTask;

	/// <summary>Represents "no input"; stdin is closed immediately after the process starts.</summary>
	public static StandardInput Empty { get; } = new EmptyInput();

	/// <summary>Feeds the given <paramref name="text"/> to stdin as a single write. Encoding defaults to UTF-8.</summary>
	public static StandardInput FromString(string text, Encoding? encoding = null)
	{
		ArgumentNullException.ThrowIfNull(text);
		return new StringInput(text, encoding ?? Encoding.UTF8);
	}

	/// <summary>Feeds the given bytes to stdin as a single write.</summary>
	public static StandardInput FromBytes(ReadOnlyMemory<byte> bytes)
		=> new BytesInput(bytes);

	/// <summary>
	/// Copies the given <paramref name="stream"/> into stdin. The stream is read from its current
	/// position to the end. If <paramref name="leaveOpen"/> is <c>false</c> (default), the runner
	/// disposes the stream after the copy completes.
	/// </summary>
	public static StandardInput FromStream(Stream stream, bool leaveOpen = false)
	{
		ArgumentNullException.ThrowIfNull(stream);

		return new StreamInput(stream, leaveOpen);
	}

	/// <summary>
	/// Feeds the yielded strings to stdin line by line, appending a newline after each. The
	/// runner enumerates the source as the process consumes input. Encoding defaults to UTF-8.
	/// </summary>
	public static StandardInput FromLines(IAsyncEnumerable<string> lines, Encoding? encoding = null)
	{
		ArgumentNullException.ThrowIfNull(lines);

		return new LinesInput(lines, encoding ?? Encoding.UTF8);
	}

	/// <summary>
	/// Synchronous counterpart of <see cref="FromLines"/>: feeds each string from the given
	/// <see cref="IEnumerable{T}"/> as a line. Encoding defaults to UTF-8.
	/// </summary>
	public static StandardInput FromEnumerable(IEnumerable<string> lines, Encoding? encoding = null)
	{
		ArgumentNullException.ThrowIfNull(lines);

		return new EnumerableInput(lines, encoding ?? Encoding.UTF8);
	}

	/// <summary>
	/// Pipes the contents of the file at <paramref name="path"/> into stdin. The file is opened
	/// lazily when the process starts. Existence is validated eagerly here — if the file is
	/// missing, this method throws <see cref="FileNotFoundException"/> immediately rather than
	/// letting the runner silently swallow the error during stdin pumping.
	/// </summary>
	/// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
	public static StandardInput FromFile(string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);

		return File.Exists(path)
			? new FileInput(path)
			: throw new FileNotFoundException("Standard input file not found.", path);
	}

	internal sealed class EmptyInput : StandardInput;

	internal sealed class StringInput(string text, Encoding encoding) : StandardInput
	{
		internal override async Task WriteToAsync(Stream destination, CancellationToken cancellationToken)
		{
			var bytes = encoding.GetBytes(text);
			await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
		}
	}

	internal sealed class BytesInput(ReadOnlyMemory<byte> bytes) : StandardInput
	{
		internal override async Task WriteToAsync(Stream destination, CancellationToken cancellationToken)
			=> await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
	}

	internal sealed class StreamInput(Stream stream, bool leaveOpen) : StandardInput
	{
		// One-shot: a Stream's position advances after the first pump; we cannot reset it for a
		// retry without knowing the source's seek semantics. Callers wanting retry must materialise
		// to bytes via FromBytes/FromString/FromFile first.
		internal override bool IsReplayable => false;

		internal override async Task WriteToAsync(Stream destination, CancellationToken cancellationToken)
		{
			try
			{
				await stream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				if (!leaveOpen)
					await stream.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	internal sealed class LinesInput(IAsyncEnumerable<string> lines, Encoding encoding) : StandardInput
	{
		// One-shot: IAsyncEnumerable<T> has no contract for re-enumeration; many implementations
		// produce a single iterator that's consumed by the first await foreach. Block retry to
		// avoid silent empty-stdin on the second attempt.
		internal override bool IsReplayable => false;

		internal override async Task WriteToAsync(Stream destination, CancellationToken cancellationToken)
		{
			var newline = encoding.GetBytes(Environment.NewLine);
			await foreach (var line in lines.WithCancellation(cancellationToken).ConfigureAwait(false))
			{
				var bytes = encoding.GetBytes(line);
				await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
				await destination.WriteAsync(newline, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	internal sealed class EnumerableInput(IEnumerable<string> lines, Encoding encoding) : StandardInput
	{
		internal override async Task WriteToAsync(Stream destination, CancellationToken cancellationToken)
		{
			var newline = encoding.GetBytes(Environment.NewLine);
			foreach (var line in lines)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var bytes = encoding.GetBytes(line);
				await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
				await destination.WriteAsync(newline, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	internal sealed class FileInput(string path) : StandardInput
	{
		internal override async Task WriteToAsync(Stream destination, CancellationToken cancellationToken)
		{
			await using var fs = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 4096,
				useAsync: true);
			await fs.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
		}
	}
}
