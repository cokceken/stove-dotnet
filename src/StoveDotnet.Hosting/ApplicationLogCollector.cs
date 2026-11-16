using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace StoveDotnet.Hosting;

/// <summary>
/// Bounded in-process application logs for Stove failure output. Logs written inside a test's trace are attributed to
/// that test. Warnings and errors written outside any trace (startup, background services) cannot be attributed; they
/// are reported separately for the time the failed test was running.
/// </summary>
public sealed class ApplicationLogCollector : ILoggerProvider, IFailureDetailsProvider
{
    private const int Capacity = 2000;
    private const int MaxCorrelatedLines = 200;
    private const int MaxUncorrelatedLines = 20;
    private readonly ConcurrentQueue<Entry> _entries = new();
    private int _count;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);
        var entries = _entries.ToArray();
        var correlated = entries.Where(e => e.TraceId == test.TraceId).ToArray();
        var uncorrelated = entries.Where(e => e.TraceId is null && e.Timestamp >= test.StartedAt).ToArray();
        if (correlated.Length == 0 && uncorrelated.Length == 0) return Task.FromResult<FailureDetails?>(null);

        var content = new StringBuilder();
        Append(content, correlated, MaxCorrelatedLines);
        if (uncorrelated.Length > 0)
        {
            if (content.Length > 0) content.AppendLine();
            content.AppendLine("Warnings and errors outside any trace while this test ran (not test-correlated):");
            Append(content, uncorrelated, MaxUncorrelatedLines);
        }

        return Task.FromResult<FailureDetails?>(new FailureDetails("application logs", content.ToString().TrimEnd()));
    }

    /// <summary>
    /// For application adapters: attaches the warnings and errors logged so far to a start failure, so the startup
    /// error reported by Stove shows what the application logged before it gave up.
    /// </summary>
    public void AttachStartupLogs(Exception startFailure)
    {
        ArgumentNullException.ThrowIfNull(startFailure);
        var entries = _entries.Where(e => e.TraceId is null).ToArray();
        if (entries.Length == 0 || startFailure.Data.IsReadOnly) return;
        var content = new StringBuilder().AppendLine("Application warnings and errors before the failure:");
        Append(content, entries, MaxUncorrelatedLines);
        startFailure.Data[StoveStartupException.DetailsKey] = content.ToString().TrimEnd();
    }

    private static void Append(StringBuilder content, Entry[] entries, int limit)
    {
        if (entries.Length > limit)
            content.AppendLine(CultureInfo.InvariantCulture, $"[{entries.Length - limit} earlier entries omitted]");
        foreach (var entry in entries.TakeLast(limit))
        {
            content.AppendLine(CultureInfo.InvariantCulture, $"{entry.Timestamp:HH:mm:ss.fff} [{entry.Level}] {entry.Category}: {entry.Message}");
            if (entry.Exception is not null) content.AppendLine(entry.Exception);
        }
    }

    private void Add(Entry entry)
    {
        _entries.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > Capacity && _entries.TryDequeue(out _)) Interlocked.Decrement(ref _count);
    }

    private sealed record Entry(DateTimeOffset Timestamp, string? TraceId, LogLevel Level, string Category, string Message, string? Exception);

    private sealed class Logger(ApplicationLogCollector owner, string category) : ILogger
    {
        // Framework categories narrate every request at Information; the application's own categories are the signal.
        private readonly LogLevel _minimum =
            category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal)
                ? LogLevel.Warning : LogLevel.Information;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimum && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var traceId = Activity.Current?.TraceId.ToString();
            // Without a trace there is no owner; keep only what may explain a failure.
            if (traceId is null && logLevel < LogLevel.Warning) return;
            owner.Add(new Entry(DateTimeOffset.UtcNow, traceId, logLevel, category, formatter(state, exception), exception?.ToString()));
        }
    }
}
