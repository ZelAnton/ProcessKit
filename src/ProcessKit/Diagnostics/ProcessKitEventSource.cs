using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;

namespace ProcessKit.Diagnostics;

/// <summary>
/// Structured events emitted by ProcessKit. Subscribe via an <see cref="EventListener"/> filtered
/// on <see cref="EventSource.Name"/> = <c>"ProcessKit"</c>, or capture with
/// <c>dotnet-trace collect --providers ProcessKit</c>.
/// </summary>
/// <remarks>
/// Events are intentionally compact and free of caller-supplied strings beyond the program
/// basename — <c>argv</c> and environment variables are never recorded so secrets cannot leak
/// through diagnostics channels. Subscribers that need richer context should listen to the
/// <see cref="ProcessKitActivitySource"/> spans instead, which carry the same identifiers as
/// the events and can be enriched by parent activities.
/// </remarks>
[EventSource(Name = SourceName)]
public sealed class ProcessKitEventSource : EventSource
{
	/// <summary>The <see cref="EventSource.Name"/> used by ProcessKit's event source.</summary>
	public const string SourceName = "ProcessKit";

	/// <summary>The shared singleton instance ProcessKit writes events to.</summary>
	public static readonly ProcessKitEventSource Log = new();

	ProcessKitEventSource() { }

	/// <summary>Fired immediately after a child process is started.</summary>
	[Event(1, Level = EventLevel.Informational)]
	public void ProcessStarted(int pid, string program)
	{
		// Matches the strongly-typed WriteEvent(int, int, string) overload — AOT-safe (no reflection-
		// driven params object[] fallback).
		if (IsEnabled())
			WriteEvent(1, pid, program);
	}

	/// <summary>
	/// Fired once per started process, when its lifecycle observably ends (normal exit, timeout
	/// kill, or external cancellation). <paramref name="hasExitCode"/> is <c>false</c> when the
	/// platform reports no code (e.g. terminated by signal on Unix); <paramref name="exitCode"/>
	/// is then unspecified.
	/// </summary>
	[Event(2, Level = EventLevel.Informational)]
	[UnconditionalSuppressMessage("Trimming", "IL2026",
		Justification = "All payload arguments are primitive types (int/long) with fixed layout; WriteEventCore writes them directly via EventData* without serialising any object graph.")]
	public unsafe void ProcessExited(int pid, int exitCode, bool hasExitCode, bool timedOut, long durationMs)
	{
		// No strongly-typed WriteEvent overload accepts (int, int, int, int, long). The fall-back
		// WriteEvent(int, params object[]) would box every argument and inspect them via reflection —
		// incompatible with the AOT guarantee. Build the EventData payload by hand and use
		// WriteEventCore (which is RUC-annotated, but the per-arg primitive payload above is exactly
		// the suppression's stated safe case).
		if (!IsEnabled())
			return;

		var hasExitCodeInt = hasExitCode ? 1 : 0;
		var timedOutInt = timedOut ? 1 : 0;

		var data = stackalloc EventData[5];
		data[0].DataPointer = (nint)(&pid);
		data[0].Size = sizeof(int);
		data[1].DataPointer = (nint)(&exitCode);
		data[1].Size = sizeof(int);
		data[2].DataPointer = (nint)(&hasExitCodeInt);
		data[2].Size = sizeof(int);
		data[3].DataPointer = (nint)(&timedOutInt);
		data[3].Size = sizeof(int);
		data[4].DataPointer = (nint)(&durationMs);
		data[4].Size = sizeof(long);
		WriteEventCore(2, 5, data);
	}

	/// <summary>
	/// Fired once per <see cref="ProcessGroup"/> teardown. <paramref name="mechanism"/> is one of
	/// <c>"JobObject"</c>, <c>"Pgroup"</c>, <c>"None"</c>. <paramref name="escalated"/> indicates
	/// SIGKILL escalation on Unix; on Windows it is always <c>false</c> because the Job Object
	/// terminates members atomically.
	/// </summary>
	[Event(3, Level = EventLevel.Informational)]
	public void GroupShutdown(string mechanism, bool escalated, int processCount)
	{
		// Matches the strongly-typed WriteEvent(int, string, int, int) overload — AOT-safe.
		if (IsEnabled())
			WriteEvent(3, mechanism, escalated ? 1 : 0, processCount);
	}
}
