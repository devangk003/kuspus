using System.Windows.Threading;
using KusPus.Core.Platform;

namespace KusPus.App;

/// <summary>WPF <see cref="IUiDispatcher"/>: <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/> at Normal priority.</summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action) => _dispatcher.BeginInvoke(action);
}
