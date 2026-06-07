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
///   <item>
///     <description>
///       <c>processkit.probe.line</c> / <c>processkit.probe.custom</c> / <c>processkit.probe.port</c>
///       — readiness probes on a running process. Tags: <c>program</c>, <c>within_ms</c>; the
///       custom probe additionally carries <c>poll_ms</c>, the port probe carries <c>endpoint</c>.
///       Status is <see cref="ActivityStatusCode.Error"/> when the probe fails (deadline elapsed
///       or child exited first) — the failure throws <see cref="ProcessNotReadyException"/> and
///       does NOT kill the child.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>processkit.pipeline.run</c> — full lifecycle of a <see cref="ProcessPipeline"/>
///       (shell-free <c>a | b | c</c>). Tags: <c>stage_count</c>, <c>timeout_ms</c>,
///       <c>winner_index</c>, <c>winner_program</c>, <c>exit_code</c>, <c>timed_out</c>. Status
///       flips to <see cref="ActivityStatusCode.Error"/> on timeout / cancellation. Inner stages
///       do NOT emit their own <c>processkit.process.run</c> spans (the pipeline bypasses the
///       per-stage session); per-stage timing is currently summarised at pipeline granularity.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>processkit.retry.attempt</c> — one span per attempt when a <see cref="Command"/>'s
///       success-checking verb (<see cref="Command.RunAsync"/> / <see cref="Command.ExitCodeAsync"/>
///       / <see cref="Command.ProbeAsync"/>) is configured via <see cref="Command.WithRetry"/>.
///       Tags: <c>attempt</c> (1-based), <c>delay_ms_before</c>, <c>program</c>,
///       <c>max_attempts</c>, <c>error_type</c> (on failure). Status flips to
///       <see cref="ActivityStatusCode.Error"/> on per-attempt failure and "cancelled" on terminal
///       cancellation.
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
