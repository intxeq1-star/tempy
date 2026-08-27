using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace OMClientAgent.Services;

public sealed class UserSessionManager
{
    private readonly ILogger<UserSessionManager> _logger;

    public UserSessionManager(ILogger<UserSessionManager> logger)
    {
        _logger = logger;
    }

    public bool HasInteractiveUser()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var sessionId = (int)WtsGetActiveConsoleSessionId();
            if (sessionId == -1)
                return false;

            IntPtr buffer = IntPtr.Zero;
            uint bytes = 0;
            if (WtsQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.WTSUserName, out buffer, out bytes) && bytes > 0)
            {
                var user = Marshal.PtrToStringAnsi(buffer);
                WtsFreeMemory(buffer);
                return !string.IsNullOrWhiteSpace(user);
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not resolve interactive user session: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<(bool ran, int exitCode, string output)> TryRunInteractiveAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !HasInteractiveUser())
            return (false, -1, string.Empty);

        var taskName = $"OMAgent_Job_{Guid.NewGuid():N}";
        var outFile = Path.Combine(Path.GetTempPath(), $"omagent_{Guid.NewGuid():N}.out");
        var errFile = Path.Combine(Path.GetTempPath(), $"omagent_{Guid.NewGuid():N}.err");
        var wrapped = $"{command} > \"{outFile}\" 2> \"{errFile}\"";
        try
        {
            await RunProcessAsync("schtasks.exe", $"/Create /TN \"{taskName}\" /TR \"{wrapped}\" /SC ONCE /ST 23:59 /IT /F", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            await RunProcessAsync("schtasks.exe", $"/Run /TN \"{taskName}\"", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + timeout;
            var exitCode = -1;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var query = await RunProcessAsync("schtasks.exe", $"/Query /TN \"{taskName}\" /V /FO LIST", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                if (query.output.Contains("Last Result:", StringComparison.OrdinalIgnoreCase))
                {
                    var line = query.output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(x => x.Contains("Last Result:", StringComparison.OrdinalIgnoreCase));
                    if (line is not null)
                    {
                        var val = line.Split(':')[^1].Trim();
                        exitCode = int.TryParse(val, out var v) ? v : -1;
                        break;
                    }
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            await RunProcessAsync("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            var stdout = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, ct).ConfigureAwait(false) : string.Empty;
            var stderr = File.Exists(errFile) ? await File.ReadAllTextAsync(errFile, ct).ConfigureAwait(false) : string.Empty;
            return (true, exitCode, stdout + Environment.NewLine + stderr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Interactive execution via scheduled task failed.");
            return (false, -1, ex.Message);
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            try { if (File.Exists(errFile)) File.Delete(errFile); } catch { }
        }
    }

    private static async Task<(int exitCode, string output)> RunProcessAsync(string file, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (p.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private enum WtsInfoClass { WTSUserName = 5 }

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern uint WtsGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WtsQuerySessionInformation(IntPtr hServer, int sessionId, WtsInfoClass wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WtsFreeMemory(IntPtr pMemory);
}
