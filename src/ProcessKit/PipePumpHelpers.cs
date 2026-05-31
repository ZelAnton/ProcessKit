using System.Diagnostics;
using System.Text;

namespace ProcessKit;

static class PipePumpHelpers
{
	internal static ProcessStartInfo PrepareStartInfo(ProcessStartInfo source, ProcessRunOptions? options)
	{
		ArgumentNullException.ThrowIfNull(source);

		var psi = new ProcessStartInfo
		{
			FileName = source.FileName,
			WorkingDirectory = source.WorkingDirectory,
			CreateNoWindow = source.CreateNoWindow,
			WindowStyle = source.WindowStyle,
			Verb = source.Verb,
			UserName = source.UserName,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			// Always redirect stdin so the runner controls it. When no input is supplied the runner
			// closes it immediately (see WriteStandardInputAsync), giving the child an empty stdin
			// that EOFs at once — the documented "stdin closed at start" contract. Without this the
			// child would inherit the parent's stdin and a stdin-reading process could hang forever.
			RedirectStandardInput = true,
			StandardOutputEncoding = options?.StdOutEncoding ?? source.StandardOutputEncoding ?? Encoding.UTF8,
			StandardErrorEncoding = options?.StdErrEncoding ?? source.StandardErrorEncoding ?? Encoding.UTF8,
		};

		if (OperatingSystem.IsWindows())
		{
			psi.Domain = source.Domain;
			psi.PasswordInClearText = source.PasswordInClearText;
			psi.LoadUserProfile = source.LoadUserProfile;
		}

		foreach (var arg in source.ArgumentList)
			psi.ArgumentList.Add(arg);

		// A fresh ProcessStartInfo.Environment is pre-seeded with the current process environment.
		// Clear it first so the clone mirrors the caller's view exactly — otherwise a variable the
		// caller *removed* from source.Environment would silently reappear in the started process.
		psi.Environment.Clear();
		foreach (var entry in source.Environment)
			psi.Environment[entry.Key] = entry.Value;

		// Options override the PSI (same precedence as the encoding overrides above): WorkingDirectory
		// replaces, Environment entries are applied over the clone (a null value removes the variable).
		// Applied here so they take effect uniformly — full-PSI and convenience overloads — and so
		// runner-level defaults flow through.
		if (options?.WorkingDirectory is { } workingDirectory)
			psi.WorkingDirectory = workingDirectory;

		if (options?.Environment is { } environment)
		{
			foreach (var (key, value) in environment)
			{
				if (value is null)
					psi.Environment.Remove(key);
				else
					psi.Environment[key] = value;
			}
		}

		return psi;
	}

	internal static async Task PumpLinesAsync(
		TextReader reader,
		ILineBuffer buffer,
		Action<string>? handler,
		Action incrementCounter,
		CancellationToken cancellationToken)
	{
		try
		{
			while (true)
			{
				string? line;
				try
				{
					line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (IOException)
				{
					// Pipe closed by the OS (typically when the process is killed). Treat as EOF.
					break;
				}
				catch (ObjectDisposedException)
				{
					// The CancellationTokenSource was disposed while we were awaiting (e.g. dispose
					// raced ahead because we exceeded the teardown timeout). Treat as EOF — we are
					// being torn down anyway.
					break;
				}

				if (line is null)
					break;

				// Increment BEFORE buffering so the line counter reflects every line read off the
				// pipe even when a bounded buffer drops it — lets a caller detect loss (count >
				// lines received).
				incrementCounter();
				try
				{
					handler?.Invoke(line);
				}
				catch
				{
					 // ignored - user handler must not break the pump
				}

				// Never blocks: unbounded always accepts; bounded silently drops per its policy. The
				// pump must keep draining the OS pipe regardless, or the child could deadlock.
				buffer.Write(line);
			}
		}
		finally
		{
			buffer.Complete();
		}
	}

	internal static async Task WriteStandardInputAsync(
		IProcessHandle handle,
		StandardInput? input,
		CancellationToken cancellationToken)
	{
		var stdin = handle.StandardInput.BaseStream;
		try
		{
			// Polymorphic dispatch — each StandardInput subtype knows how to pump itself.
			// null and StandardInput.Empty both write nothing (the base no-op), then the finally
			// closes stdin so the child sees EOF immediately. stdin is always redirected (see
			// PrepareStartInfo).
			await (input ?? StandardInput.Empty).WriteToAsync(stdin, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// cancellation — process was killed before stdin drained; nothing recoverable here.
		}
		catch (IOException)
		{
			// pipe closed — process exited before consuming the rest of stdin; expected.
		}
		catch (ObjectDisposedException)
		{
			// CTS disposed mid-write while the runner is being torn down; safe to ignore.
		}
		catch (Exception)
		{
			// A user-supplied stdin source (FromStream / FromLines / FromEnumerable) threw an
			// unexpected exception. Feeding stdin is best-effort: a faulty source must not fault this
			// task and let the exception escape handle teardown (DisposeAsync awaits the stdin task).
			// The child receives whatever was written before the failure, then EOF when stdin closes.
		}
		finally
		{
			try
			{
				handle.StandardInput.Close();
			}
			catch
			{
				// ignored - if we fail to close the input, it's likely because the process has already exited and the OS has cleaned up the pipe, so there's no need to log or surface this.
			}
		}
	}

	internal static async Task ObserveAsync(Task task)
	{
		try
		{
			await task.ConfigureAwait(false);
		}
		catch (SystemException e) when (e
			is OperationCanceledException
			or IOException
			or ObjectDisposedException)
		{
			// ignored - expected exceptions from pump teardown when the process exits or is killed, or when cancellation is requested; no need to log or surface them.
		}
	}
}
