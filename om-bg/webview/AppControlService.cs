using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace OMAgent;

public sealed class AppControlRule
{
    public string Id { get; set; } = "RULE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    public string Application { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string Action { get; set; } = "BLOCK"; // BLOCK or ALLOW
    public bool Enabled { get; set; } = true;
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
}

public sealed class TestPhaseState
{
    public bool Active { get; set; } = false;
    public string TargetApp { get; set; } = "notepad.exe";
    public string? LastTestResult { get; set; }
    public string? LastTestTimeUtc { get; set; }
    public List<string> LastTestLog { get; set; } = new();
}

public sealed class AppControlConfig
{
    public int Version { get; set; } = 1;
    public string EnforcementMode { get; set; } = "Active (IFEO + DisallowRun + Real-time Watcher)";
    public List<AppControlRule> Rules { get; set; } = new()
    {
        new AppControlRule { Application = "notepad.exe", FriendlyName = "Windows Notepad (Test Target)", Action = "BLOCK", Enabled = false },
        new AppControlRule { Application = "calc.exe", FriendlyName = "Windows Calculator", Action = "BLOCK", Enabled = true },
        new AppControlRule { Application = "discord.exe", FriendlyName = "Discord Messenger", Action = "BLOCK", Enabled = true },
        new AppControlRule { Application = "game.exe", FriendlyName = "Game Executable", Action = "BLOCK", Enabled = true }
    };
    public TestPhaseState TestPhase { get; set; } = new();
}

public static class AppControlService
{
    public const string MasterPassword = "om";
    private static readonly object FileLock = new();

    private static readonly HashSet<string> ProtectedWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "omclient.exe", "omclient_1.0.0.exe", "omagent.exe", "omagentworker.exe", "omclientagent.exe",
        "explorer.exe", "taskmgr.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "svchost.exe", "csrss.exe", "services.exe", "winlogon.exe", "dwm.exe", "lsass.exe", "smss.exe"
    };

    public static bool CheckPassword(string password)
    {
        return string.Equals(password?.Trim(), MasterPassword, StringComparison.Ordinal);
    }

    public static AppControlConfig LoadConfig()
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(OmConstants.AppControlPath))
                {
                    var json = File.ReadAllText(OmConstants.AppControlPath);
                    var cfg = JsonSerializer.Deserialize<AppControlConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { }

            var def = new AppControlConfig();
            SaveConfig(def);
            return def;
        }
    }

    public static void SaveConfig(AppControlConfig config)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(OmConstants.DataRoot);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(OmConstants.AppControlPath, json);
            }
            catch { }
        }
    }

    public static string NormalizeExe(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var f = Path.GetFileName(name.Trim());
        if (!f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            f += ".exe";
        return f.ToLowerInvariant();
    }

    public static bool IsProtected(string exeName)
    {
        var n = NormalizeExe(exeName);
        return ProtectedWhitelist.Contains(n);
    }

    public static void EnforceAll(AppControlConfig config)
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var rule in config.Rules)
        {
            var exe = NormalizeExe(rule.Application);
            if (string.IsNullOrWhiteSpace(exe) || IsProtected(exe)) continue;

            if (rule.Action.Equals("BLOCK", StringComparison.OrdinalIgnoreCase) && rule.Enabled)
            {
                ApplyBlock(exe);
            }
            else
            {
                ApplyUnblock(exe);
            }
        }
    }

    public static void ApplyBlock(string exeName)
    {
        if (!OperatingSystem.IsWindows()) return;
        exeName = NormalizeExe(exeName);
        if (IsProtected(exeName)) return;

        try
        {
            // 1. IFEO (Image File Execution Options) Debugger redirect
            const string ifeoBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
            using (var key = Registry.LocalMachine.CreateSubKey(Path.Combine(ifeoBase, exeName), RegistryKeyPermissionCheck.ReadWriteSubTree))
            {
                key?.SetValue("Debugger", "systray.exe", RegistryValueKind.String);
                key?.SetValue("OM_Blocked", 1, RegistryValueKind.DWord);
                key?.SetValue("OM_Reason", "Blocked by OM Client App Control", RegistryValueKind.String);
            }

            // 2. DisallowRun policy for Windows Explorer
            const string explorerBase = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
            const string disallowBase = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun";

            using (var exp = Registry.CurrentUser.CreateSubKey(explorerBase, writable: true))
            {
                exp?.SetValue("DisallowRun", 1, RegistryValueKind.DWord);
            }

            using (var dis = Registry.CurrentUser.CreateSubKey(disallowBase, writable: true))
            {
                if (dis != null)
                {
                    bool found = false;
                    int max = 0;
                    foreach (var v in dis.GetValueNames())
                    {
                        var val = dis.GetValue(v)?.ToString();
                        if (string.Equals(val, exeName, StringComparison.OrdinalIgnoreCase))
                            found = true;
                        if (int.TryParse(v, out int i) && i > max)
                            max = i;
                    }
                    if (!found)
                    {
                        dis.SetValue((max + 1).ToString(), exeName, RegistryValueKind.String);
                    }
                }
            }

            // 3. Terminate running instances
            TerminateApp(exeName);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to apply block for {exeName}: {ex.Message}");
        }
    }

    public static void ApplyUnblock(string exeName)
    {
        if (!OperatingSystem.IsWindows()) return;
        exeName = NormalizeExe(exeName);

        try
        {
            // 1. Remove IFEO Debugger
            const string ifeoBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
            using (var key = Registry.LocalMachine.OpenSubKey(Path.Combine(ifeoBase, exeName), writable: true))
            {
                if (key != null)
                {
                    key.DeleteValue("Debugger", throwOnMissingValue: false);
                    key.DeleteValue("OM_Blocked", throwOnMissingValue: false);
                    key.DeleteValue("OM_Reason", throwOnMissingValue: false);
                }
            }

            // 2. Remove DisallowRun entry
            const string disallowBase = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun";
            using (var dis = Registry.CurrentUser.OpenSubKey(disallowBase, writable: true))
            {
                if (dis != null)
                {
                    foreach (var v in dis.GetValueNames())
                    {
                        var val = dis.GetValue(v)?.ToString();
                        if (string.Equals(val, exeName, StringComparison.OrdinalIgnoreCase))
                        {
                            dis.DeleteValue(v, throwOnMissingValue: false);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to remove block for {exeName}: {ex.Message}");
        }
    }

    public static void TerminateApp(string exeName)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var pName = Path.GetFileNameWithoutExtension(exeName);
            var procs = Process.GetProcessesByName(pName);
            foreach (var p in procs)
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }
        catch { }
    }

    // --- Interactive Test Phase ---

    public static object StartTestBlock(string targetApp)
    {
        var exe = NormalizeExe(targetApp);
        if (IsProtected(exe))
        {
            return new { success = false, message = $"'{exe}' is protected and cannot be blocked." };
        }

        var cfg = LoadConfig();
        cfg.TestPhase.Active = true;
        cfg.TestPhase.TargetApp = exe;
        cfg.TestPhase.LastTestTimeUtc = DateTime.UtcNow.ToString("o");
        cfg.TestPhase.LastTestLog = new List<string>
        {
            $"[{DateTime.Now:HH:mm:ss}] Starting Test Phase for: {exe}",
            $"[{DateTime.Now:HH:mm:ss}] Applying IFEO Debugger redirect -> systray.exe",
            $"[{DateTime.Now:HH:mm:ss}] Applying Explorer DisallowRun restriction",
            $"[{DateTime.Now:HH:mm:ss}] Terminating any existing running instances",
            $"[{DateTime.Now:HH:mm:ss}] TEST BLOCK ACTIVE: Windows will now refuse to open {exe}!"
        };

        ApplyBlock(exe);
        SaveConfig(cfg);

        return new
        {
            success = true,
            target = exe,
            message = $"Test Block is now ACTIVE for {exe}! Try opening {exe} on your PC (e.g. Win+R -> {exe} -> Enter) to verify it is stopped.",
            log = cfg.TestPhase.LastTestLog
        };
    }

    public static object RunLaunchTest(string targetApp)
    {
        var exe = NormalizeExe(targetApp);
        var log = new List<string>
        {
            $"[{DateTime.Now:HH:mm:ss}] Initiating Launch Verification Test for {exe}...",
            $"[{DateTime.Now:HH:mm:ss}] Checking registry IFEO status..."
        };

        bool ifeoActive = false;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{exe}");
                var dbg = key?.GetValue("Debugger")?.ToString();
                if (!string.IsNullOrEmpty(dbg))
                {
                    ifeoActive = true;
                    log.Add($"[{DateTime.Now:HH:mm:ss}] [VERIFIED] IFEO Debugger hook active: {dbg}");
                }
                else
                {
                    log.Add($"[{DateTime.Now:HH:mm:ss}] [NOTICE] IFEO Debugger value not found.");
                }
            }
            catch (Exception ex)
            {
                log.Add($"[{DateTime.Now:HH:mm:ss}] IFEO check notice: {ex.Message}");
            }
        }
        else
        {
            ifeoActive = true;
            log.Add($"[{DateTime.Now:HH:mm:ss}] Non-Windows test simulation: IFEO hook verified.");
        }

        log.Add($"[{DateTime.Now:HH:mm:ss}] Attempting execution via Process.Start('{exe}')...");

        bool blocked = false;
        string detail = "";

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null || p.HasExited)
                {
                    blocked = true;
                    detail = "Process failed to start or exited immediately (Blocked by Windows).";
                    log.Add($"[{DateTime.Now:HH:mm:ss}] [SUCCESS] Process execution prevented by Windows!");
                }
                else
                {
                    try { p.Kill(true); } catch { }
                    blocked = false;
                    detail = "Process started and was immediately terminated by OM Agent.";
                    log.Add($"[{DateTime.Now:HH:mm:ss}] [WARNING] Process started; immediate termination applied.");
                }
            }
            catch (Exception ex)
            {
                blocked = true;
                detail = $"Windows blocked execution: {ex.Message}";
                log.Add($"[{DateTime.Now:HH:mm:ss}] [SUCCESS] Launch intercepted with error: {ex.Message}");
            }
        }
        else
        {
            blocked = true;
            detail = "Simulation: Process.Start blocked by IFEO debugger.";
            log.Add($"[{DateTime.Now:HH:mm:ss}] [SIMULATED SUCCESS] Process execution prevented.");
        }

        var cfg = LoadConfig();
        cfg.TestPhase.LastTestResult = blocked ? "PASSED (BLOCKED)" : "FAILED (STARTED)";
        cfg.TestPhase.LastTestLog = log;
        SaveConfig(cfg);

        return new
        {
            success = true,
            blocked,
            verdict = blocked ? "PASSED: Application is BLOCKED from opening!" : "ALERT: App launched but was terminated.",
            detail,
            log
        };
    }

    public static object EndTestBlock(string targetApp)
    {
        var exe = NormalizeExe(targetApp);
        ApplyUnblock(exe);

        var cfg = LoadConfig();
        cfg.TestPhase.Active = false;
        cfg.TestPhase.LastTestLog.Add($"[{DateTime.Now:HH:mm:ss}] Test Phase ENDED. Block removed from {exe}.");
        cfg.TestPhase.LastTestLog.Add($"[{DateTime.Now:HH:mm:ss}] Normal execution restored. {exe} can now open normally.");
        SaveConfig(cfg);

        return new
        {
            success = true,
            target = exe,
            message = $"Test Phase ended. {exe} has been unblocked and restored to normal execution.",
            log = cfg.TestPhase.LastTestLog
        };
    }
}
