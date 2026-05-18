using System.Diagnostics;

namespace ProcessKit;

interface IProcessGroupImpl : IDisposable, IAsyncDisposable
{
	Process StartAndAdd(ProcessStartInfo startInfo);
	void Add(Process process);
	void TerminateAll();
	ProcessGroupStats GetStats();
}
