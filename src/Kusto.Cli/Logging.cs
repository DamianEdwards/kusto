using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

public static class LoggingFactoryBuilder
{
    public static ILoggerFactory Create(LogLevel? requestedLogLevel, string? logFilePath = null, TextWriter? stderrWriter = null)
    {
        var effectiveLevel = requestedLogLevel ?? LogLevel.Information;
        var resolvedLogFilePath = logFilePath ?? GetDefaultLogFilePath();
        var resolvedStderr = stderrWriter ?? Console.Error;

        return LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(effectiveLevel);
            builder.AddProvider(new FileLoggerProvider(resolvedLogFilePath, effectiveLevel));
            if (requestedLogLevel.HasValue)
            {
                builder.AddProvider(new StderrLoggerProvider(effectiveLevel, resolvedStderr));
            }
        });
    }

    public static string GetDefaultLogFilePath()
    {
        return Path.Combine(Path.GetTempPath(), "kusto", "kusto.log");
    }
}

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly string _logFilePath;
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(string logFilePath, LogLevel minimumLevel)
    {
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logFilePath = logFilePath;
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new TextLogger(categoryName, _minimumLevel, WriteLine);
    }

    public void Dispose()
    {
    }

    private void WriteLine(string line)
    {
        lock (_sync)
        {
            using var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(line);
            writer.Flush();
        }
    }
}

public sealed class StderrLoggerProvider(LogLevel minimumLevel, TextWriter writer) : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly LogLevel _minimumLevel = minimumLevel;
    private readonly TextWriter _writer = writer;

    public ILogger CreateLogger(string categoryName)
    {
        return new TextLogger(categoryName, _minimumLevel, WriteLine);
    }

    public void Dispose()
    {
    }

    private void WriteLine(string line)
    {
        lock (_sync)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }
}

internal sealed class TextLogger(string categoryName, LogLevel minimumLevel, Action<string> writeLine) : ILogger
{
    private readonly string _categoryName = categoryName;
    private readonly LogLevel _minimumLevel = minimumLevel;
    private readonly Action<string> _writeLine = writeLine;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None && logLevel >= _minimumLevel;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {_categoryName}: {message}";
        if (exception is not null)
        {
            line = $"{line}{Environment.NewLine}{exception}";
        }

        _writeLine(line);
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}
