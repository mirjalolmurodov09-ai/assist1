using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.StudentAgent.Infrastructure.Logging;

/// <summary>Small rolling file logger (one file per day, old files removed). Writing happens on a background task so callers never block on disk.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 14;
    private const int QueueCapacity = 2048;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly Task _writer;

    public FileLoggerProvider(string directory, TimeProvider? time = null)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        PurgeOldFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void Enqueue(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(_time.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(" [").Append(ShortLevel(level)).Append("] ")
            .Append(category[(category.LastIndexOf('.') + 1)..]).Append(": ")
            .Append(SensitiveDataRedactor.Redact(message));
        if (exception is not null)
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(SensitiveDataRedactor.Redact(exception.Message));
        _queue.Writer.TryWrite(line.ToString());
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var path = Path.Combine(_directory, $"agent-{_time.GetUtcNow():yyyyMMdd}.log");
                try
                {
                    await File.AppendAllTextAsync(path, line + Environment.NewLine).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"Log write failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void PurgeOldFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "agent-*.log"))
            {
                if (_time.GetUtcNow() - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(RetentionDays)) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Log purge failed: {ex.Message}");
        }
    }

    private static string ShortLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(3)); // Flush what is queued.
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _owner;

        public FileLogger(string category, FileLoggerProvider owner)
        {
            _category = category;
            _owner = owner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _owner.Enqueue(logLevel, _category, formatter(state, exception), exception);
        }
    }
}
