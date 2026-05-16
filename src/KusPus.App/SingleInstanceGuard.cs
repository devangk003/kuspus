namespace KusPus.App;

/// <summary>
/// Named-mutex single-instance check per TECH_SPEC §8.10 / §21. The second launch
/// broadcasts a registered Win32 message and exits; the running instance can
/// listen for the message via its MainWindow's WndProc to bring itself to front.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Local\KusPus";
    public const string BringToFrontMessage = "KusPus.BringMainToFront";

    private readonly Mutex _mutex;

    public bool IsOwner { get; }

    private SingleInstanceGuard(Mutex mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    public static SingleInstanceGuard AcquireOrSignal()
    {
        var mutex = new Mutex(initiallyOwned: true, name: MutexName, out bool created);
        return new SingleInstanceGuard(mutex, created);
    }

    public void Dispose()
    {
        if (IsOwner)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* already released */ }
        }
        _mutex.Dispose();
    }
}
