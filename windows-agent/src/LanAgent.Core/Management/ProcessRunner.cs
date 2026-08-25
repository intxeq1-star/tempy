using System.Diagnostics;

namespace LanAgent.Core.Management;

/// <summary>Captured result of an external process run.</summary>
public sealed record ProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    string StartedAtIso,
    string CompletedAtIso,
    long DurationMs)
{
    public static ProcessResult Failed(string error) => new(-1, "", error, false, Util.Tx.NowIso(), Util.Tx.NowIso(), 0);
}

/// <summary>
/// Runs external management processes (winget, netsh, powershell, shutdown …) with output capture,
/// hard timeout (kill on expiry) and output clamping. Never throws for normal process failures.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        int timeoutMs,
        string? workingDir = null,
        int maxOutputBytes = 256 * 1024,
        CancellationToken outerCt = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;

        string startedAt = Util.Tx.NowIso();
        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return ProcessResult.Failed($"failed to start '{fileName}'");
        }
        catch (Exception ex)
        {
            return ProcessResult.Failed($"failed to start '{fileName}': {ex.Message}");
        }

        process.StandardInput.Close(); // no stdin; child must not wait for input

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(outerCt);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(outerCt);

        bool timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        timeoutCts.CancelAfter(Math.Max(1000, timeoutMs));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* already gone */ }
        }

        string stdout = "", stderr = "";
        try { stdout = Clamp(await stdoutTask.ConfigureAwait(false), maxOutputBytes); }
        catch (Exception ex) { stdout = $"<stdout capture failed: {ex.Message}>"; }
        try { stderr = Clamp(await stderrTask.ConfigureAwait(false), maxOutputBytes); }
        catch (Exception ex) { stderr = $"<stderr capture failed: {ex.Message}>"; }

        sw.Stop();
        int exitCode = timedOut ? -1 : process.ExitCode;
        if (timedOut) stderr = (stderr + " | agent: process timed out and was killed").TrimStart(' ', '|');

        return new ProcessResult(exitCode, stdout, stderr, timedOut, startedAt, Util.Tx.NowIso(), sw.ElapsedMilliseconds);
    }

    /// <summary>Splits a raw command line into tokens honoring double quotes (used for allowlist checks).</summary>
    public static List<string> SplitCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string Clamp(string output, int maxBytes)
    {
        if (string.IsNullOrEmpty(output)) return output;
        if (System.Text.Encoding.UTF8.GetByteCount(output) <= maxBytes) return output;
        // Keep the tail: it usually carries the interesting error/summary lines.
        string tail = output[^Math.Min(output.Length, maxBytes / 2)..];
        return $"<output clamped, showing tail>\n{tail}";
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch
        {
            try { process.Kill(); } catch { /* already exiting */ }
        }
    }
}
