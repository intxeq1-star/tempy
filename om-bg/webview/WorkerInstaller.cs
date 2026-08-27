using System.Diagnostics;

namespace OMAgent;

public static class WorkerInstaller
{
    public const string ExeName = "OMAgent.exe";

    public static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var (_, output) = Sc("query " + OmConstants.ServiceName);
        return output.Contains(OmConstants.ServiceName, StringComparison.OrdinalIgnoreCase);
    }

    public static int Install()
    {
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);
        var stableExe = DeployStableCopy(exePath);

        if (IsInstalled())
        {
            Console.WriteLine($"Service '{OmConstants.ServiceName}' already exists. Stopping and reinstalling...");
            Stop();
            Sc($"delete {OmConstants.ServiceName}");
            Thread.Sleep(2000);
        }

        EnsureDefaults();

        var bin = $"\"{stableExe}\"";
        if (!Sc($"create {OmConstants.ServiceName} binPath= \"{stableExe}\" start= auto error= normal").ok)
            return 1;

        Sc($"description {OmConstants.ServiceName} \"{OmConstants.Description}\"");
        Sc($"config {OmConstants.ServiceName} start= auto");
        Sc($"failure {OmConstants.ServiceName} reset= 86400 actions= restart/15000/restart/30000/restart/60000");

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Microsoft.Win32.Registry.SetValue(
                    $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{OmConstants.ServiceName}",
                    "DelayedAutostart", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
        }

        Start();
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsInstalled()) return 0;
        Stop();
        Sc($"delete {OmConstants.ServiceName}");
        return 0;
    }

    public static int Start()
    {
        if (!IsInstalled()) return 1;
        Sc($"start {OmConstants.ServiceName}");
        return 0;
    }

    public static int Stop()
    {
        if (!IsInstalled()) return 0;
        Sc($"stop {OmConstants.ServiceName}");
        return 0;
    }

    private static (bool ok, string output) Sc(string arguments)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        try
        {
            if (!p.Start()) return (false, "");
            p.WaitForExit(15000);
            var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (string.IsNullOrWhiteSpace(outp))
                return (p.ExitCode == 0, "");
            return (p.ExitCode == 0, outp);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string DeployStableCopy(string sourcePath)
    {
        var target = Path.Combine(OmConstants.DataRoot, ExeName);
        try
        {
            Directory.CreateDirectory(OmConstants.DataRoot);
            if (!string.Equals(sourcePath, target, StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            return sourcePath;
        }
    }

    public static void StopWorker()
    {
        var pid = StatusStore.Read().LastScriptPid;
        if (pid > 0) KillTree(pid);
        Stop();
        Thread.Sleep(1200);
    }

    private static void KillTree(int pid)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/PID {pid} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.Start();
            p.WaitForExit(8000);
        }
        catch { }
    }

    private static void EnsureDefaults()
    {
        try
        {
            Directory.CreateDirectory(OmConstants.DataRoot);
            Directory.CreateDirectory(OmConstants.LogsDir);
            if (!File.Exists(OmConstants.ScriptPath))
            {
                File.WriteAllText(OmConstants.ScriptPath, DefaultWorkerScript.Content);
            }
            if (!File.Exists(OmConstants.ConfigPath))
                new AppConfig().Save();
        }
        catch { }
    }
}
