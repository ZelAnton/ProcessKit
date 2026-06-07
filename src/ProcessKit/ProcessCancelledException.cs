namespace ProcessKit;

/// <summary>
/// Thrown by a <see cref="Command"/> verb when the <see cref="Command.WithCancellation"/> token
/// fires. Distinct from <see cref="Command.WithTimeout"/>-driven termination — a timeout is
/// captured in <see cref="ProcessResult{T}.WasTimedOut"/>, whereas a cancellation is always
/// "loud" and surfaces as this exception (matches Rust's <c>Error::Cancelled</c> semantics).
/// </summary>
/// <remarks>
/// Derives from <see cref="OperationCanceledException"/> so existing
/// <c>catch (OperationCanceledException)</c> blocks still catch cancellations transparently;
/// callers that need the program name can catch the specific type.
/// </remarks>
public sealed class ProcessCancelledException : OperationCanceledException
{
	/// <summary>The basename of the program that was cancelled.</summary>
	public string Program { get; }

	/// <summary>
	/// Forwards <paramref name="cancellationToken"/> to the base class so the standard
	/// <see cref="OperationCanceledException.CancellationToken"/> property reflects the token that
	/// actually cancelled the run — preserves the BCL convention callers rely on
	/// (<c>if (ex.CancellationToken == myCt) …</c>).
	/// </summary>
	public ProcessCancelledException(string program, CancellationToken cancellationToken)
		: base($"`{program}` was cancelled.", cancellationToken)
	{
		Program = program;
	}
}
