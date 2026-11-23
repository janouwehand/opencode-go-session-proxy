using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenCodeGoProxy.Logging;

/// <summary>
/// Minimal rolling file logger used when the proxy runs without a console (Windows Service).
/// One file per UTC day (<c>proxy-yyyyMMdd.log</c>), rolled to <c>proxy-yyyyMMdd.N.log</c>
/// when the size cap is reached. Logging is strictly best effort: it never throws into the
/// request path, and the provider becomes inert if the log directory cannot be used.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly int _retentionDays;
    private readonly Func<DateTimeOffset> _clock;
    private FileStream? _stream;
    private DateOnly? _fileDate;
    private long _currentBytes;
    private bool _disabled;

    public FileLoggerProvider(
        string directory,
        long maxFileBytes = 10 * 1024 * 1024,
        int retentionDays = 7,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        _maxFileBytes = maxFileBytes;
        _retentionDays = retentionDays;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        try
        {
            Directory.CreateDirectory(_directory);
            DeleteExpiredFiles();
        }
        catch
        {
            // An unusable log directory must not prevent the proxy from starting.
            _disabled = true;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disabled = true;
            CloseStream();
        }
    }

    private void Write(DateTimeOffset now, string text)
    {
        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                var date = DateOnly.FromDateTime(now.UtcDateTime);
                var bytes = Encoding.UTF8.GetBytes(text);

                if (_stream is null || _fileDate != date)
                {
                    OpenStream(date);
                }

                if (_stream is not null && _currentBytes > 0 && _currentBytes + bytes.Length > _maxFileBytes)
                {
                    Roll(date);
                }

                if (_stream is null)
                {
                    return;
                }

                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
                _currentBytes += bytes.Length;
            }
            catch
            {
                // Keep the request path alive; retry on the next log entry.
                CloseStream();
            }
        }
    }

    private void OpenStream(DateOnly date)
    {
        CloseStream();
        _fileDate = date;
        _stream = new FileStream(
            Path.Combine(_directory, $"proxy-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log"),
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        _currentBytes = _stream.Length;
    }

    private void Roll(DateOnly date)
    {
        CloseStream();

        var basePath = Path.Combine(_directory, $"proxy-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
        for (var index = 1; ; index++)
        {
            var rolledPath = Path.Combine(
                _directory,
                $"proxy-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.{index.ToString(CultureInfo.InvariantCulture)}.log");
            if (File.Exists(rolledPath))
            {
                continue;
            }

            File.Move(basePath, rolledPath);
            break;
        }

        _fileDate = date;
        _stream = new FileStream(basePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _currentBytes = 0;
    }

    private void CloseStream()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _stream = null;
        _fileDate = null;
        _currentBytes = 0;
    }

    private void DeleteExpiredFiles()
    {
        if (_retentionDays <= 0)
        {
            return;
        }

        var cutoff = _clock().UtcDateTime.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "proxy-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best effort; a file held by another process is retried on the next start.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
            {
                return;
            }

            try
            {
                var now = provider._clock();
                var builder = new StringBuilder();
                builder.Append(now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(" Z  ")
                    .Append(logLevel)
                    .Append("  ")
                    .Append(categoryName)
                    .Append(": ")
                    .Append(Flatten(formatter(state, exception)));

                if (exception is not null)
                {
                    var indented = exception.ToString()
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\n", "\n    ", StringComparison.Ordinal);
                    builder.Append('\n').Append("    ").Append(indented);
                }

                builder.Append('\n');
                provider.Write(now, builder.ToString());
            }
            catch
            {
                // Logging must never break request processing.
            }
        }

        /// <summary>Keeps the formatted message on a single log line.</summary>
        private static string Flatten(string message) =>
            message.Replace('\r', ' ').Replace('\n', ' ');
    }
}
