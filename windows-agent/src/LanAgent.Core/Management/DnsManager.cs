using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanAgent.Core.Logging;

namespace LanAgent.Core.Management;

public sealed record AdapterDnsInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string NicType { get; init; }
    public required bool IsPhysical { get; init; }
    public required List<string> DnsServers { get; init; }
}

public sealed record DnsApplyOutcome
{
    public required List<Dictionary<string, object?>> Adapters { get; init; }
    public required int Changed { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
}

public sealed record DnsCheckOutcome
{
    public required List<Dictionary<string, object?>> Adapters { get; init; }
    public required List<string> Expected { get; init; }
    public required bool Compliant { get; init; }
}

/// <summary>DNS configuration management for physical LAN adapters (idempotent, verified).</summary>
public interface IDnsManager
{
    List<AdapterDnsInfo> GetRelevantAdapters(bool includeWireless);
    Task<DnsApplyOutcome> ApplyAsync(List<string> servers, bool includeWireless, bool resetToDhcp);
    DnsCheckOutcome Check(List<string> expectedServers, bool includeWireless);
}

public sealed class DnsManager : IDnsManager
{
    private readonly IAgentLog _log;

    private static readonly string[] VirtualKeywords =
    {
        "virtual", "hyper-v", "vmware", "virtualbox", "loopback", "tap-", "vpn",
        "wan miniport", "bluetooth", "docker", "ws-", "veth", "tailscale", "zero tier", "openvpn"
    };

    public DnsManager(IAgentLog log) => _log = log;

    public List<AdapterDnsInfo> GetRelevantAdapters(bool includeWireless)
    {
        var result = new List<AdapterDnsInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            bool isEthernet = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
            bool isWireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            if (!isEthernet && !(includeWireless && isWireless)) continue;

            string haystack = (nic.Description + " " + nic.Name).ToLowerInvariant();
            bool isVirtual = VirtualKeywords.Any(k => haystack.Contains(k));
            if (isVirtual) continue;

            var dns = nic.GetIPProperties().DnsServers
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();

            result.Add(new AdapterDnsInfo
            {
                Name = nic.Name,
                Description = nic.Description,
                NicType = nic.NetworkInterfaceType.ToString(),
                IsPhysical = true,
                DnsServers = dns
            });
        }
        return result;
    }

    public async Task<DnsApplyOutcome> ApplyAsync(List<string> servers, bool includeWireless, bool resetToDhcp)
    {
        if (!OperatingSystem.IsWindows())
            return new DnsApplyOutcome { Adapters = new(), Changed = 0, Success = false, Error = "DNS management requires Windows" };

        servers = servers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (!resetToDhcp && servers.Count == 0)
            return new DnsApplyOutcome { Adapters = new(), Changed = 0, Success = false, Error = "no DNS servers supplied" };

        var adapters = GetRelevantAdapters(includeWireless);
        var perAdapter = new List<Dictionary<string, object?>>();
        int changed = 0;
        bool allOk = true;

        foreach (var adapter in adapters)
        {
            var before = adapter.DnsServers.ToList();
            bool needsChange = resetToDhcp
                ? before.Count > 0
                : !before.SequenceEqual(servers);

            string status = "SUCCESS";
            string detail = needsChange ? "changed" : "already compliant";

            if (needsChange)
            {
                var run = await RunNetshAsync(BuildSetArgs(adapter.Name, servers, resetToDhcp)).ConfigureAwait(false);
                if (run.ExitCode != 0)
                {
                    status = "FAILED";
                    allOk = false;
                    detail = $"netsh exit {run.ExitCode}: {WingetClient.Tail(run.StdErr, 300)}";
                    _log.Error("dns", "netsh set failed", new { adapter = adapter.Name, error = detail });
                }
                else
                {
                    // Extra servers beyond the first need explicit add calls.
                    for (int i = 1; i < servers.Count; i++)
                    {
                        var add = await RunNetshAsync($"interface ipv4 add dnsserver name=\"{Escape(adapter.Name)}\" addr={servers[i]} index={i + 1}").ConfigureAwait(false);
                        if (add.ExitCode != 0 && !add.StdErr.Contains("already", StringComparison.OrdinalIgnoreCase))
                        {
                            _log.Warn("dns", "secondary DNS add failed", new { adapter = adapter.Name, server = servers[i], exit = add.ExitCode });
                        }
                    }
                }
            }

            // Verify by reading back the effective configuration.
            var after = GetCurrentDns(adapter.Name);
            bool verified = resetToDhcp ? after.Count == 0 : after.SequenceEqual(servers) || after.FirstOrDefault() == servers.FirstOrDefault();
            if (status == "SUCCESS" && !verified)
            {
                status = "FAILED";
                allOk = false;
                detail = $"verification mismatch: expected [{string.Join(",", resetToDhcp ? new List<string>() : servers)}] got [{string.Join(",", after)}]";
                _log.Error("dns", "post-apply verification failed", new { adapter = adapter.Name, expected = servers, actual = after });
            }
            else if (status == "SUCCESS" && needsChange)
            {
                changed++;
                _log.Info("dns", "dns applied", new { adapter = adapter.Name, before, after });
            }

            perAdapter.Add(new Dictionary<string, object?>
            {
                ["name"] = adapter.Name,
                ["status"] = status,
                ["detail"] = detail,
                ["before"] = before,
                ["after"] = after
            });
        }

        return new DnsApplyOutcome { Adapters = perAdapter, Changed = changed, Success = allOk, Error = allOk ? null : "one or more adapters failed" };
    }

    public DnsCheckOutcome Check(List<string> expectedServers, bool includeWireless)
    {
        var adapters = GetRelevantAdapters(includeWireless);
        var list = new List<Dictionary<string, object?>>();
        bool compliant = true;
        foreach (var adapter in adapters)
        {
            bool ok = adapter.DnsServers.SequenceEqual(expectedServers);
            if (!ok) compliant = false;
            list.Add(new Dictionary<string, object?>
            {
                ["name"] = adapter.Name,
                ["dns_servers"] = adapter.DnsServers,
                ["compliant"] = ok
            });
        }
        return new DnsCheckOutcome { Adapters = list, Expected = expectedServers, Compliant = compliant };
    }

    private static string BuildSetArgs(string adapterName, List<string> servers, bool resetToDhcp)
    {
        if (resetToDhcp)
            return $"interface ipv4 set dnsservers name=\"{Escape(adapterName)}\" dhcp";
        return $"interface ipv4 set dnsservers name=\"{Escape(adapterName)}\" static {servers[0]} primary validate=no";
    }

    private static string Escape(string adapterName) => adapterName.Replace("\"", "");

    private static List<string> GetCurrentDns(string adapterName)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name == adapterName);
            if (nic is null) return new List<string>();
            return nic.GetIPProperties().DnsServers
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static Task<ProcessResult> RunNetshAsync(string args)
        => ProcessRunner.RunAsync("netsh.exe", args, timeoutMs: 30_000);
}
