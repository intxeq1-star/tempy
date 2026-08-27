using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public sealed class SoftwareManager
{
    private readonly AgentConfigService _config;
    private readonly TlsTrust _tls;
    private readonly ILogger<SoftwareManager> _logger;

    public SoftwareManager(AgentConfigService config, TlsTrust tls, ILogger<SoftwareManager> logger)
    {
        _config = config;
        _tls = tls;
        _logger = logger;
    }

    public async Task<PackageMetadata?> GetPackageAsync(string name, CancellationToken ct)
    {
        try
        {
            using var http = _tls.CreateHttpClient();
            var uri = new Uri(new Uri(_config.Current.ServerUrl.TrimEnd('/') + "/"),
                OmApi.Package + $"?name={Uri.EscapeDataString(name)}");
            using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Package '{Name}' is not an approved package (404).", name);
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("GET package '{Name}' returned {Status}.", name, response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<PackageMetadata>(OmJson.Options, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("GetPackage('{Name}') failed: {Message}", name, ex.Message);
            return null;
        }
    }

    public async Task<CommandResult> InstallPackageAsync(string name, int timeoutSeconds, CancellationToken ct)
    {
        var pkg = await GetPackageAsync(name, ct).ConfigureAwait(false);
        if (pkg is null)
            return new CommandResult { Status = JobStatus.Failed, ExitCode = 1, StandardError = $"Package '{name}' not found / not approved." };
        if (string.IsNullOrWhiteSpace(pkg.DownloadUrl) || string.IsNullOrWhiteSpace(pkg.Sha256))
            return new CommandResult { Status = JobStatus.Failed, ExitCode = 1, StandardError = "Package is missing download URL or SHA-256." };

        try
        {
            var dir = _config.Current.DataDirectory;
            Directory.CreateDirectory(dir);
            var fileName = Path.GetFileName(new Uri(pkg.DownloadUrl).AbsolutePath);
            var dest = Path.Combine(dir, fileName);

            await DownloadAndVerifyAsync(pkg.DownloadUrl, dest, pkg.Sha256, ct).ConfigureAwait(false);
            _logger.LogInformation("Downloaded & verified {Package} ({Bytes} bytes).", pkg.Name, new FileInfo(dest).Length);

            var args = string.IsNullOrWhiteSpace(pkg.InstallArgs) ? "/quiet /norestart" : pkg.InstallArgs!;
            var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : TimeSpan.FromSeconds(600);
            var (exit, output) = await RunInstallerAsync(dest, args, timeout, ct).ConfigureAwait(false);
            if (exit != 0)
                return new CommandResult { Status = JobStatus.Failed, ExitCode = exit, StandardOutput = output };

            if (VerifyInstallation(pkg))
                return new CommandResult { Status = JobStatus.Success, ExitCode = 0, StandardOutput = output };

            return new CommandResult { Status = JobStatus.Failed, ExitCode = 1, StandardOutput = output, StandardError = "Install completed but detection did not find the package." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Software install failed for {Package}", pkg.Name);
            return new CommandResult { Status = JobStatus.Failed, ExitCode = 1, StandardError = ex.Message };
        }
    }

    public async Task<CommandResult> UninstallPackageAsync(string name, int timeoutSeconds, CancellationToken ct)
    {
        var pkg = await GetPackageAsync(name, ct).ConfigureAwait(false);
        if (pkg is null)
            return new CommandResult { Status = JobStatus.Failed, ExitCode = 1, StandardError = $"Package '{name}' not found / not approved." };

        if (string.IsNullOrWhiteSpace(pkg.UninstallArgs))
            return new CommandResult { Status = JobStatus.Success, ExitCode = 0, StandardOutput = "No uninstall command provided." };

        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : TimeSpan.FromSeconds(600);
        var (exit, output) = await RunInstallerAsync("cmd.exe", "/d /s /c \"" + pkg.UninstallArgs + "\"", timeout, ct).ConfigureAwait(false);
        return new CommandResult { Status = exit == 0 ? JobStatus.Success : JobStatus.Failed, ExitCode = exit, StandardOutput = output };
    }

    public async Task DownloadAndVerifyAsync(string url, string dest, string expectedSha256, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var bytes = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SHA-256 mismatch. Expected {expectedSha256} but got {hash}.");

        await File.WriteAllBytesAsync(dest, bytes, ct).ConfigureAwait(false);
    }

    private static async Task<(int exit, string output)> RunInstallerAsync(string file, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
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
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
            return (p.ExitCode, (await so.ConfigureAwait(false)) + "\n" + (await se.ConfigureAwait(false)));
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private bool VerifyInstallation(PackageMetadata pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg.Name)) return true;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null) return false;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                var display = k?.GetValue("DisplayName")?.ToString() ?? string.Empty;
                if (display.Contains(pkg.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    public async Task<List<InstalledSoftware>> ListInstalledAsync()
    {
        var list = new List<InstalledSoftware>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            await Task.CompletedTask;
            var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null) return list;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                var name = k?.GetValue("DisplayName")?.ToString();
                var version = k?.GetValue("DisplayVersion")?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    list.Add(new InstalledSoftware { Name = name, Version = version ?? string.Empty });
            }
            if (Environment.Is64BitOperatingSystem)
            {
                var wow = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
                if (wow is not null)
                    foreach (var sub in wow.GetSubKeyNames())
                    {
                        using var k = wow.OpenSubKey(sub);
                        var name = k?.GetValue("DisplayName")?.ToString();
                        var version = k?.GetValue("DisplayVersion")?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                            list.Add(new InstalledSoftware { Name = name, Version = version ?? string.Empty });
                    }
            }
        }
        catch { }
        return list;
    }
}
