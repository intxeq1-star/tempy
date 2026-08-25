using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace LanAgent.Core.Management;

public sealed record QuickSystemInfo(
    string Hostname,
    string OsVersion,
    string OsBuild,
    string OsArch,
    string? LocalIp,
    List<string> AllIps);

/// <summary>Reads local machine facts. Registry/WMI-free where possible; safe on non-Windows (diagnostics).</summary>
public static class SystemInfoCollector
{
    public static QuickSystemInfo CollectQuick()
    {
        List<string> ips = GetLocalIPv4();
        return new QuickSystemInfo(
            Hostname: Environment.MachineName,
            OsVersion: GetOsVersion(),
            OsBuild: GetOsBuild(),
            OsArch: Environment.Is64BitOperatingSystem ? "X64" : "X86",
            LocalIp: ips.FirstOrDefault(),
            AllIps: ips);
    }

    /// <summary>Full report used by GET_SYSTEM_INFO (PROTOCOL_CONTRACT §10).</summary>
    public static Dictionary<string, object?> CollectFull()
    {
        var quick = CollectQuick();
        WindowsApi.TryGetMemory(out ulong totalMb, out ulong availMb);

        var drives = new List<Dictionary<string, object?>>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
            drives.Add(new Dictionary<string, object?>
            {
                ["name"] = drive.Name,
                ["total_gb"] = Math.Round(drive.TotalSize / 1024.0 / 1024 / 1024, 1),
                ["free_gb"] = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024 / 1024, 1)
            });
        }

        return new Dictionary<string, object?>
        {
            ["hostname"] = quick.Hostname,
            ["os_version"] = quick.OsVersion,
            ["os_build"] = quick.OsBuild,
            ["os_arch"] = quick.OsArch,
            ["agent_version"] = AgentInfo.Version,
            ["uptime_sec"] = (long)(Environment.TickCount64 / 1000),
            ["cpu"] = ReadCpuName(),
            ["ram_total_mb"] = totalMb == 0 ? null : totalMb,
            ["ram_free_mb"] = availMb == 0 ? null : availMb,
            ["drives"] = drives,
            ["local_ips"] = quick.AllIps,
            ["machine_guid"] = ReadMachineGuid(),
            ["dotnet_runtime"] = Environment.Version.ToString()
        };
    }

    public static List<string> GetLocalIPv4()
    {
        var ips = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = addr.Address.ToString();
                        if (!ips.Contains(ip)) ips.Add(ip);
                    }
                }
            }
        }
        catch
        {
            // Network enumeration must never crash the agent.
        }
        return ips;
    }

    private static string GetOsVersion()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key is not null)
                {
                    string? product = key.GetValue("ProductName") as string;
                    string? display = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string;
                    string? build = key.GetValue("CurrentBuildNumber") as string;
                    string? ubr = (key.GetValue("UBR") as int?)?.ToString();
                    int buildNum = int.TryParse(build, out int b) ? b : 0;
                    // ProductName still says "Windows 10" on Windows 11 — normalize via build number.
                    string major = buildNum >= 22000 && product is not null && product.Contains("Windows 10")
                        ? product.Replace("Windows 10", "Windows 11")
                        : (product ?? "Windows");
                    string suffix = string.IsNullOrEmpty(display) ? "" : " " + display;
                    return $"{major}{suffix} (build {build}.{ubr})";
                }
            }
        }
        catch
        {
            // fall through to Environment
        }
        return Environment.OSVersion.VersionString;
    }

    private static string GetOsBuild()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key is not null)
                {
                    string? build = key.GetValue("CurrentBuildNumber") as string;
                    string? ubr = (key.GetValue("UBR") as int?)?.ToString();
                    return $"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{build}.{ubr}";
                }
            }
        }
        catch { /* fall through */ }
        return Environment.OSVersion.Version.ToString();
    }

    private static string? ReadCpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key is not null) return key.GetValue("ProcessorNameString") as string;
            }
        }
        catch { /* not critical */ }
        return null;
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (key is not null) return key.GetValue("MachineGuid") as string;
            }
        }
        catch { /* not critical */ }
        return null;
    }
}
