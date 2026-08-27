using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace OMClientAgent.Infrastructure;

public sealed class MachineIdentityProvider
{
    private readonly ILogger<MachineIdentityProvider> _logger;
    private readonly LocalDatabase _db;
    private readonly string _agentVersion;

    public MachineIdentityProvider(ILogger<MachineIdentityProvider> logger, LocalDatabase db, string agentVersion)
    {
        _logger = logger;
        _db = db;
        _agentVersion = agentVersion;
    }

    public Core.Models.MachineIdentity GetOrCreate()
    {
        var stored = _db.LoadMachineIdentity();
        if (stored is not null && !string.IsNullOrWhiteSpace(stored.MachineId))
            return stored;

        var identity = new Core.Models.MachineIdentity
        {
            MachineId = NewMachineId(),
            ComputerName = Environment.MachineName,
            OsVersion = ReadOsVersion(),
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            DomainOrWorkgroup = ReadDomain(),
            AgentVersion = _agentVersion,
            EnrolledAtUtc = DateTime.UtcNow
        };
        _db.SaveMachineIdentity(identity);
        _logger.LogInformation("Created machine identity {MachineId} for {ComputerName}", identity.MachineId, identity.ComputerName);
        return identity;
    }

    private static string NewMachineId()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var guid = new Guid(bytes);
        return $"PC-{Environment.MachineName}-{guid:N}".ToUpperInvariant();
    }

    private static string ReadOsVersion()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var os = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (os is not null)
                    return $"Windows {os.GetValue("CurrentBuildNumber")} (release {os.GetValue("DisplayVersion")})";
            }
        }
        catch { }
        return Environment.OSVersion.ToString();
    }

    private static string? ReadDomain()
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName is { Length: > 0 } d
                ? d : "WORKGROUP";
        }
        catch { return "WORKGROUP"; }
    }
}
