using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace KusPus.Mac.App;

/// <summary>
/// The rolling file sink behind a swappable inner logger. ILogger instances created from
/// DI capture the Serilog pipeline once, so "Clear logs" can't just replace Log.Logger;
/// instead it closes the current file here, deletes the files, and reopens a fresh one
/// (otherwise macOS keeps appending to the unlinked file until the next roll).
/// </summary>
internal sealed class RestartableFileSink : ILogEventSink, IDisposable
{
    private readonly object _lock = new();
    private readonly Func<Logger> _factory;
    private Logger _inner;

    public RestartableFileSink(Func<Logger> factory)
    {
        _factory = factory;
        _inner = factory();
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_lock)
        {
            _inner.Write(logEvent);
        }
    }

    /// <summary>Closes the current file, runs <paramref name="whileClosed"/>, then starts a new file.</summary>
    public void Restart(Action whileClosed)
    {
        lock (_lock)
        {
            _inner.Dispose();
            try
            {
                whileClosed();
            }
            finally
            {
                _inner = _factory();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _inner.Dispose();
        }
    }
}
