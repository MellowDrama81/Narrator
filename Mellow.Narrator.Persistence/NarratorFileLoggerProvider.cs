using System.Text;
using System.Text.Json;
using Mellow.Narrator.Core;
using Microsoft.Extensions.Logging;

namespace Mellow.Narrator.Persistence;

public sealed class NarratorFileLoggerProvider : ILoggerProvider, INarratorLogLevelSwitch
{
    internal const long MaximumFileBytes = 5 * 1024 * 1024;
    internal const int RetainedFileCount = 3;

    private readonly object _sync = new();
    private readonly string _logDirectory;
    private readonly string _currentLogPath;
    private volatile NarratorLogLevel _minimumLevel = NarratorLogLevel.Information;
    private bool _disposed;

    public NarratorFileLoggerProvider(PersistenceOptions options)
    {
        _logDirectory = Path.Combine(options.GetValidatedRoot(), "Mellow.Narrator", "logs");
        _currentLogPath = Path.Combine(_logDirectory, "narrator.log");
    }

    public NarratorLogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_sync) _disposed = true;
    }

    private bool IsEnabled(string category, LogLevel level)
    {
        // The framework's own HttpClientFactory logging handlers write raw request/response traffic
        // (headers included) at this category. OpenAiCompatibleProvider already logs what's useful
        // itself with credential redaction; letting this category through here would duplicate that
        // and risk writing an unredacted Authorization header to disk at Trace level.
        if (category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal))
            return false;
        var configured = _minimumLevel;
        return configured != NarratorLogLevel.Off && level >= ToMicrosoftLevel(configured);
    }

    private void Write(
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        Exception? exception)
    {
        if (!IsEnabled(category, level)) return;
        var entry = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            level = level.ToString(),
            category,
            eventId = eventId.Id == 0 ? (int?)null : eventId.Id,
            eventName = eventId.Name,
            message,
            exception = exception?.ToString()
        });
        var bytes = Encoding.UTF8.GetByteCount(entry) + Environment.NewLine.Length;

        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                Directory.CreateDirectory(_logDirectory);
                RotateIfRequired(bytes);
                File.AppendAllText(_currentLogPath, entry + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging is diagnostic and must never make an application operation fail.
            }
        }
    }

    private void RotateIfRequired(int incomingBytes)
    {
        if (!File.Exists(_currentLogPath) ||
            new FileInfo(_currentLogPath).Length + incomingBytes <= MaximumFileBytes)
            return;

        for (var index = RetainedFileCount; index >= 1; index--)
        {
            var source = index == 1
                ? _currentLogPath
                : Path.Combine(_logDirectory, $"narrator.{index - 1}.log");
            var destination = Path.Combine(_logDirectory, $"narrator.{index}.log");
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(source)) File.Move(source, destination);
        }
    }

    private static LogLevel ToMicrosoftLevel(NarratorLogLevel level) => level switch
    {
        NarratorLogLevel.Trace => LogLevel.Trace,
        NarratorLogLevel.Debug => LogLevel.Debug,
        NarratorLogLevel.Information => LogLevel.Information,
        NarratorLogLevel.Warning => LogLevel.Warning,
        NarratorLogLevel.Error => LogLevel.Error,
        _ => LogLevel.None
    };

    private sealed class FileLogger(NarratorFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(category, logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (provider.IsEnabled(category, logLevel))
                provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
