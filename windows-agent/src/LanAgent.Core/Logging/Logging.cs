using System.Text.Json;

namespace LanAgent.Core.Logging;

public enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

/// <summary>
/// Minimal structured logger interface used across the agent. Every entry carries a timestamp,
/// level, component context, free-text message and an optional structured details object.
/// </summary>
public interface IAgentLog : IDisposable
{
    void Log(LogLevel level, string context, string message, object? details = null);
    void Debug(string context, string message, object? details = null);
    void Info(string context, string message, object? details = null);
    void Warn(string context, string message, object? details = null);
    void Error(string context, string message, object? details = null, Exception? exception = null);
    /// <summary>Flush pending writes (used on shutdown).</summary>
    void Flush();
}

/// <summary>
/// Rolling structured file logger. Writes one JSON object per line to
/// {logDirectory}/lanagent-YYYYMMDD.log with daily rotation, per-file size cap and retention pruning.
/// Thread-safe. Also mirrors to console when enabled (interactive/console debugging).
/// </summary>
public sealed class RollingFileLog : IAgentLog
{
    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly int _retentionDays;
    private readonly bool _console;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentFilePath = "";
    private DateTime _currentDate;
    private int _rotationIndex;
    private bool _disposed;

    public RollingFileLog(string logDirectory, long maxFileBytes = 10 * 1024 * 1024, int retentionDays = 14, bool mirrorToConsole = false)
    {
        _directory = logDirectory;
        _maxFileBytes = maxFileBytes;
        _retentionDays = retentionDays;
        _console = mirrorToConsole;
        Directory.CreateDirectory(logDirectory);
        OpenWriter(DateTime.Today, 0);
        Task.Run(() => TryPruneOldLogs());
    }

    public string LogDirectory => _directory;

    public void Log(LogLevel level, string context, string message, object? details = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["level"] = level.ToString().ToUpperInvariant(),
            ["ctx"] = context,
            ["msg"] = message
        };
        if (details is not null) entry["details"] = details;
        string line = Util.AgentJson.Serialize(entry);
        WriteLine(line);
    }

    public void Debug(string context, string message, object? details = null) => Log(LogLevel.Debug, context, message, details);
    public void Info(string context, string message, object? details = null) => Log(LogLevel.Info, context, message, details);
    public void Warn(string context, string message, object? details = null) => Log(LogLevel.Warning, context, message, details);

    public void Error(string context, string message, object? details = null, Exception? exception = null)
    {
        var d = details as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        if (details is not null && !ReferenceEquals(d, details)) d["details"] = details;
        if (exception is not null)
        {
            d["exception_type"] = exception.GetType().Name;
            d["exception"] = exception.Message;
        }
        Log(LogLevel.Error, context, message, d);
    }

    private void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_disposed || _writer is null) return;
            try
            {
                RotateIfNeeded(line.Length + 2);
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (IOException)
            {
                // Disk problems must never crash the agent; drop the line.
            }
            if (_console)
            {
                try { Console.WriteLine(line); } catch { /* ignore */ }
            }
        }
    }

    private void RotateIfNeeded(long incomingBytes)
    {
        DateTime today = DateTime.Today;
        if (today != _currentDate)
        {
            OpenWriter(today, 0);
            return;
        }
        try
        {
            var fi = new FileInfo(_currentFilePath);
            if (fi.Exists && fi.Length + incomingBytes > _maxFileBytes)
            {
                _rotationIndex++;
                OpenWriter(today, _rotationIndex);
            }
        }
        catch (IOException)
        {
            // Keep using the current writer if size probing fails.
        }
    }

    private void OpenWriter(DateTime date, int rotationIndex)
    {
        try
        {
            _writer?.Dispose();
        }
        catch { /* ignore */ }

        _currentDate = date;
        _rotationIndex = rotationIndex;
        _currentFilePath = Path.Combine(_directory,
            rotationIndex == 0
                ? $"lanagent-{date:yyyyMMdd}.log"
                : $"lanagent-{date:yyyyMMdd}.{rotationIndex}.log");
        var stream = new FileStream(_currentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = false };
    }

    private void TryPruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Today.AddDays(-_retentionDays);
            foreach (var file in Directory.GetFiles(_directory, "lanagent-*.log"))
            {
                // File name shapes: lanagent-YYYYMMDD.log  /  lanagent-YYYYMMDD.N.log
                string name = Path.GetFileNameWithoutExtension(file); // lanagent-YYYYMMDD[.N]
                if (name.Length < "lanagent-YYYYMMDD".Length) continue;
                string datePart = name.Substring("lanagent-".Length, 8);
                if (DateTime.TryParseExact(datePart, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime fileDate)
                    && fileDate < cutoff)
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }
            }
        }
        catch
        {
            // Never let pruning bring the agent down.
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Flush(); _writer?.Dispose(); } catch { /* ignore */ }
            _writer = null;
        }
    }
}

/// <summary>No-op logger for tests.</summary>
public sealed class NullLog : IAgentLog
{
    public static readonly NullLog Instance = new();
    public void Log(LogLevel level, string context, string message, object? details = null) { }
    public void Debug(string context, string message, object? details = null) { }
    public void Info(string context, string message, object? details = null) { }
    public void Warn(string context, string message, object? details = null) { }
    public void Error(string context, string message, object? details = null, Exception? exception = null) { }
    public void Flush() { }
    public void Dispose() { }
}

/// <summary>Collects log entries in memory; used by unit tests and the test harness.</summary>
public sealed class MemoryLog : IAgentLog
{
    public sealed record Entry(LogLevel Level, string Context, string Message, object? Details);
    private readonly object _lock = new();
    public List<Entry> Entries { get; } = new();
    public void Log(LogLevel level, string context, string message, object? details = null)
    { lock (_lock) { Entries.Add(new Entry(level, context, message, details)); } }
    public void Debug(string context, string message, object? details = null) => Log(LogLevel.Debug, context, message, details);
    public void Info(string context, string message, object? details = null) => Log(LogLevel.Info, context, message, details);
    public void Warn(string context, string message, object? details = null) => Log(LogLevel.Warning, context, message, details);
    public void Error(string context, string message, object? details = null, Exception? exception = null) => Log(LogLevel.Error, context, message, details);
    public void Flush() { }
    public void Dispose() { }
    public List<Entry> Snapshot() { lock (_lock) { return Entries.ToList(); } }
}
