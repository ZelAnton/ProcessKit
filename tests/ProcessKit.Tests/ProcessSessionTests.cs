using System.Diagnostics;
using System.Text;

namespace ProcessKit.Tests;

/// <summary>
/// Deterministic, in-memory unit tests for the <see cref="ProcessSession"/> lifecycle, driven by a
/// <see cref="FakeProcessHandle"/> — no real OS process. These cover the concurrency-heavy paths
/// (exit races, diagnostics caching, timeout/cancellation, sink dispatch) that the real-process
/// integration tests can only exercise non-deterministically.
/// </summary>
public class ProcessSessionTests
{
	static ProcessSession NewSession(
		FakeProcessHandle handle,
		IStdOutSink sink,
		ProcessRunOptions? options = null,
		CancellationToken cancellationToken = default)
		=> new(new ProcessStartInfo("fake"), options, sink, new FakeProcessHandleFactory(handle), cancellationToken);

	[Test]
	public async Task LineSink_StreamsDecodedLines()
	{
		var handle = new FakeProcessHandle(stdout: "first\nsecond\nthird\n"u8.ToArray());
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink);

		var lines = new List<string>();
		await foreach (var line in sink.ReadAllAsync())
			lines.Add(line);

		Assert.That(lines, Is.EqualTo((string[])["first", "second", "third"]));
		Assert.That(sink.LineCount, Is.EqualTo(3));
	}

	[Test]
	public async Task ByteSink_CapturesRawBytes()
	{
		var payload = new byte[] { 0x00, 0x01, 0x02, 0xff, 0xfe, 0xfd };
		var handle = new FakeProcessHandle(stdout: payload);
		var sink = new ByteBufferStdOutSink();
		await using var session = NewSession(handle, sink);

		await session.StdOutPumpCompletion;

		Assert.That(sink.ToArray(), Is.EqualTo(payload));
	}

	[Test]
	public async Task SyncExitDuringConstruction_ResolvesCompletionExactlyOnce()
	{
		var handle = new FakeProcessHandle(exitCode: 5);
		handle.PresetExited(); // already exited before the session subscribes — exercises the sync fire
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink);

		Assert.That(session.Completion.IsCompleted, Is.True);
		Assert.That(await session.Completion, Is.EqualTo(5));
		Assert.That(session.Duration, Is.Not.Null);
	}

	[Test]
	public async Task ExitCode_IsPropagatedThroughCompletion()
	{
		var handle = new FakeProcessHandle(exitCode: 7);
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink);

		handle.RaiseExited();

		Assert.That(await session.Completion, Is.EqualTo(7));
	}

	[Test]
	public async Task OnExited_IsIdempotent_UnderRepeatedEvents()
	{
		var handle = new FakeProcessHandle(exitCode: 3);
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink);

		handle.RaiseExited();
		var first = await session.Completion;
		var duration = session.Duration;

		// Firing again must not tear or change the observed state (the _exitedHandled guard).
		handle.RaiseExited();
		handle.RaiseExited();

		Assert.That(await session.Completion, Is.EqualTo(first));
		Assert.That(session.Duration, Is.EqualTo(duration));
	}

	[Test]
	public async Task ThrowingCounters_YieldNullDiagnostics_WithoutThrowing()
	{
		var handle = new FakeProcessHandle { ThrowOnCounters = true };
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink);

		Assert.That(session.CpuTime, Is.Null);          // live read: counter throws → null
		Assert.That(session.PeakMemoryBytes, Is.Null);

		handle.RaiseExited();
		await session.Completion;

		Assert.That(session.CpuTime, Is.Null);          // caching attempt also threw → still null
		Assert.That(session.PeakMemoryBytes, Is.Null);
	}

	[Test]
	public async Task Diagnostics_AreCachedAndSurviveExit()
	{
		var handle = new FakeProcessHandle(exitCode: 0);
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink);

		handle.RaiseExited();
		await session.Completion;

		Assert.That(session.CpuTime, Is.EqualTo(TimeSpan.FromMilliseconds(5)));
		Assert.That(session.PeakMemoryBytes, Is.EqualTo(4096));
		Assert.That(session.Duration, Is.Not.Null);
	}

	[Test]
	public async Task Timeout_SetsWasTimedOut_AndKillsHandle()
	{
		var handle = new FakeProcessHandle();
		var sink = new LineChannelStdOutSink(null);
		var options = new ProcessRunOptions { Timeout = TimeSpan.FromMilliseconds(100) };
		await using var session = NewSession(handle, sink, options);

		await session.Completion; // timeout fires → killCts → fake killed → Exited

		Assert.That(session.WasTimedOut, Is.True);
		Assert.That(handle.KillCount, Is.GreaterThan(0));
	}

	[Test]
	public async Task ExternalCancellation_KillsHandle_AndIsNotReportedAsTimeout()
	{
		var handle = new FakeProcessHandle();
		var sink = new LineChannelStdOutSink(null);
		using var cts = new CancellationTokenSource();
		await using var session = NewSession(handle, sink, cancellationToken: cts.Token);

		await cts.CancelAsync();
		await session.Completion;

		Assert.That(handle.KillCount, Is.GreaterThan(0));
		Assert.That(session.WasTimedOut, Is.False);
	}

	[Test]
	public async Task StdErr_StreamsLinesAndCounts()
	{
		var handle = new FakeProcessHandle(stderr: "e1\ne2\n");
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink);

		var lines = new List<string>();
		await foreach (var line in session.StdErr)
			lines.Add(line);

		Assert.That(lines, Is.EqualTo((string[])["e1", "e2"]));
		Assert.That(session.StdErrLineCount, Is.EqualTo(2));
	}

	[Test]
	public async Task BoundedBuffer_DropsOldest_ButCounterCountsEveryLine()
	{
		var handle = new FakeProcessHandle(stdout: "l1\nl2\nl3\nl4\nl5\n"u8.ToArray());
		var policy = new OutputBufferPolicy { MaxBufferedLines = 2, Overflow = OutputOverflowMode.DropOldest };
		var sink = new LineChannelStdOutSink(policy);
		await using var session = NewSession(handle, sink);

		// Let the pump finish writing (and dropping) before reading, so the retained set is stable.
		await session.StdOutPumpCompletion;

		var retained = new List<string>();
		await foreach (var line in sink.ReadAllAsync())
			retained.Add(line);

		Assert.That(sink.LineCount, Is.EqualTo(5));                          // every line counted off the pipe
		Assert.That(retained, Is.EqualTo((string[])["l4", "l5"]));          // only the newest 2 retained
	}

	[Test]
	public async Task KeepStandardInputOpen_ExposesWriter_AndWritesReachStdin()
	{
		var handle = new FakeProcessHandle();
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink, new ProcessRunOptions { KeepStandardInputOpen = true });

		Assert.That(session.InteractiveInput, Is.Not.Null);
		await session.InteractiveInput!.WriteLineAsync("hello");
		await session.InteractiveInput.CompleteAsync();

		Assert.That(Encoding.UTF8.GetString(handle.CapturedStdin()), Does.Contain("hello"));
	}

	[Test]
	public async Task NoKeepStandardInputOpen_InteractiveInputIsNull()
	{
		var handle = new FakeProcessHandle();
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink);

		Assert.That(session.InteractiveInput, Is.Null);
	}

	[Test]
	public async Task InteractiveInput_WriteAfterComplete_Throws()
	{
		var handle = new FakeProcessHandle();
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink, new ProcessRunOptions { KeepStandardInputOpen = true });

		await session.InteractiveInput!.CompleteAsync();

		Assert.ThrowsAsync<InvalidOperationException>(async () => await session.InteractiveInput!.WriteLineAsync("x"));
	}

	[Test]
	public async Task InteractiveInput_CompleteAsync_IsIdempotent()
	{
		var handle = new FakeProcessHandle();
		var sink = new LineChannelStdOutSink(null);
		await using var session = NewSession(handle, sink, new ProcessRunOptions { KeepStandardInputOpen = true });

		await session.InteractiveInput!.CompleteAsync();
		Assert.DoesNotThrowAsync(async () => await session.InteractiveInput!.CompleteAsync());
	}

	[Test]
	public void KeepStandardInputOpen_WithUpfrontStandardInput_Throws()
	{
		var handle = new FakeProcessHandle();
		var sink = new LineChannelStdOutSink(null);
		var options = new ProcessRunOptions { KeepStandardInputOpen = true, StandardInput = StandardInput.FromString("x") };

		Assert.Throws<ArgumentException>(() => NewSession(handle, sink, options));
	}

	[Test]
	public async Task PumpTeardownTimeout_Option_IsAcceptedAndDisposesCleanly()
	{
		var handle = new FakeProcessHandle(stdout: "x\n"u8.ToArray(), exitCode: 0);
		var sink = new LineChannelStdOutSink(null);
		var options = new ProcessRunOptions { PumpTeardownTimeout = TimeSpan.FromMilliseconds(250) };
		var session = NewSession(handle, sink, options);

		handle.RaiseExited();
		await session.Completion;
		await session.DisposeAsync(); // honors the configured teardown budget; must not throw

		Assert.That(session.Completion.IsCompletedSuccessfully, Is.True);
	}

	[Test]
	public async Task DiscardBuffer_RetainsNothing_ButCounts()
	{
		var handle = new FakeProcessHandle(stdout: "a\nb\nc\n"u8.ToArray());
		var policy = new OutputBufferPolicy { MaxBufferedLines = 0 };
		var sink = new LineChannelStdOutSink(policy);
		await using var session = NewSession(handle, sink);

		await session.StdOutPumpCompletion;

		var retained = new List<string>();
		await foreach (var line in sink.ReadAllAsync())
			retained.Add(line);

		Assert.That(retained, Is.Empty);
		Assert.That(sink.LineCount, Is.EqualTo(3));
	}
}
