using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using ProcessKit.Diagnostics;

namespace ProcessKit.Tests;

public class DiagnosticsTests
{
	[Test]
	public async Task ProcessRunActivity_EmitsExpectedTagsOnNormalExit()
	{
		var activities = new ConcurrentBag<Activity>();
		using var listener = SubscribeListener(activities);

		var runner = new ProcessRunner();
		var result = await runner.GetFullOutputAsync(TestExe.Echo("hi"));
		Assert.That(result.ExitCode, Is.Zero);

		var span = activities.SingleOrDefault(a => a.OperationName == "processkit.process.run");
		Assert.That(span, Is.Not.Null, "processkit.process.run was not emitted");
		var s = span!;
		Assert.That(TagString(s, "program"), Is.Not.Empty);
		Assert.That(TagOrNull(s, "pid"), Is.Not.Null);
		Assert.That(TagOrNull(s, "exit_code"), Is.EqualTo(0));
		Assert.That(TagOrNull(s, "has_exit_code"), Is.EqualTo(true));
		Assert.That(TagOrNull(s, "timed_out"), Is.EqualTo(false));
		Assert.That(TagString(s, "mechanism"), Is.AnyOf("JobObject", "Pgroup"));
		Assert.That((long)TagOrNull(s, "duration_ms")!, Is.GreaterThanOrEqualTo(0));
		Assert.That(s.Status, Is.EqualTo(ActivityStatusCode.Unset).Or.EqualTo(ActivityStatusCode.Ok));
	}

	[Test]
	public async Task ProcessRunActivity_TagsTimedOutWhenTimeoutFires()
	{
		var activities = new ConcurrentBag<Activity>();
		using var listener = SubscribeListener(activities);

		var runner = new ProcessRunner();
		var result = await runner.GetFullOutputAsync(
			TestExe.Sleep(5),
			new ProcessRunOptions { Timeout = TimeSpan.FromMilliseconds(150) });

		Assert.That(result.WasTimedOut, Is.True);
		var span = activities.SingleOrDefault(a => a.OperationName == "processkit.process.run");
		Assert.That(span, Is.Not.Null);
		var s = span!;
		Assert.That(TagOrNull(s, "timed_out"), Is.EqualTo(true));
		Assert.That(s.Status, Is.EqualTo(ActivityStatusCode.Error));
	}

	[Test]
	public void GroupShutdownActivity_EmitsMechanismAndProcessCount()
	{
		var activities = new ConcurrentBag<Activity>();
		using var listener = SubscribeListener(activities);

		using (var group = new ProcessGroup())
		{
			var process = group.Start(TestExe.Sleep(0.1));
			process.WaitForExit();
		}

		var span = activities.SingleOrDefault(a => a.OperationName == "processkit.group.shutdown");
		Assert.That(span, Is.Not.Null, "processkit.group.shutdown was not emitted");
		var s = span!;
		Assert.That(TagString(s, "mechanism"), Is.AnyOf("JobObject", "Pgroup"));
		Assert.That(TagOrNull(s, "escalated_to_kill"), Is.Not.Null);
		Assert.That(TagOrNull(s, "process_count"), Is.Not.Null);
	}

	[Test]
	public async Task EventSource_EmitsProcessStartedAndExited()
	{
		// Force the EventSource singleton to be constructed before the listener so its name is
		// discoverable from OnEventSourceCreated regardless of test execution order.
		_ = ProcessKitEventSource.Log;

		using var capturer = new ProcessKitEventCapturer();

		var runner = new ProcessRunner();
		var result = await runner.GetFullOutputAsync(TestExe.Echo("evt"));
		Assert.That(result.ExitCode, Is.Zero);

		Assert.That(capturer.Events.Any(e => e.EventName == nameof(ProcessKitEventSource.ProcessStarted)), Is.True,
			"ProcessStarted event was not observed");
		Assert.That(capturer.Events.Any(e => e.EventName == nameof(ProcessKitEventSource.ProcessExited)), Is.True,
			"ProcessExited event was not observed");
	}

	static ActivityListener SubscribeListener(ConcurrentBag<Activity> sink)
	{
		var listener = new ActivityListener
		{
			ShouldListenTo = src => src.Name == ProcessKitActivitySource.Name,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
			ActivityStopped = sink.Add,
		};
		ActivitySource.AddActivityListener(listener);
		return listener;
	}

	static string TagString(Activity activity, string key) =>
		(activity.GetTagItem(key) as string) ?? string.Empty;

	static object? TagOrNull(Activity activity, string key) => activity.GetTagItem(key);

	sealed class ProcessKitEventCapturer : EventListener
	{
		public ConcurrentBag<EventWrittenEventArgs> Events { get; } = [];

		protected override void OnEventSourceCreated(EventSource eventSource)
		{
			if (eventSource.Name == ProcessKitEventSource.SourceName)
				EnableEvents(eventSource, EventLevel.Informational);
		}

		protected override void OnEventWritten(EventWrittenEventArgs eventData) => Events.Add(eventData);
	}
}
