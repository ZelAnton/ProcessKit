using System.Diagnostics;

namespace ProcessKit.Tests;

/// <summary>
/// Integration tests for <see cref="LinuxCgroupV2"/>. Most tests require real cgroup v2 mounted
/// AND delegation granted to the current process (root, container, or systemd unit with
/// <c>Delegate=yes</c>) — gated by <see cref="IsCgroupAvailable"/> so they skip cleanly on Windows /
/// macOS / Linux without delegation. The Linux CI runs in a Docker container with delegation enabled.
/// </summary>
public class CgroupV2Tests
{
	[Test]
	public void Mechanism_OnLinuxWithDelegation_ReportsCgroupV2()
	{
		Assume.That(IsCgroupAvailable(), "Requires cgroup v2 delegation.");
		using var group = new ProcessGroup();
		Assert.That(group.Mechanism, Is.EqualTo(Mechanism.CgroupV2));
	}

	[Test]
	public async Task StartAndAdd_PlacesChildInCgroup()
	{
		Assume.That(IsCgroupAvailable(), "Requires cgroup v2 delegation.");
		using var group = new ProcessGroup();

		var process = group.Start(TestExe.Sleep(5));

		var members = await group.GetMembersAsync();
		Assert.That(members, Does.Contain(process.Id),
			"Child PID must appear in cgroup.procs after StartAndAdd.");
	}

	[Test]
	public async Task SignalAsync_Kill_RemovesAllChildren()
	{
		Assume.That(IsCgroupAvailable(), "Requires cgroup v2 delegation.");
		using var group = new ProcessGroup();

		var process = group.Start(TestExe.Sleep(30));
		await group.SignalAsync(Signal.Kill);

		Assert.That(process.WaitForExit(5000), Is.True,
			"cgroup.kill must terminate the child within the timeout.");
	}

	[Test]
	public async Task GetMembersAsync_ListsLiveChildren()
	{
		Assume.That(IsCgroupAvailable(), "Requires cgroup v2 delegation.");
		using var group = new ProcessGroup();

		var p1 = group.Start(TestExe.Sleep(5));
		var p2 = group.Start(TestExe.Sleep(5));

		var members = await group.GetMembersAsync();
		Assert.That(members, Does.Contain(p1.Id));
		Assert.That(members, Does.Contain(p2.Id));
	}

	[Test]
	public async Task GetMembersAsync_OnEmptyGroup_ReturnsEmpty()
	{
		Assume.That(IsCgroupAvailable(), "Requires cgroup v2 delegation.");
		using var group = new ProcessGroup();
		var members = await group.GetMembersAsync();
		Assert.That(members, Is.Empty);
	}

	[Test]
	public async Task AdoptAsync_BringsExternalProcessIntoCgroup()
	{
		Assume.That(IsCgroupAvailable(), "Requires cgroup v2 delegation.");
		using var group = new ProcessGroup();

		// Start OUTSIDE the group.
		var external = Process.Start(TestExe.Sleep(15))
			?? throw new InvalidOperationException("Process.Start returned null.");

		try
		{
			await group.AdoptAsync(external);
			var members = await group.GetMembersAsync();
			Assert.That(members, Does.Contain(external.Id));
		}
		finally
		{
			try
			{
				if (!external.HasExited)
					external.Kill(entireProcessTree: true);
			}
			catch
			{
				// Process gone or disposed mid-cleanup — the goal here is "no longer running",
				// which is exactly the current state. Nothing to surface.
			}
			external.Dispose();
		}
	}

	// LinuxCgroupV2 is `[SupportedOSPlatform("linux")]`; the && short-circuit makes the analyzer
	// recognise the OS guard so calls don't trigger CA1416 outside this helper.
	static bool IsCgroupAvailable() =>
		OperatingSystem.IsLinux() && LinuxCgroupV2.IsAvailable();
}
