# ProcessKit

Cross-platform child process lifetime management for .NET.

ProcessKit ensures that child processes are terminated
when the parent exits — whether gracefully or via crash.

On Windows this is backed by kernel Job Objects;
on Unix systems (Linux, macOS, FreeBSD) it uses POSIX process groups.

## Requirements

- .NET 10.0 or later
- Windows 8+ / Linux / macOS / FreeBSD
- AOT-compatible

## Installation

Available on [NuGet.org](https://www.nuget.org/packages/ProcessKit).

```sh
dotnet add package ProcessKit
```

## Verifying the package

Every release is signed with the `AntonZhelezniakou-CodeSigning` certificate.
The public certificate is committed at
[certificates/AntonZhelezniakou-CodeSigning.cer](certificates/AntonZhelezniakou-CodeSigning.cer).

### Verify the NuGet signature

```pwsh
# Compute the SHA-256 fingerprint of the public certificate
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
    "certificates/AntonZhelezniakou-CodeSigning.cer"
$fp = $cert.GetCertHashString("SHA256")

# Verify the package against that fingerprint
dotnet nuget verify ProcessKit.<version>.nupkg --certificate-fingerprint $fp
```

Expected: `Successfully verified package 'ProcessKit.<version>'.`

### Verify the SHA256 checksums

Each GitHub Release ships a `SHA256SUMS` file alongside the `.nupkg` / `.snupkg`.
Download all three into the same directory, then:

```sh
sha256sum -c SHA256SUMS
```

Expected:

```
ProcessKit.<version>.nupkg: OK
ProcessKit.<version>.snupkg: OK
```

## Usage

```csharp
using System.Diagnostics;
using ProcessKit;

// Children are terminated when the group is disposed —
// even if the parent process crashes.
using var group = new ProcessGroup();

var psi = new ProcessStartInfo("myworker", ["--arg"]) { UseShellExecute = false };
var worker = group.Start(psi);

// Kill on cancellation
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var transient = group.Start(psi, cts.Token);

// Add an externally-started process to the group
var external = Process.Start(psi);
group.Add(external!);

// Runtime statistics (CPU time, peak memory, active count)
var stats = group.GetStats();
Console.WriteLine($"active={stats.ActiveProcessCount} cpu={stats.TotalCpuTime} peak={stats.PeakMemoryBytes}");

// Async dispose — non-blocking on Unix where SIGTERM/wait is involved
await using var asyncGroup = new ProcessGroup();
// ...
```

## Running tests on Linux from Windows

The Unix code path (`UnixProcessGroup`, `Libc`) is exercised in CI on
`ubuntu-latest`, but you can also run the suite locally against a Linux
container — useful when changing native interop or shutdown semantics.

Requirements:

- [Rancher Desktop](https://rancherdesktop.io/) (or Docker Desktop) with the
  `dockerd` / moby engine enabled so `docker` is on `PATH`
- PowerShell 7+

```pwsh
pwsh ./scripts/test-linux.ps1
```

The script mounts the repo into `mcr.microsoft.com/dotnet/sdk:10.0` and runs
`dotnet build` + `dotnet test`. The host's `bin/` and `obj/` folders are
shadowed inside the container with anonymous volumes, so the Linux build
neither sees the Windows IDE artifacts nor writes back into the host tree.
A named volume (`processkit-nuget`) caches NuGet packages between runs.

Useful switches:

```pwsh
pwsh ./scripts/test-linux.ps1 -Filter "FullyQualifiedName~TerminateAll"
pwsh ./scripts/test-linux.ps1 -Configuration Debug -Rebuild
```

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the version history.

## License

This project is licensed under the [MIT License](LICENSE).
