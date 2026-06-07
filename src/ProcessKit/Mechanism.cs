namespace ProcessKit;

/// <summary>
/// The kernel-level containment mechanism a <see cref="ProcessGroup"/> is using on the current
/// host. Observable via <see cref="ProcessGroup.Mechanism"/>; lets callers branch on capability
/// (e.g. resource limits require <see cref="JobObject"/> or <see cref="CgroupV2"/>).
/// </summary>
public enum Mechanism
{
	/// <summary>
	/// Windows Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. Kernel-enforced
	/// whole-tree containment.
	/// </summary>
	JobObject,

	/// <summary>
	/// POSIX process group, torn down via <c>killpg</c>. The primary mechanism on macOS and BSDs,
	/// and the Linux fallback when no cgroup is writable. Weaker than a Job Object / cgroup —
	/// a child that calls <c>setsid</c> escapes containment.
	/// </summary>
	ProcessGroup,

	/// <summary>
	/// Linux cgroup v2 (reserved — not yet selected; introduced in a later phase). Currently no
	/// <see cref="ProcessGroup"/> reports this value; included so consumers can write switch
	/// statements that will remain exhaustive when the cgroup backend lands.
	/// </summary>
	CgroupV2,

	/// <summary>
	/// No kernel containment available. Reserved for platforms with neither a Job Object nor a
	/// POSIX process group (e.g. WASI). Currently unreachable on the supported platforms.
	/// </summary>
	None,
}
