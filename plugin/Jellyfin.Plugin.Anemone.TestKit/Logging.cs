using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>One captured log call, in a form tests can assert on directly.</summary>
public sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception)
{
    public override string ToString() => $"[{Level}] {Category}: {Message}";
}

/// <summary>
/// <see cref="ILoggerFactory"/> that hands out <see cref="FakeLogger{T}"/>/<see cref="FakeLogger"/> instances
/// which all append to one shared, thread-safe <see cref="Entries"/> list - so a test can assert "this log
/// line was written" without caring which component's logger produced it.
/// </summary>
public sealed class FakeLoggerFactory : ILoggerFactory
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Every entry logged so far by any logger this factory created, in order.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>True if any captured entry's message contains <paramref name="substring"/> (ordinal).</summary>
    public bool HasMessageContaining(string substring) =>
        Entries.Any(e => e.Message.Contains(substring, StringComparison.Ordinal));

    public ILogger<T> CreateLogger<T>() => new FakeLogger<T>(this);

    ILogger ILoggerFactory.CreateLogger(string categoryName) => new FakeLogger(this, categoryName);

    internal void Append(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public void AddProvider(ILoggerProvider provider)
    {
        // No-op: this factory IS the provider, there's nothing external to attach.
    }

    public void Dispose()
    {
    }
}

/// <summary>Non-generic <see cref="ILogger"/> that appends every call to its owning <see cref="FakeLoggerFactory"/>.</summary>
public sealed class FakeLogger : ILogger
{
    private readonly FakeLoggerFactory _factory;
    private readonly string _category;

    public FakeLogger(FakeLoggerFactory factory, string category)
    {
        _factory = factory;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _factory.Append(new LogEntry(_category, logLevel, eventId, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>Generic <see cref="ILogger{T}"/> wrapper so <c>_loggerFactory.CreateLogger&lt;T&gt;()</c> callers get a typed category name.</summary>
public sealed class FakeLogger<T> : ILogger<T>
{
    private readonly FakeLogger _inner;

    public FakeLogger(FakeLoggerFactory factory)
    {
        _inner = new FakeLogger(factory, typeof(T).FullName ?? typeof(T).Name);
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}
