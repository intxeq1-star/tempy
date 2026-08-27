using System.Diagnostics;
using System.Text;

namespace OMAgent;

public sealed class RunResult
{
    public bool Started { get; set; }
    public bool TimedOut { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public int Pid { get; set; } = -1;
}

public sealed class ScriptRunner
{
    private readonly object _gate = new();

    public async Task<RunResult> RunAsync(AppConfig config, CancellationToken external, Action<int>? onStart = null)
    {
        var scriptPath = config.ScriptPath;
        if (!File.Exists(scriptPath))
            return new RunResult { Started = false, Output = "worker.ps1 not found at " + scriptPath };

        if (!Monitor.TryEnter(_gate))
            return new RunResult { Started = false, Output = "previous script run still active; skipped this cycle." };

        var started1 = DateTime.UtcNow;
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        try
        {
            using var proc = new Process { StartInfo = psi };
            try { proc.Start(); }
            catch (Exception ex) { return new RunResult { Started = false, Output = $"could not start PowerShell: {ex.Message}" }; }

            onStart?.Invoke(proc.Id);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(external);
            var stderrTask = proc.StandardError.ReadToEndAsync(external);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            if (config.ScriptTimeoutSeconds > 0)
                cts.CancelAfter(TimeSpan.FromSeconds(config.ScriptTimeoutSeconds));

            bool timedOut = false;
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { proc.Kill(entireProcessTree: true); } catch { }
            }

            string stdout = "";
            try { stdout = await stdoutTask.ConfigureAwait(false); } catch { }
            string stderr = "";
            try { stderr = await stderrTask.ConfigureAwait(false); } catch { }

            using var final = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { await proc.WaitForExitAsync(final.Token).ConfigureAwait(false); } catch { }

            var output = (stdout ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(stderr)) output = output + Environment.NewLine + "STDERR: " + stderr.Trim();

            var result = new RunResult
            {
                Started = true,
                TimedOut = timedOut,
                ExitCode = timedOut ? -1 : proc.ExitCode,
                Output = output,
                Pid = proc.Id
            };
            Logger.Info($"Script run finished in {(DateTime.UtcNow - started1).TotalSeconds:F1}s (exit={result.ExitCode}, timedOut={result.TimedOut}).");
            return result;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }
}
