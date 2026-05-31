namespace ProcessKit;

/// <summary>
/// Policy applied when the in-memory backlog of unconsumed output lines reaches
/// <see cref="OutputBufferPolicy.MaxBufferedLines"/>. The runner always keeps draining the OS
/// pipe (so the child never blocks); this only decides which buffered lines are kept for replay.
/// </summary>
public enum OutputOverflowMode
{
	/// <summary>
	/// Discard the oldest buffered line to make room for the newest (ring-buffer / "tail"
	/// semantics). The most recent output — where errors usually surface — survives.
	/// </summary>
	DropOldest,

	/// <summary>
	/// Discard the incoming line and keep what is already buffered ("head" semantics).
	/// </summary>
	DropNewest,
}
