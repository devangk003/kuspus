using Microsoft.Extensions.Logging;

namespace KusPus.Persistence.Tests;

/// <summary>
/// Minimal in-memory <see cref="ILogger{T}"/> for verifying that
/// PrefsStore / HistoryStore emit the warnings the spec mandates.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }

    public bool HasWarningContaining(string substring) =>
        Entries.Any(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains(substring, StringComparison.Ordinal));
}
