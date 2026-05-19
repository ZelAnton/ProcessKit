using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace ProcessKit;

static class PipePumpHelpers
{
	internal static ProcessStartInfo PrepareStartInfo(ProcessStartInfo source, ProcessRunOptions? options)
	{
		ArgumentNullException.ThrowIfNull(source);

		var needsStdin = options?.StandardInput is not null and not StandardInput.EmptyInput;

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
			RedirectStandardInput = needsStdin,
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

		foreach (var key in source.Environment.Keys)
			psi.Environment[key] = source.Environment[key];

		return psi;
	}

	internal static async Task PumpLinesAsync(
		TextReader reader,
		ChannelWriter<string> writer,
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

				incrementCounter();
				try
				{
					handler?.Invoke(line);
				}
				catch
				{
					 // ignored - user handler must not break the pump
				}

				if (!writer.TryWrite(line))
				{
					// Channel is unbounded so TryWrite should always succeed; this only happens
					// if the channel was completed early (e.g. dispose race) — drop quietly.
					break;
				}
			}
		}
		finally
		{
			writer.TryComplete();
		}
	}

	internal static async Task ReadIntoBufferAsync(
		TextReader reader,
		StringBuilder buffer,
		Action<string>? handler,
		CancellationToken cancellationToken)
	{
		while (true)
		{
			string? line;
			try
			{
				line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (SystemException e) when (e
				is OperationCanceledException
				or IOException
				or ObjectDisposedException)
			{
				break;
			}


			if (line is null)
				break;

			try
			{
				handler?.Invoke(line);
			}
			catch
			{
				// ignored - user handler must not break the pump; we still want to capture the output in the buffer even if the handler fails.
			}

			if (buffer.Length > 0)
				buffer.AppendLine();
			buffer.Append(line);
		}
	}

	internal static async Task WriteStandardInputAsync(
		Process process,
		StandardInput? input,
		CancellationToken cancellationToken)
	{
		if (input is null or StandardInput.EmptyInput)
			return;

		var stdin = process.StandardInput.BaseStream;
		try
		{
			switch (input)
			{
				case StandardInput.StringInput s:
				{
					var bytes = s.Encoding.GetBytes(s.Text);
					await stdin.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
					break;
				}
				case StandardInput.BytesInput b:
				{
					await stdin.WriteAsync(b.Bytes, cancellationToken).ConfigureAwait(false);
					break;
				}
				case StandardInput.StreamInput streamInput:
				{
					try
					{
						await streamInput.Stream.CopyToAsync(stdin, cancellationToken).ConfigureAwait(false);
					}
					finally
					{
						if (!streamInput.LeaveOpen)
							await streamInput.Stream.DisposeAsync().ConfigureAwait(false);
					}
					break;
				}
				case StandardInput.LinesInput lines:
				{
					var newline = lines.Encoding.GetBytes(Environment.NewLine);
					await foreach (var line in lines.Lines.WithCancellation(cancellationToken).ConfigureAwait(false))
					{
						var bytes = lines.Encoding.GetBytes(line);
						await stdin.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
						await stdin.WriteAsync(newline, cancellationToken).ConfigureAwait(false);
					}
					break;
				}
				case StandardInput.EnumerableInput enumerable:
				{
					var newline = enumerable.Encoding.GetBytes(Environment.NewLine);
					foreach (var line in enumerable.Lines)
					{
						cancellationToken.ThrowIfCancellationRequested();
						var bytes = enumerable.Encoding.GetBytes(line);
						await stdin.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
						await stdin.WriteAsync(newline, cancellationToken).ConfigureAwait(false);
					}
					break;
				}
				case StandardInput.FileInput file:
				{
					await using var fs = new FileStream(
						file.Path,
						FileMode.Open,
						FileAccess.Read,
						FileShare.Read,
						bufferSize: 4096,
						useAsync: true);
					await fs.CopyToAsync(stdin, cancellationToken).ConfigureAwait(false);
					break;
				}
			}
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
		finally
		{
			try
			{
				process.StandardInput.Close();
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
