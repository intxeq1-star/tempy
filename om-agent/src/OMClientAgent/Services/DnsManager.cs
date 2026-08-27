using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public sealed class DnsManager
{
    private readonly AgentConfigService _config;
    private readonly LocalDatabase _db;
    private readonly ILogger<DnsManager> _logger;

    public DnsManager(AgentConfigService config, LocalDatabase db, ILogger<DnsManager> logger)
    {
        _config = config;
        _db = db;
        _logger = logger;
    }

    public async Task<CommandResult> ApplyDnsAsync(string dnsServer, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return new CommandResult { Status = JobStatus.Success, ExitCode = 0, StandardOutput = "DNS apply simulated (non-Windows)." };

        try
        {
            BackupCurrent();
            foreach (var adapter in GetActiveAdapters())
            {
                var (exit, output) = await RunAsync("netsh.exe",
                    $"interface ipv4 set dnsservers name=\"{adapter}\" static {dnsServer} primary", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                if (exit != 0)
                    return new CommandResult { Status = JobStatus.Failed, ExitCode = exit, StandardError = output };
            }

            var ok = await VerifyAsync(dnsServer, ct).ConfigureAwait(false);
            if (!ok)
            {
                RestoreBackup();
                return new CommandResult { Status = JobStatus.Failed, ExitCode = 1, StandardError = "DNS verification failed; previous configuration was restored." };
            }

            _db.SetSetting("DesiredDns", dnsServer);
            _db.SetSetting("ActualDns", GetCurrentDns() ?? string.Empty);
            _db.SetSetting("DnsVerification", "OK");
            return new CommandResult { Status = JobStatus.Success, ExitCode = 0, StandardOutput = $"DNS set to {dnsServer} and verified." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DNS apply failed.");
            return new CommandResult { Status = JobStatus.Failed, ExitCode = 1, StandardError = ex.Message };
        }
    }

    public string? GetCurrentDns()
    {
        if (!OperatingSystem.IsWindows()) return "127.0.0.1";
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                if (props.DnsAddresses.Count > 0)
                    return props.DnsAddresses[0].ToString();
            }
        }
        catch { }
        return null;
    }

    private async Task<bool> VerifyAsync(string dnsServer, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            foreach (var host in new[] { "www.google.com", "om-server.local" })
            {
                try
                {
                    var entries = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
                    if (entries.Length > 0) return true;
                }
                catch { }
            }
            return false;
        }
        catch { return false; }
    }

    private void BackupCurrent()
    {
        var current = GetCurrentDns();
        _db.SetSetting("DnsBackup", current ?? string.Empty);
        _logger.LogInformation("Backed up DNS configuration: {Dns}", current);
    }

    private void RestoreBackup()
    {
        var backup = _db.GetSetting("DnsBackup");
        if (string.IsNullOrWhiteSpace(backup)) return;
        foreach (var adapter in GetActiveAdapters())
        {
            RunAsync("netsh.exe", $"interface ipv4 set dnsservers name=\"{adapter}\" static {backup} primary", TimeSpan.FromSeconds(30), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        _logger.LogWarning("Restored previous DNS configuration: {Dns}", backup);
    }

    private List<string> GetActiveAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                             ni.GetIPProperties().GatewayAddresses.Count > 0)
                .Select(ni => ni.Name)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private static async Task<(int exit, string output)> RunAsync(string file, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = file, Arguments = args, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
            var so = p.StandardOutput.ReadToEndAsync(ct);
            var se = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
            return (p.ExitCode, (await so.ConfigureAwait(false)) + "\n" + (await se.ConfigureAwait(false)));
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
