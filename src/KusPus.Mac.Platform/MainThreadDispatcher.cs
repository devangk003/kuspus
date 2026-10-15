using CoreFoundation;
using Foundation;
using KusPus.Core.Platform;

namespace KusPus.Mac.Platform;

/// <summary>AppKit <see cref="IUiDispatcher"/>: async dispatch onto the main GCD queue.</summary>
public sealed class MainThreadDispatcher : IUiDispatcher
{
    public void Post(Action action) => DispatchQueue.MainQueue.DispatchAsync(action);
}

/// <summary>
/// Runs AppKit-only calls (NSWorkspace, NSRunningApplication, …) on the main thread from
/// code that may be continuing on a thread-pool thread after <c>ConfigureAwait(false)</c>.
/// The binding enforces this with <c>AppKitThreadAccessException</c>; an unawaited one
/// surfaces only as "Unobserved task exception" and silently drops the paste.
/// </summary>
public static class MainThread
{
    public static Task<T> RunAsync<T>(Func<T> func)
    {
        if (NSThread.IsMain)
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public static Task RunAsync(Action action) => RunAsync(() =>
    {
        action();
        return true;
    });
}
