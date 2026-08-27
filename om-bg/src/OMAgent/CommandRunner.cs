using System.Diagnostics;
using System.Security.Principal;

namespace OMAgent;

public enum CommandShell
{
    Cmd,
    PowerShell
}

public sealed class CommandRunner
{
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool HasInteractiveUser()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var sessionId = (int)WtsGetActiveConsoleSessionId();
            if (sessionId == -1) return false;
            IntPtr buffer = IntPtr.Zero;
            uint bytes = 0;
            if (WtsQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.WTSUserName, out buffer, out bytes) && bytes > 0)
            {
                var user = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(buffer);
                WtsFreeMemory(buffer);
                return !string.IsNullOrWhiteSpace(user);
            }
            return false;
        }
        catch { return false; }
    }

    public static (string exe, string args) Build(string commandLine, CommandShell shell)
    {
        var command = commandLine ?? string.Empty;
        return shell switch
        {
            CommandShell.PowerShell =>
                ("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{Escape(command)}\""),
            _ =>
                ("cmd.exe", $"/d /s /c \"{Escape(command)}\"")
        };
    }

    private static string Escape(string command) => command.Replace("\"", "\\\"");

    public static async Task<RunResult> RunAdminAsync(string commandLine, CommandShell shell, TimeSpan timeout, CancellationToken ct)
    {
        var (exe, args) = Build(commandLine, shell);
        return await RunProcessAsync(exe, args, timeout, ct).ConfigureAwait(false);
    }

    public static async Task<RunResult> RunUserAsync(string commandLine, CommandShell shell, TimeSpan timeout, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !HasInteractiveUser())
            return new RunResult { Started = false, Output = "No interactive user session available." };

        var (exe, args) = Build(commandLine, shell);
        var taskName = $"OMAgent_User_{Guid.NewGuid():N}";
        var outFile = Path.Combine(Path.GetTempPath(), $"omagent_{Guid.NewGuid():N}.out");
        var errFile = Path.Combine(Path.GetTempPath(), $"omagent_{Guid.NewGuid():N}.err");
        var wrapped = $"{exe} {args} > \"{outFile}\" 2> \"{errFile}\"";

        try
        {
            await RunProcessAsync("schtasks.exe", $"/Create /TN \"{taskName}\" /TR \"{wrapped}\" /SC ONCE /ST 23:59 /IT /F", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            await RunProcessAsync("schtasks.exe", $"/Run /TN \"{taskName}\"", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + timeout;
            var exitCode = -1;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var query = await RunProcessAsync("schtasks.exe", $"/Query /TN \"{taskName}\" /V /FO LIST", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                var line = query.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(x => x.Contains("Last Result:", StringComparison.OrdinalIgnoreCase));
                if (line is not null)
                {
                    var val = line.Split(':')[^1].Trim();
                    exitCode = int.TryParse(val, out var v) ? v : -1;
                    break;
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            await RunProcessAsync("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            var stdout = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, ct).ConfigureAwait(false) : "";
            var stderr = File.Exists(errFile) ? await File.ReadAllTextAsync(errFile, ct).ConfigureAwait(false) : "";
            return new RunResult { Started = true, ExitCode = exitCode, Output = stdout + Environment.NewLine + stderr };
        }
        catch (Exception ex)
        {
            return new RunResult { Started = false, ExitCode = -1, Output = ex.Message };
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            try { if (File.Exists(errFile)) File.Delete(errFile); } catch { }
        }
    }

    private static async Task<RunResult> RunProcessAsync(string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
            var so = p.StandardOutput.ReadToEndAsync(ct);
            var se = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            bool timedOut = false;
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { timedOut = true; try { p.Kill(entireProcessTree: true); } catch { } }
            string stdout = ""; try { stdout = await so.ConfigureAwait(false); } catch { }
            string stderr = ""; try { stderr = await se.ConfigureAwait(false); } catch { }
            return new RunResult
            {
                Started = true,
                TimedOut = timedOut,
                ExitCode = timedOut ? -1 : p.ExitCode,
                Output = (stdout + Environment.NewLine + stderr).Trim()
            };
        }
        catch (Exception ex)
        {
            return new RunResult { Started = false, ExitCode = -1, Output = ex.Message };
        }
    }

    private enum WtsInfoClass { WTSUserName = 5 }

    [System.Runtime.InteropServices.DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern uint WtsGetActiveConsoleSessionId();

    [System.Runtime.InteropServices.DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WtsQuerySessionInformation(IntPtr hServer, int sessionId, WtsInfoClass wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [System.Runtime.InteropServices.DllImport("Wtsapi32.dll")]
    private static extern void WtsFreeMemory(IntPtr pMemory);
}
