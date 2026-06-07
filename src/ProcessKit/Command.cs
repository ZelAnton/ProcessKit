using System.Diagnostics;
using System.Text;

namespace ProcessKit;

/// <summary>
/// Fluent, immutable specification for a child process to run. Each builder method returns a new
/// <see cref="Command"/> (record semantics + <c>with</c>-expressions), and each terminal verb
/// (<see cref="StartAsync"/>, <see cref="OutputStringAsync"/>, <see cref="RunAsync"/>, …)
/// delegates to an <see cref="IProcessRunner"/> under the hood — no duplicated lifecycle logic.
/// </summary>
/// <remarks>
/// <para>
/// Pass an explicit <paramref name="runner"/> to the verbs for DI / testing scenarios; otherwise
/// <see cref="ProcessRunner.Default"/> is used. Any runner-level defaults (set via
/// <see cref="ProcessRunner(ProcessRunOptions)"/>) flow through transparently — the verbs build a
/// per-call <see cref="ProcessRunOptions"/> and the runner merges it with its own defaults.
/// </para>
/// <para>
/// Cancellation: a token configured via <see cref="WithCancellation"/> is "loud" — every verb
/// raises <see cref="ProcessCancelledException"/> on cancel (a subclass of
/// <see cref="OperationCanceledException"/>). Timeout (<see cref="WithTimeout"/>) is the soft
/// counterpart — captured in <see cref="ProcessResult{T}.WasTimedOut"/> on non-throwing paths.
/// </para>
/// </remarks>
public sealed record Command
{
	/// <summary>Executable to run. Required (set via <see cref="Create"/>).</summary>
	public string Program { get; init; } = string.Empty;

	public IReadOnlyList<string> Arguments { get; init; } = [];

	public string? WorkingDirectory { get; init; }

	/// <summary>Variables applied over the cloned parent environment (per-call key wins).
	/// A <c>null</c> value removes the variable.</summary>
	public IReadOnlyDictionary<string, string?>? Environment { get; init; }

	/// <summary>When non-null, the parent environment is cleared at spawn time and only the listed
	/// variables are copied across from the current process's environment. When <c>null</c> the
	/// child inherits everything (default <see cref="ProcessStartInfo"/> behaviour). Matches Rust
	/// <c>Command::inherit_env</c>.</summary>
	public IReadOnlyList<string>? InheritEnvironmentVariables { get; init; }

	public TimeSpan? Timeout { get; init; }

	/// <summary>Cancellation token threaded into every verb. When the token fires the child is
	/// killed and the verb throws <see cref="ProcessCancelledException"/>.</summary>
	public CancellationToken Cancellation { get; init; }

	public StandardInput? StandardInput { get; init; }

	public Action<string>? StandardOutputHandler { get; init; }

	public Action<string>? StandardErrorHandler { get; init; }

	public OutputBufferPolicy? OutputBuffer { get; init; }

	/// <summary>Optional shared <see cref="ProcessGroup"/> — when set, the runner does NOT create a
	/// private group; the caller retains ownership.</summary>
	public ProcessGroup? ProcessGroup { get; init; }

	/// <summary>Shutdown options for the runner's private group (ignored when
	/// <see cref="ProcessGroup"/> is supplied — that group's own options govern teardown).</summary>
	public ProcessGroupOptions? ProcessGroupOptions { get; init; }

	public Encoding? StdOutEncoding { get; init; }

	public Encoding? StdErrEncoding { get; init; }

	public bool CreateNoWindow { get; init; }

	public bool KeepStandardInputOpen { get; init; }

	/// <summary>Starts a new command targeting <paramref name="program"/>.</summary>
	public static Command Create(string program)
	{
		ArgumentException.ThrowIfNullOrEmpty(program);
		return new Command { Program = program };
	}

	public Command Args(params string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);
		if (args.Length == 0)
			return this;
		return this with { Arguments = [.. Arguments, .. args] };
	}

	public Command WithWorkingDirectory(string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		return this with { WorkingDirectory = path };
	}

	/// <summary>Sets or removes the named environment variable. Passing <c>null</c> removes it.</summary>
	public Command WithEnvironment(string key, string? value)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		var next = Environment is null
			? new Dictionary<string, string?>(StringComparer.Ordinal)
			: new Dictionary<string, string?>(Environment, StringComparer.Ordinal);
		next[key] = value;
		return this with { Environment = next };
	}

	/// <summary>Clears the child's inherited environment and re-populates only the named variables
	/// from the current process at spawn time. Matches Rust <c>Command::inherit_env</c>. Requires
	/// at least one variable name — to wipe the environment entirely, set every key explicitly via
	/// <see cref="WithEnvironment"/> instead.</summary>
	public Command InheritEnvironment(params string[] varNames)
	{
		ArgumentNullException.ThrowIfNull(varNames);
		if (varNames.Length == 0)
			throw new ArgumentException(
				"InheritEnvironment requires at least one variable name; calling it with no names would silently clear the child's environment (no SystemRoot/PATH/etc), which is almost never intended.",
				nameof(varNames));
		return this with { InheritEnvironmentVariables = [.. varNames] };
	}

	public Command WithTimeout(TimeSpan timeout)
	{
		// Match BCL conventions (Task.Delay, CancellationTokenSource): negative/zero timeouts are
		// invalid. A zero timeout would mean "kill the process before it produces output", which is
		// never what a caller of WithTimeout intended.
		if (timeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
		return this with { Timeout = timeout };
	}

	public Command WithCancellation(CancellationToken token) => this with { Cancellation = token };

	/// <summary>Routes the run through a caller-owned shared <see cref="ProcessKit.ProcessGroup"/>.
	/// The runner will not create a private group; the caller retains lifetime ownership.</summary>
	public Command WithProcessGroup(ProcessGroup group)
	{
		ArgumentNullException.ThrowIfNull(group);
		return this with { ProcessGroup = group };
	}

	/// <summary>Sets shutdown options for the runner's <em>private</em> group (only applied when
	/// <see cref="WithProcessGroup"/> was NOT used).</summary>
	public Command WithProcessGroupOptions(ProcessGroupOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		return this with { ProcessGroupOptions = options };
	}

	public Command WithStandardInput(StandardInput input)
	{
		ArgumentNullException.ThrowIfNull(input);
		return this with { StandardInput = input };
	}

	public Command OnStandardOutputLine(Action<string> handler)
	{
		ArgumentNullException.ThrowIfNull(handler);
		return this with { StandardOutputHandler = handler };
	}

	public Command OnStandardErrorLine(Action<string> handler)
	{
		ArgumentNullException.ThrowIfNull(handler);
		return this with { StandardErrorHandler = handler };
	}

	public Command WithOutputBuffer(OutputBufferPolicy policy)
	{
		ArgumentNullException.ThrowIfNull(policy);
		return this with { OutputBuffer = policy };
	}

	/// <summary>Applies the same encoding to both stdout and stderr.</summary>
	public Command WithEncoding(Encoding both)
	{
		ArgumentNullException.ThrowIfNull(both);
		return this with { StdOutEncoding = both, StdErrEncoding = both };
	}

	public Command WithStdOutEncoding(Encoding encoding)
	{
		ArgumentNullException.ThrowIfNull(encoding);
		return this with { StdOutEncoding = encoding };
	}

	public Command WithStdErrEncoding(Encoding encoding)
	{
		ArgumentNullException.ThrowIfNull(encoding);
		return this with { StdErrEncoding = encoding };
	}

	public Command WithCreateNoWindow() => this with { CreateNoWindow = true };

	public Command WithKeepStandardInputOpen() => this with { KeepStandardInputOpen = true };

	// --- Terminal verbs ---------------------------------------------------------------------

	/// <summary>Spawns the process and returns a live handle. Caller is responsible for awaiting
	/// completion and disposing.</summary>
	public Task<IRunningProcess> StartAsync(IProcessRunner? runner = null)
	{
		var handle = (runner ?? ProcessRunner.Default).Start(BuildStartInfo(), BuildOptions(), Cancellation);
		return Task.FromResult(handle);
	}

	/// <summary>Captures stdout (decoded text), stderr, and the exit code as a
	/// <see cref="ProcessResult{T}"/>. A configured timeout is captured in
	/// <see cref="ProcessResult{T}.WasTimedOut"/>; a configured cancellation throws
	/// <see cref="ProcessCancelledException"/>.</summary>
	public async Task<ProcessResult<string>> OutputStringAsync(IProcessRunner? runner = null)
	{
		try
		{
			return await (runner ?? ProcessRunner.Default)
				.GetFullOutputAsync(BuildStartInfo(), BuildOptions(), Cancellation)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
		{
			throw new ProcessCancelledException(Program, Cancellation);
		}
	}

	/// <summary>Captures stdout as raw bytes (untouched by encoding) plus stderr text and exit code.</summary>
	public async Task<ProcessResult<byte[]>> OutputBytesAsync(IProcessRunner? runner = null)
	{
		try
		{
			return await (runner ?? ProcessRunner.Default)
				.GetBytesOutputAsync(BuildStartInfo(), BuildOptions(), Cancellation)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
		{
			throw new ProcessCancelledException(Program, Cancellation);
		}
	}

	/// <summary>Returns the raw exit code. Output is discarded (memory-flat).</summary>
	public async Task<int> ExitCodeAsync(IProcessRunner? runner = null)
	{
		try
		{
			return await (runner ?? ProcessRunner.Default)
				.GetExitCodeAsync(BuildStartInfo(), BuildOptions(), Cancellation)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
		{
			throw new ProcessCancelledException(Program, Cancellation);
		}
	}

	/// <summary>Treats the process as a boolean check: exit 0 → <c>true</c>, exit 1 → <c>false</c>,
	/// any other code throws <see cref="ProcessExitException"/>. Matches Rust
	/// <c>Command::probe</c>; designed for tools like <c>git diff --quiet</c> where unknown codes
	/// signal a genuine error. A timeout-kill is also treated as "genuine error" — the captured
	/// exit code after a timeout kill is unreliable (often coincidentally 1 on Unix), so probing a
	/// killed child would silently report a false "false".</summary>
	public async Task<bool> ProbeAsync(IProcessRunner? runner = null)
	{
		var result = await OutputStringAsync(runner).ConfigureAwait(false);
		if (result.WasTimedOut)
			throw new ProcessExitException(
				result.ExitCode,
				result.StdErr,
				$"`{Program}` probe was killed by its configured timeout; the exit code is unreliable in that case and probe semantics do not apply.");
		return result.ExitCode switch
		{
			0 => true,
			1 => false,
			_ => throw new ProcessExitException(
				result.ExitCode,
				result.StdErr,
				$"`{Program}` probe expected exit 0 or 1, got {result.ExitCode}: {Truncate(result.StdErr, 4096)}"),
		};
	}

	/// <summary>Requires the process to exit with code 0; returns stdout with its trailing newline
	/// stripped (Rust <c>Command::run</c> semantics — leading/interior whitespace is preserved so
	/// multi-line output round-trips faithfully).</summary>
	public async Task<string> RunAsync(IProcessRunner? runner = null)
	{
		var result = await OutputStringAsync(runner).ConfigureAwait(false);
		result.EnsureSuccess();
		return result.StdOut.TrimEnd('\r', '\n');
	}

	/// <summary>Returns the first stdout line that satisfies <paramref name="match"/> (or the very
	/// first line when <paramref name="match"/> is <c>null</c>), or <c>null</c> if the process
	/// exits without producing such a line. Streams stdout — early-match terminates the process.</summary>
	public async Task<string?> FirstLineAsync(Predicate<string>? match = null, IProcessRunner? runner = null)
	{
		// GetFirstLineOutputAsync expects Func<string,bool>? — adapt the more-conventional
		// Predicate<string>? users pass here.
		Func<string, bool>? predicate = match is null ? null : new Func<string, bool>(match);
		try
		{
			return await (runner ?? ProcessRunner.Default)
				.GetFirstLineOutputAsync(BuildStartInfo(), predicate, BuildOptions(), Cancellation)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
		{
			throw new ProcessCancelledException(Program, Cancellation);
		}
	}

	// --- Builders ---------------------------------------------------------------------------

	ProcessStartInfo BuildStartInfo()
	{
		if (string.IsNullOrEmpty(Program))
			throw new InvalidOperationException("Command.Program is not set; build the command via Command.Create(...).");

		var psi = new ProcessStartInfo(Program);
		foreach (var arg in Arguments)
			psi.ArgumentList.Add(arg);
		if (WorkingDirectory is not null)
			psi.WorkingDirectory = WorkingDirectory;
		psi.CreateNoWindow = CreateNoWindow;

		if (InheritEnvironmentVariables is not null)
		{
			// Allow-list semantics: clear the inherited env first, then re-populate only the named
			// variables from the current process at SPAWN time (matches Rust inherit_env).
			psi.Environment.Clear();
			foreach (var name in InheritEnvironmentVariables)
			{
				var value = System.Environment.GetEnvironmentVariable(name);
				if (value is not null)
					psi.Environment[name] = value;
			}
		}

		// Options.Environment is applied on top of psi.Environment during PrepareStartInfo's PSI
		// clone, so we don't need to apply it here — let the runner do its uniform merge.

		return psi;
	}

	ProcessRunOptions BuildOptions() => new()
	{
		StandardInput = StandardInput,
		StandardOutputHandler = StandardOutputHandler,
		StandardErrorHandler = StandardErrorHandler,
		Timeout = Timeout,
		StdOutEncoding = StdOutEncoding,
		StdErrEncoding = StdErrEncoding,
		OutputBuffer = OutputBuffer,
		WorkingDirectory = WorkingDirectory,
		Environment = Environment,
		KeepStandardInputOpen = KeepStandardInputOpen,
		ProcessGroup = ProcessGroup,
		ProcessGroupOptions = ProcessGroupOptions,
	};

	static string Truncate(string s, int max) =>
		string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");
}
