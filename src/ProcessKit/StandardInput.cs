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
/// Runners pattern-match on the concrete subtype to decide how to pump input.
/// </remarks>
public abstract class StandardInput
{
	private protected StandardInput() { }

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

	internal sealed class StringInput(string Text, Encoding Encoding) : StandardInput
	{
		public string Text { get; } = Text;
		public Encoding Encoding { get; } = Encoding;
	}

	internal sealed class BytesInput(ReadOnlyMemory<byte> Bytes) : StandardInput
	{
		public ReadOnlyMemory<byte> Bytes { get; } = Bytes;
	}

	internal sealed class StreamInput(Stream Stream, bool LeaveOpen) : StandardInput
	{
		public Stream Stream { get; } = Stream;
		public bool LeaveOpen { get; } = LeaveOpen;
	}

	internal sealed class LinesInput(IAsyncEnumerable<string> Lines, Encoding Encoding) : StandardInput
	{
		public IAsyncEnumerable<string> Lines { get; } = Lines;
		public Encoding Encoding { get; } = Encoding;
	}

	internal sealed class EnumerableInput(IEnumerable<string> Lines, Encoding Encoding) : StandardInput
	{
		public IEnumerable<string> Lines { get; } = Lines;
		public Encoding Encoding { get; } = Encoding;
	}

	internal sealed class FileInput(string Path) : StandardInput
	{
		public string Path { get; } = Path;
	}
}
