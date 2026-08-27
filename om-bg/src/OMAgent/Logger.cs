namespace OMAgent;

public static class Logger
{
    private static readonly object Lock = new();
    private const long MaxBytes = 5 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.Message}");

    private static void Write(string level, string message)
    {
        lock (Lock)
        {
            try
            {
                var dir = OmConstants.LogsDir;
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"omagent-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}{Environment.NewLine}";
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length + line.Length > MaxBytes)
                {
                    fi.MoveTo(Path.Combine(dir, $"omagent-{DateTime.Now:yyyyMMdd}-{DateTime.Now:HHmmss}.log"), overwrite: true);
                    Rotate();
                }
                File.AppendAllText(file, line);
            }
            catch { }
        }
    }

    private static void Rotate()
    {
        try
        {
            var files = Directory.GetFiles(OmConstants.LogsDir, "omagent-*.log").OrderByDescending(f => f).ToArray();
            foreach (var f in files.Skip(10)) File.Delete(f);
        }
        catch { }
    }

    public static string Tail(int lines)
    {
        try
        {
            if (!Directory.Exists(OmConstants.LogsDir)) return "(no log directory yet)";
            var file = Directory.GetFiles(OmConstants.LogsDir, "omagent-*.log")
                .OrderByDescending(f => f).FirstOrDefault();
            if (file is null) return "(no log files yet)";
            return string.Join('\n', File.ReadLines(file).Reverse().Take(lines).Reverse());
        }
        catch (Exception ex) { return $"(could not read log: {ex.Message})"; }
    }
}
