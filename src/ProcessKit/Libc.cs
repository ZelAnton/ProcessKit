using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ProcessKit;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("freebsd")]
static partial class Libc
{
	internal const int SIGTERM = 15;
	internal const int EACCES = 13;
	internal const int EPERM = 1;
	internal const int ESRCH = 3;

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int setpgid(int pid, int pgid);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int kill(int pid, int sig);
}
