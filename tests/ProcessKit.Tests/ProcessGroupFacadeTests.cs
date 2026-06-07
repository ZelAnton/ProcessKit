using System.Diagnostics;

namespace ProcessKit.Tests;

/// <summary>
/// Deterministic unit tests for the <see cref="ProcessGroup"/> façade, driven by
/// <see cref="FakeProcessGroupImpl"/> — no real OS process. Covers the logic that runs before
/// (and around) the platform impl: argument/disposed guards, delegation, dispose idempotency, and
/// pre-start cancellation. The real kill-on-cancel path stays in the integration <c>ProcessGroupTests</c>.
/// </summary>
public class ProcessGroupFacadeTests
{
	[Test]
	public void Add_DelegatesToInner()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);
		using var process = new Process();

		group.Add(process);

		Assert.That(impl.AddCount, Is.EqualTo(1));
	}

	[Test]
	public void Add_Null_Throws_WithoutDelegating()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);

		Assert.Throws<ArgumentNullException>(() => group.Add(null!));
		Assert.That(impl.AddCount, Is.Zero);
	}

	[Test]
	public void TerminateAll_DelegatesToInner()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);

		group.TerminateAll();

		Assert.That(impl.TerminateAllCount, Is.EqualTo(1));
	}

	[Test]
	public void GetStats_ReturnsImplValue()
	{
		var impl = new FakeProcessGroupImpl { StatsToReturn = new ProcessGroupStats(3, TimeSpan.FromSeconds(2), 999) };
		using var group = new ProcessGroup(impl);

		var stats = group.GetStats();

		Assert.That(stats.ActiveProcessCount, Is.EqualTo(3));
		Assert.That(stats.PeakMemoryBytes, Is.EqualTo(999));
	}

	[Test]
	public void Start_NullStartInfo_Throws()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);

		Assert.Throws<ArgumentNullException>(() => group.Start(null!));
		Assert.That(impl.StartAndAddCount, Is.Zero);
	}

	[Test]
	public void Start_WithCancelledToken_Throws_BeforeStartAndAdd()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		Assert.Throws<OperationCanceledException>(() => group.Start(new ProcessStartInfo("x"), cts.Token));
		Assert.That(impl.StartAndAddCount, Is.Zero);
	}

	[Test]
	public void Dispose_IsIdempotent_CallsImplDisposeOnce()
	{
		var impl = new FakeProcessGroupImpl();
		var group = new ProcessGroup(impl);

		group.Dispose();
		group.Dispose();

		Assert.That(impl.DisposeCount, Is.EqualTo(1));
	}

	[Test]
	public async Task DisposeAsync_IsIdempotent_CallsImplDisposeAsyncOnce()
	{
		var impl = new FakeProcessGroupImpl();
		var group = new ProcessGroup(impl);

		await group.DisposeAsync();
		await group.DisposeAsync();

		Assert.That(impl.DisposeAsyncCount, Is.EqualTo(1));
	}

	[Test]
	public async Task DisposeThenDisposeAsync_OnlyFirstTeardownRuns()
	{
		var impl = new FakeProcessGroupImpl();
		var group = new ProcessGroup(impl);

		group.Dispose();
		await group.DisposeAsync();

		Assert.That(impl.DisposeCount, Is.EqualTo(1));
		Assert.That(impl.DisposeAsyncCount, Is.Zero); // shared _disposed flag gated the second path
	}

	[Test]
	public void Operations_AfterDispose_ThrowObjectDisposedException()
	{
		var impl = new FakeProcessGroupImpl();
		var group = new ProcessGroup(impl);
		group.Dispose();

		using var process = new Process();
		Assert.Throws<ObjectDisposedException>(() => group.Start(new ProcessStartInfo("x")));
		Assert.Throws<ObjectDisposedException>(() => group.Add(process));
		Assert.Throws<ObjectDisposedException>(group.TerminateAll);
		Assert.Throws<ObjectDisposedException>(() => group.GetStats());
		Assert.ThrowsAsync<ObjectDisposedException>(() => group.SignalAsync(Signal.Term));
		Assert.ThrowsAsync<ObjectDisposedException>(() => group.SignalAsync(new CustomSignal(15)));
		Assert.ThrowsAsync<ObjectDisposedException>(() => group.SuspendAsync());
		Assert.ThrowsAsync<ObjectDisposedException>(() => group.ResumeAsync());
		Assert.ThrowsAsync<ObjectDisposedException>(() => group.GetMembersAsync());
		Assert.ThrowsAsync<ObjectDisposedException>(() => group.AdoptAsync(process));
	}

	[Test]
	public void Mechanism_ReturnsImplValue()
	{
		var impl = new FakeProcessGroupImpl { MechanismToReturn = Mechanism.JobObject };
		using var group = new ProcessGroup(impl);

		Assert.That(group.Mechanism, Is.EqualTo(Mechanism.JobObject));
	}

	[Test]
	public async Task SignalAsync_Canonical_DelegatesToImpl_WithSignalRecorded()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);

		await group.SignalAsync(Signal.Term);

		Assert.That(impl.SignalAsyncCount, Is.EqualTo(1));
		Assert.That(impl.LastSignal, Is.EqualTo(Signal.Term));
	}

	[Test]
	public async Task SignalAsync_Custom_DelegatesToImpl_WithNumberRecorded()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);

		await group.SignalAsync(new CustomSignal(42));

		Assert.That(impl.CustomSignalAsyncCount, Is.EqualTo(1));
		Assert.That(impl.LastCustomSignal, Is.EqualTo(new CustomSignal(42)));
	}

	[Test]
	public async Task SuspendResumeAsync_DelegatesToInner()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);

		await group.SuspendAsync();
		await group.ResumeAsync();

		Assert.That(impl.SuspendAsyncCount, Is.EqualTo(1));
		Assert.That(impl.ResumeAsyncCount, Is.EqualTo(1));
	}

	[Test]
	public async Task GetMembersAsync_ReturnsImplValue()
	{
		var impl = new FakeProcessGroupImpl { MembersToReturn = [101, 202, 303] };
		using var group = new ProcessGroup(impl);

		var members = await group.GetMembersAsync();

		Assert.That(impl.GetMembersAsyncCount, Is.EqualTo(1));
		Assert.That(members, Is.EqualTo((int[])[101, 202, 303]));
	}

	[Test]
	public void AdoptAsync_Null_ThrowsArgumentNullException()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);

		Assert.ThrowsAsync<ArgumentNullException>(() => group.AdoptAsync(null!));
		Assert.That(impl.AddCount, Is.Zero);
	}

	[Test]
	public void AdoptAsync_NotStartedProcess_ThrowsArgumentException()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);
		using var process = new Process();

		// process.Id getter throws InvalidOperationException for an unstarted Process; the façade
		// translates that into ArgumentException with the parameter name.
		var ex = Assert.ThrowsAsync<ArgumentException>(() => group.AdoptAsync(process));
		Assert.That(ex!.ParamName, Is.EqualTo("externalProcess"));
		Assert.That(impl.AddCount, Is.Zero);
	}

	[Test]
	public async Task SignalAsync_PreCancelledToken_ThrowsBeforeDelegating()
	{
		var impl = new FakeProcessGroupImpl();
		using var group = new ProcessGroup(impl);
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		Assert.ThrowsAsync<OperationCanceledException>(() => group.SignalAsync(Signal.Term, cts.Token));
	}
}
