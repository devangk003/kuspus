#pragma warning disable CA1848
#pragma warning disable CA1873

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KusPus.Native.PInvoke;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Native;

/// <summary>
/// Windows Job Object containment for subprocesses (specifically whisper.exe).
/// See TECH_SPEC §15. <see cref="JOBOBJECT_EXTENDED_LIMIT_INFORMATION"/> is configured
/// with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when KusPus dies for any reason
/// (kill, crash, OS shutdown), Windows closes our job handle and kills every process
/// inside.
///
/// Lifetime: one per app process (Phase 6 wires it up in the DI composition root and
/// disposes on app exit). Cheap — ~50 bytes of kernel state plus one P/Invoke per
/// <see cref="Contain"/> call.
/// </summary>
public sealed class JobObjectContainer : IProcessContainer, IDisposable
{
    private readonly ILogger<JobObjectContainer> _logger;
    private IntPtr _job;

    public JobObjectContainer(ILogger<JobObjectContainer>? logger = null)
    {
        _logger = logger ?? NullLogger<JobObjectContainer>.Instance;

        _job = Kernel32.CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObjectW failed.");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeConstants.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        bool ok = Kernel32.SetInformationJobObject(
            _job,
            NativeConstants.JobObjectExtendedLimitInformation,
            in info,
            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            Kernel32.CloseHandle(_job);
            _job = IntPtr.Zero;
            throw new Win32Exception(err, "SetInformationJobObject failed.");
        }

        _logger.LogInformation("Job Object created with KILL_ON_JOB_CLOSE.");
    }

    public void Contain(Process process)
    {
        ObjectDisposedException.ThrowIf(_job == IntPtr.Zero, this);

        if (!Kernel32.AssignProcessToJobObject(_job, process.Handle))
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"AssignProcessToJobObject failed for PID {process.Id}.");
        }
    }

    public void Dispose()
    {
        if (_job != IntPtr.Zero)
        {
            Kernel32.CloseHandle(_job);
            _job = IntPtr.Zero;
            _logger.LogInformation("Job Object closed; contained processes terminated.");
        }
    }
}
