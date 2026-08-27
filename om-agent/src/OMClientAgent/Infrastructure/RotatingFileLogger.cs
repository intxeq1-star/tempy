using System.Text;
using Microsoft.Extensions.Logging;

namespace OMClientAgent.Infrastructure;

public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _lock = new();
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxFiles = 10;

    public RotatingFileLoggerProvider(string? directory = null)
    {
        _directory = directory ?? Core.OmPaths.LogsDirectory;
        try { Directory.CreateDirectory(_directory); } catch { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Write(string category, LogLevel level, string message)
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(_directory)) return;
                var file = Path.Combine(_directory, $"agent-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-11}] [{category}] {message}{Environment.NewLine}";
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length + line.Length > MaxFileBytes)
                {
                    fi.MoveTo(Path.Combine(_directory, $"agent-{DateTime.Now:yyyyMMdd}-{DateTime.Now:HHmmss}.log"), overwrite: true);
                    RotateOld();
                }
                File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch { }
        }
    }

    private void RotateOld()
    {
        var files = Directory.GetFiles(_directory, "agent-*.log").OrderByDescending(f => f).ToArray();
        for (var i = MaxFiles; i < files.Length; i++)
        {
            try { File.Delete(files[i]); } catch { }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly RotatingFileLoggerProvider _provider;
        private readonly string _category;
        public FileLogger(RotatingFileLoggerProvider provider, string category) { _provider = provider; _category = category; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            if (exception is not null) msg += Environment.NewLine + exception;
            _provider.Write(_category, logLevel, msg);
        }
    }
}
