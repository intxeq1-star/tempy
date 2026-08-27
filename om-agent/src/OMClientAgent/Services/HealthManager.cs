using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public sealed class HealthManager
{
    private readonly IResultReporter _reporter;
    private readonly LocalDatabase _db;
    private readonly AgentConfigService _config;
    private readonly ILogger<HealthManager> _logger;
    private DateTime _lastSampleAt;
    private double _lastCpu;

    public HealthManager(IResultReporter reporter, LocalDatabase db, AgentConfigService config, ILogger<HealthManager> logger)
    {
        _reporter = reporter;
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var mode = _db.GetSetting("HttpFallbackActive") == "true" ? "https" : "signalr";
        var m = _config.Current.MachineId;
        var heartbeat = new AgentHeartbeat
        {
            MachineId = m,
            ComputerName = Environment.MachineName,
            AgentVersion = OmProtocol.Version,
            IpAddress = GetPrimaryIp(),
            Mode = mode,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await _reporter.SendHeartbeatAsync(heartbeat, ct).ConfigureAwait(false);
    }

    public async Task SendHealthAsync(CancellationToken ct)
    {
        var health = new
        {
            cpu = SampleCpu(),
            diskFreeGb = Math.Round(GetDiskFreeMb() / 1024.0, 1),
            memoryMb = GetMemoryMb(),
            service = _db.GetSetting("HttpFallbackActive") == "true" ? "degraded" : "running",
            pendingJobs = _db.GetPendingJobs().Count
        };
        var msg = new HealthReportMsg
        {
            MachineId = _config.Current.MachineId,
            AgentVersion = OmProtocol.Version,
            HealthJson = System.Text.Json.JsonSerializer.Serialize(health, Core.Models.OmJson.Options)
        };
        await _reporter.SendHealthAsync(msg, ct).ConfigureAwait(false);
    }

    private long SampleCpu()
    {
        try
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSampleAt).TotalSeconds;
            _lastSampleAt = now;
            if (elapsed < 1) return (long)_lastCpu;

            var cpu = OperatingSystem.IsWindows() ? WindowsCpu() : 0;
            _lastCpu = cpu;
            return (long)_lastCpu;
        }
        catch { return 0; }
    }

    private double WindowsCpu()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return 0;
            var idleMs = ToMs(idle);
            var kernelMs = ToMs(kernel);
            var userMs = ToMs(user);
            var busy = kernelMs + userMs - idleMs;
            var total = kernelMs + userMs;
            var percent = (double)busy * 100.0 / (total == 0 ? 1 : total);
            return percent;
        }
        catch { return 0; }
    }

    private static long ToMs(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        var high = (ulong)ft.dwHighDateTime;
        var low = (ulong)ft.dwLowDateTime;
        var fileTime = (high << 32) | low;
        return (long)(fileTime / 10000);
    }

    private long GetMemoryMb()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MEMORYSTATUSEX();
                status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                if (GlobalMemoryStatusEx(ref status))
                    return (long)status.ullAvailPhys / (1024 * 1024);
            }
            else
            {
                var mem = File.ReadAllText("/proc/meminfo").Split('\n')
                    .FirstOrDefault(l => l.StartsWith("MemTotal"))?.Split(':')[1].Trim().Replace("kB", "");
                if (long.TryParse(mem, out var kb)) return kb / 1024;
            }
        }
        catch { }
        return 0;
    }

    private long GetDiskFreeMb()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_config.Current.DataDirectory) ?? Path.GetPathRoot(Environment.CurrentDirectory) ?? @"C:\");
            return drive.AvailableFreeSpace / (1024 * 1024);
        }
        catch { return 0; }
    }

    private static string? GetPrimaryIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                foreach (var addr in props.UnicastAddresses)
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return addr.Address.ToString();
            }
        }
        catch { }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime, out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime, out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
