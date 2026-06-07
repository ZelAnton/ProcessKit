using System.Diagnostics;

namespace ProcessKit.Diagnostics;

/// <summary>
/// The single <see cref="System.Diagnostics.ActivitySource"/> used by ProcessKit to emit
/// distributed-tracing spans. Subscribe with an <see cref="ActivityListener"/> filtered by
/// <see cref="Name"/>, or attach an OpenTelemetry exporter pointed at the same source name.
/// </summary>
/// <remarks>
/// <para>Spans emitted by ProcessKit:</para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>processkit.process.run</c> — wraps a child process from start to exit. Tags:
///       <c>program</c> (executable basename only; full path is not recorded to avoid leaking
///       directory layout), <c>pid</c>, <c>mechanism</c> (<c>JobObject</c> / <c>Pgroup</c> /
///       <c>None</c>), <c>exit_code</c>, <c>has_exit_code</c>, <c>timed_out</c>, <c>duration_ms</c>.
///       Status is <see cref="ActivityStatusCode.Error"/> when the process was killed by timeout
///       or the start sequence threw.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>processkit.group.shutdown</c> — wraps <see cref="ProcessGroup.Dispose"/> /
///       <see cref="ProcessGroup.DisposeAsync"/>. Tags: <c>mechanism</c>,
///       <c>escalated_to_kill</c>, <c>process_count</c> (snapshot taken before teardown).
///     </description>
///   </item>
/// </list>
/// <para>
/// Tags never contain <c>argv</c> or environment variables — both can carry secrets. Callers
/// that need richer context should attach their own tags via an <see cref="ActivityListener"/>
/// or a parent activity.
/// </para>
/// </remarks>
public static class ProcessKitActivitySource
{
	/// <summary>The <see cref="ActivitySource.Name"/> used for all ProcessKit spans.</summary>
	public const string Name = "ProcessKit";

	/// <summary>The shared <see cref="ActivitySource"/> instance ProcessKit emits spans through.</summary>
	public static readonly ActivitySource Source = new(Name, "1.4.0");
}
