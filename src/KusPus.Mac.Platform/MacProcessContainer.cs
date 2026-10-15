#pragma warning disable CA1848
#pragma warning disable CA1873

using System.Diagnostics;
using System.Runtime.InteropServices;
using KusPus.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Mac.Platform;

/// <summary>
/// Best-effort <see cref="IProcessContainer"/> for macOS. There is no Job Object
/// equivalent, so we track every contained child and kill it when the container is
/// disposed, when the runtime exits normally, or on SIGTERM/SIGINT. A hard
/// <c>kill -9</c> of KusPus can still orphan a whisper run; the 5-minute timeout
/// in <c>WhisperRunner</c> bounds the damage.
/// </summary>
public sealed class MacProcessContainer : IProcessContainer, IDisposable
{
    private readonly ILogger<MacProcessContainer> _logger;
    private readonly HashSet<Process> _children = new();
    private readonly object _lock = new();
    private readonly PosixSignalRegistration _sigterm;
    private readonly PosixSignalRegistration _sigint;
    private bool _disposed;

    public MacProcessContainer(ILogger<MacProcessContainer>? logger = null)
    {
        _logger = logger ?? NullLogger<MacProcessContainer>.Instance;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => KillAll());
        _sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => KillAll());
        _logger.LogInformation("Process container ready (kill-on-exit via ProcessExit/SIGTERM).");
    }

    public void Contain(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            _children.Add(process);
        }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            lock (_lock)
            {
                _children.Remove(process);
            }
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _sigterm.Dispose();
        _sigint.Dispose();
        KillAll();
    }

    private void OnProcessExit(object? sender, EventArgs e) => KillAll();

    private void KillAll()
    {
        Process[] snapshot;
        lock (_lock)
        {
            snapshot = _children.ToArray();
            _children.Clear();
        }
        foreach (var p in snapshot)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    _logger.LogInformation("Killed contained child PID {Pid}.", p.Id);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone or not ours to kill.
            }
        }
    }
}
