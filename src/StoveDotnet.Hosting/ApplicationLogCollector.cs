using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace StoveDotnet.Hosting;

/// <summary>Bounded application-local logs correlated by Activity trace id, for Stove failure output.</summary>
public sealed class ApplicationLogCollector : ILoggerProvider, IFailureDetailsProvider
{
    private readonly ConcurrentQueue<Entry> _entries = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    public Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken)
    {
        var lines = _entries.Where(e => e.TraceId == test.TraceId).Select(e => e.Message).ToArray();
        return Task.FromResult(lines.Length == 0 ? null : new FailureDetails("application logs", string.Join(Environment.NewLine, lines)));
    }

    private sealed record Entry(string TraceId, string Message);
    private sealed class Logger(ApplicationLogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || Activity.Current is not { } activity) return;
            owner._entries.Enqueue(new Entry(activity.TraceId.ToString(), $"[{logLevel}] {category}: {formatter(state, exception)} {exception}"));
            while (owner._entries.Count > 1000) owner._entries.TryDequeue(out _);
        }
    }
}
