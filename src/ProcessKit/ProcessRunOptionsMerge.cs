namespace ProcessKit;

/// <summary>
/// Merges a runner's baseline <see cref="ProcessRunOptions"/> with per-call options. Per-call values
/// win field-by-field; the environment is unioned (per-call key wins). Neither input is mutated.
/// </summary>
static class ProcessRunOptionsMerge
{
	internal static ProcessRunOptions? Merge(ProcessRunOptions? defaults, ProcessRunOptions? perCall)
	{
		// Fast paths: nothing to merge — return the live instance (no allocation, no mutation).
		if (defaults is null)
			return perCall;
		if (perCall is null)
			// KeepStandardInputOpen is a per-call decision and must never be inherited from defaults
			// (otherwise a runner default would silently re-enable interactive stdin — including
			// through the bulk helpers, which would hang a stdin-reading child).
			return defaults.KeepStandardInputOpen
				? defaults with { KeepStandardInputOpen = false }
				: defaults;

		return defaults with
		{
			// Per-call non-null replaces the default; otherwise the default carries through.
			StandardInput = perCall.StandardInput ?? defaults.StandardInput,
			StandardOutputHandler = perCall.StandardOutputHandler ?? defaults.StandardOutputHandler,
			StandardErrorHandler = perCall.StandardErrorHandler ?? defaults.StandardErrorHandler,
			ProcessGroup = perCall.ProcessGroup ?? defaults.ProcessGroup,
			Timeout = perCall.Timeout ?? defaults.Timeout,
			StdOutEncoding = perCall.StdOutEncoding ?? defaults.StdOutEncoding,
			StdErrEncoding = perCall.StdErrEncoding ?? defaults.StdErrEncoding,
			OutputBuffer = perCall.OutputBuffer ?? defaults.OutputBuffer,
			WorkingDirectory = perCall.WorkingDirectory ?? defaults.WorkingDirectory,
			PumpTeardownTimeout = perCall.PumpTeardownTimeout ?? defaults.PumpTeardownTimeout,
			ProcessGroupOptions = perCall.ProcessGroupOptions ?? defaults.ProcessGroupOptions,

			// Environment is the one structural merge: defaults provide a base, per-call adds/overrides.
			Environment = MergeEnvironment(defaults.Environment, perCall.Environment),

			// Interactive stdin is inherently a per-invocation decision (a bool has no "unset"), so it
			// is never inherited from defaults.
			KeepStandardInputOpen = perCall.KeepStandardInputOpen,
		};
	}

	static IReadOnlyDictionary<string, string?>? MergeEnvironment(
		IReadOnlyDictionary<string, string?>? defaults,
		IReadOnlyDictionary<string, string?>? perCall)
	{
		if (defaults is null)
			return perCall;
		if (perCall is null)
			return defaults;

		// Ordinal keeps the merge deterministic and platform-independent; the OS applies its own
		// case folding at process start. A per-call null value still means "remove the variable".
		var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
		foreach (var (key, value) in defaults)
			merged[key] = value;
		foreach (var (key, value) in perCall)
			merged[key] = value;
		return merged;
	}
}
