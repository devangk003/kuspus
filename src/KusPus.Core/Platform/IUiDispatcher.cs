namespace KusPus.Core.Platform;

/// <summary>
/// Minimal UI-thread marshalling seam so <c>AppCoordinator</c> can live in a
/// platform-neutral assembly. WPF wraps <c>Dispatcher.BeginInvoke</c>; AppKit wraps
/// <c>NSRunLoop.Main.BeginInvokeOnMainThread</c>; tests run the action inline.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queue <paramref name="action"/> to run on the UI thread and return immediately.</summary>
    void Post(Action action);
}
