using System.Text;

namespace ProcessKit;

/// <summary>
/// Writer for a child process's standard input that is kept <strong>open</strong> after start
/// (interactive / REPL processes). Obtained from <see cref="IRunningProcess.StandardInput"/> when
/// the run was started with <see cref="ProcessRunOptions.KeepStandardInputOpen"/> set.
/// </summary>
/// <remarks>
/// Writes are serialized internally, so calls from multiple threads are safe and never interleave
/// a partial line; the ordering between concurrent callers is unspecified. Signal end-of-input with
/// <see cref="CompleteAsync"/> so the child sees EOF and can exit cleanly — otherwise the child is
/// terminated when the handle is disposed. Each write is flushed so the child receives it promptly.
/// </remarks>
public interface IProcessStandardInput
{
	/// <summary>Encoding used by <see cref="WriteLineAsync"/> (defaults to UTF-8).</summary>
	Encoding Encoding { get; }

	/// <summary>Writes raw bytes to stdin and flushes. Throws <see cref="InvalidOperationException"/> after <see cref="CompleteAsync"/>.</summary>
	ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

	/// <summary>Writes <paramref name="text"/> followed by a newline (in <see cref="Encoding"/>) and flushes.</summary>
	ValueTask WriteLineAsync(string text, CancellationToken cancellationToken = default);

	/// <summary>Flushes any buffered bytes to the child.</summary>
	ValueTask FlushAsync(CancellationToken cancellationToken = default);

	/// <summary>Closes stdin so the child sees end-of-input. Idempotent; further writes throw.</summary>
	ValueTask CompleteAsync();
}
