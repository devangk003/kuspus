using System.Diagnostics;

namespace KusPus.Native;

/// <summary>
/// Abstraction for "make sure this child process can't outlive us." Implemented by
/// <see cref="JobObjectContainer"/> via Windows Job Object with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. See TECH_SPEC §15.
/// </summary>
public interface IProcessContainer
{
    /// <summary>Assign the just-started process to the container.</summary>
    void Contain(Process process);
}
