using System.Text;

namespace ProcessKit.Tests;

/// <summary>
/// Deterministic unit tests for <see cref="ProcessRunOptionsMerge"/> — no process spawn.
/// </summary>
public class ProcessRunOptionsMergeTests
{
	[Test]
	public void NullDefaults_ReturnsPerCallInstance()
	{
		var perCall = new ProcessRunOptions {
			Timeout = TimeSpan.FromSeconds(3),
		};
		Assert.That(ProcessRunOptionsMerge.Merge(null, perCall), Is.SameAs(perCall));
	}

	[Test]
	public void NullPerCall_ReturnsDefaultsInstance()
	{
		var defaults = new ProcessRunOptions {
			Timeout = TimeSpan.FromSeconds(3),
		};
		Assert.That(ProcessRunOptionsMerge.Merge(defaults, null), Is.SameAs(defaults));
	}

	[Test]
	public void PerCallScalar_OverridesDefault()
	{
		var defaults = new ProcessRunOptions {
			Timeout = TimeSpan.FromSeconds(1),
		};
		var perCall = new ProcessRunOptions {
			Timeout = TimeSpan.FromSeconds(9),
		};

		var merged = ProcessRunOptionsMerge.Merge(defaults, perCall);

		Assert.That(merged!.Timeout, Is.EqualTo(TimeSpan.FromSeconds(9)));
	}

	[Test]
	public void DefaultScalar_AppliesWhenPerCallUnset()
	{
		var defaults = new ProcessRunOptions {
			Timeout = TimeSpan.FromSeconds(7),
			StdOutEncoding = Encoding.ASCII,
		};
		var perCall = new ProcessRunOptions {
			StandardErrorHandler = _ => { },
		};

		var merged = ProcessRunOptionsMerge.Merge(defaults, perCall);

		Assert.That(merged!.Timeout, Is.EqualTo(TimeSpan.FromSeconds(7)));
		Assert.That(merged.StdOutEncoding, Is.EqualTo(Encoding.ASCII));
		Assert.That(merged.StandardErrorHandler, Is.Not.Null);
	}

	[Test]
	public void Environment_IsUnioned_PerCallKeyWins()
	{
		var defaults = new ProcessRunOptions
		{
			Environment = new Dictionary<string, string?> {
				["A"] = "1",
				["B"] = "2",
			},
		};
		var perCall = new ProcessRunOptions
		{
			Environment = new Dictionary<string, string?> {
				["B"] = "override",
				["C"] = "3",
			},
		};

		var merged = ProcessRunOptionsMerge.Merge(defaults, perCall);

		Assert.That(merged!.Environment!["A"], Is.EqualTo("1"));
		Assert.That(merged.Environment["B"], Is.EqualTo("override"));
		Assert.That(merged.Environment["C"], Is.EqualTo("3"));
	}

	[Test]
	public void Environment_PerCallNullValue_IsPreserved_AsRemoval()
	{
		var defaults = new ProcessRunOptions {
			Environment = new Dictionary<string, string?> {
				["X"] = "keep",
			},
		};
		var perCall = new ProcessRunOptions {
			Environment = new Dictionary<string, string?> { ["X"] = null },
		};

		var merged = ProcessRunOptionsMerge.Merge(defaults, perCall);

		Assert.That(merged!.Environment!.ContainsKey("X"), Is.True);
		Assert.That(merged.Environment["X"], Is.Null);
	}

	[Test]
	public void KeepStandardInputOpen_IsPerCallOnly_NotInheritedFromDefaults()
	{
		var defaults = new ProcessRunOptions {
			KeepStandardInputOpen = true,
		};
		var perCall = new ProcessRunOptions {
			Timeout = TimeSpan.FromSeconds(1),
		};

		var merged = ProcessRunOptionsMerge.Merge(defaults, perCall);

		Assert.That(merged!.KeepStandardInputOpen, Is.False);
	}

	[Test]
	public void KeepStandardInputOpen_NotInherited_EvenWhenPerCallNull()
	{
		// The perCall==null fast path must still clear KeepStandardInputOpen, or a runner default
		// would re-enable interactive stdin (and hang the bulk helpers).
		var defaults = new ProcessRunOptions {
			KeepStandardInputOpen = true,
			Timeout = TimeSpan.FromSeconds(1),
		};

		var merged = ProcessRunOptionsMerge.Merge(defaults, null);

		Assert.That(merged!.KeepStandardInputOpen, Is.False);
		Assert.That(merged.Timeout, Is.EqualTo(TimeSpan.FromSeconds(1))); // other defaults still carry through
	}

	[Test]
	public void Handlers_PerCallReplacesDefault()
	{
		Action<string> d = _ => { };
		Action<string> p = _ => { };
		var defaults = new ProcessRunOptions {
			StandardOutputHandler = d,
		};
		var perCall = new ProcessRunOptions {
			StandardOutputHandler = p,
		};

		var merged = ProcessRunOptionsMerge.Merge(defaults, perCall);

		Assert.That(merged!.StandardOutputHandler, Is.SameAs(p));
	}

	[Test]
	public void Merge_DoesNotMutateInputs()
	{
		var defaults = new ProcessRunOptions {
			Timeout = TimeSpan.FromSeconds(1),
			Environment = new Dictionary<string, string?> {
				["A"] = "1",
			},
		};
		var perCall = new ProcessRunOptions {
			Timeout = TimeSpan.FromSeconds(2),
			Environment = new Dictionary<string, string?> {
				["B"] = "2",
			},
		};

		ProcessRunOptionsMerge.Merge(defaults, perCall);

		Assert.That(defaults.Timeout, Is.EqualTo(TimeSpan.FromSeconds(1)));
		Assert.That(defaults.Environment!.ContainsKey("B"), Is.False);
		Assert.That(perCall.Environment!.ContainsKey("A"), Is.False);
	}
}
