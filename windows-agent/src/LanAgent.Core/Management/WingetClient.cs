using LanAgent.Core.Logging;

namespace LanAgent.Core.Management;

public sealed record WingetOperationResult(
    bool Success,
    string Detail,
    bool AlreadyInstalled,
    string? InstalledVersion,
    int? WingetExitCode,
    string StdOutTail,
    string StdErrTail)
{
    public static WingetOperationResult Fail(string detail, int? exitCode = null, string? outTail = null, string? errTail = null)
        => new(false, detail, false, null, exitCode, outTail ?? "", errTail ?? "");
}

public sealed record AppPackageStatus(bool Installed, string? Version, string Source, string Detail);

/// <summary>Winget facade. All operations are idempotent: current state is checked first.</summary>
public interface IWingetClient
{
    Task<WingetOperationResult> InstallAsync(string packageId, string scope = "machine", CancellationToken ct = default);
    Task<WingetOperationResult> UninstallAsync(string packageId, CancellationToken ct = default);
    Task<WingetOperationResult> UpgradeAsync(string packageId, CancellationToken ct = default);
    Task<AppPackageStatus> CheckAsync(string packageId, CancellationToken ct = default);
    string? LastLocateError { get; }
}

public sealed class WingetClient : IWingetClient
{
    private readonly IAgentLog _log;
    private string? _cachedPath;
    private string? _lastLocateError;

    private const int NotInstalledExitCode = unchecked((int)0x8A15002B); // -1978335189

    public WingetClient(IAgentLog log) => _log = log;

    public string? LastLocateError => _lastLocateError;

    /// <summary>Locates winget.exe: PATH first, then well-known WindowsApps locations.</summary>
    public string? LocateWinget()
    {
        if (_cachedPath is not null && File.Exists(_cachedPath)) return _cachedPath;

        // 1) PATH
        var pathResult = ProcessRunner.RunAsync(
            OperatingSystem.IsWindows() ? "where.exe" : "which",
            "winget",
            timeoutMs: 10_000).GetAwaiter().GetResult();
        if (pathResult.ExitCode == 0)
        {
            string? first = pathResult.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(p => p.EndsWith("winget.exe", StringComparison.OrdinalIgnoreCase) || p.EndsWith("winget", StringComparison.OrdinalIgnoreCase));
            if (first is not null && File.Exists(first)) { _cachedPath = first; return _cachedPath; }
        }

        // 2) Well-known locations (service context may not have the per-user alias on PATH)
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsApps\winget.exe")
        };
        foreach (string candidate in candidates)
        {
            try { if (File.Exists(candidate)) { _cachedPath = candidate; return _cachedPath; } }
            catch { /* access denied — keep looking */ }
        }

        // 3) Glob the machine WindowsApps install of the App Installer package
        try
        {
            string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            foreach (string dir in Directory.EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*"))
            {
                string exe = Path.Combine(dir, "winget.exe");
                if (File.Exists(exe)) { _cachedPath = exe; return _cachedPath; }
            }
        }
        catch (Exception ex)
        {
            _lastLocateError = $"WindowsApps enumeration failed: {ex.Message}";
        }

        _lastLocateError ??= "winget.exe not found (App Installer not deployed for this account?)";
        _log.Warn("winget", "winget not located", new { error = _lastLocateError });
        return null;
    }

    public async Task<AppPackageStatus> CheckAsync(string packageId, CancellationToken ct = default)
    {
        // Registry first (fast, no external process): matches by display name heuristics.
        foreach (var app in InstalledAppsReader.Read())
        {
            if (app.Name.Contains(packageId, StringComparison.OrdinalIgnoreCase))
                return new AppPackageStatus(true, app.Version, "registry", app.Name);
        }

        string? winget = LocateWinget();
        if (winget is null)
            return new AppPackageStatus(false, null, "none", LastLocateError ?? "winget unavailable");

        var result = await ProcessRunner.RunAsync(winget,
            $"list --exact --id {packageId} --accept-source-agreements --disable-interactivity",
            timeoutMs: 60_000, outerCt: ct).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            string? version = ParseWingetListVersion(result.StdOut, packageId);
            return new AppPackageStatus(true, version, "winget", "listed by winget");
        }
        if (result.ExitCode == NotInstalledExitCode)
            return new AppPackageStatus(false, null, "winget", "no installed package found");
        return new AppPackageStatus(false, null, "winget", $"winget list exit {result.ExitCode}: {Tail(result.StdErr, 300)}");
    }

    public async Task<WingetOperationResult> InstallAsync(string packageId, string scope = "machine", CancellationToken ct = default)
    {
        AppPackageStatus before = await CheckAsync(packageId, ct).ConfigureAwait(false);
        if (before.Installed)
        {
            _log.Info("winget", "install skipped: already installed", new { package_id = packageId, version = before.Version });
            return new WingetOperationResult(true, "already installed", true, before.Version, 0, "", "");
        }

        string? winget = LocateWinget();
        if (winget is null) return WingetOperationResult.Fail("winget not available: " + LastLocateError);

        var result = await ProcessRunner.RunAsync(winget,
            $"install --exact --id {packageId} --silent --scope {scope} --no-upgrade " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity",
            timeoutMs: 20 * 60_000, outerCt: ct).ConfigureAwait(false);

        AppPackageStatus after = await CheckAsync(packageId, ct).ConfigureAwait(false);
        _log.Info("winget", "install finished", new { package_id = packageId, exit_code = result.ExitCode, verified_installed = after.Installed });
        return new WingetOperationResult(
            Success: after.Installed,
            Detail: after.Installed ? (result.ExitCode == 0 ? "installed" : "installed (winget exit " + result.ExitCode + ")") : $"winget exit {result.ExitCode}: {Tail(result.StdErr, 300)}",
            AlreadyInstalled: false,
            InstalledVersion: after.Version,
            WingetExitCode: result.ExitCode,
            StdOutTail: Tail(result.StdOut, 2000),
            StdErrTail: Tail(result.StdErr, 2000));
    }

    public async Task<WingetOperationResult> UninstallAsync(string packageId, CancellationToken ct = default)
    {
        AppPackageStatus before = await CheckAsync(packageId, ct).ConfigureAwait(false);
        if (!before.Installed)
        {
            _log.Info("winget", "uninstall skipped: not installed", new { package_id = packageId });
            return new WingetOperationResult(true, "not installed (no-op)", false, null, 0, "", "");
        }

        string? winget = LocateWinget();
        if (winget is null) return WingetOperationResult.Fail("winget not available: " + LastLocateError);

        var result = await ProcessRunner.RunAsync(winget,
            $"uninstall --exact --id {packageId} --silent --disable-interactivity",
            timeoutMs: 20 * 60_000, outerCt: ct).ConfigureAwait(false);

        AppPackageStatus after = await CheckAsync(packageId, ct).ConfigureAwait(false);
        return new WingetOperationResult(
            Success: !after.Installed,
            Detail: !after.Installed ? "uninstalled" : $"winget exit {result.ExitCode}: {Tail(result.StdErr, 300)}",
            AlreadyInstalled: after.Installed,
            InstalledVersion: after.Version,
            WingetExitCode: result.ExitCode,
            StdOutTail: Tail(result.StdOut, 2000),
            StdErrTail: Tail(result.StdErr, 2000));
    }

    public async Task<WingetOperationResult> UpgradeAsync(string packageId, CancellationToken ct = default)
    {
        AppPackageStatus before = await CheckAsync(packageId, ct).ConfigureAwait(false);
        if (!before.Installed)
            return WingetOperationResult.Fail("package not installed; nothing to upgrade");

        string? winget = LocateWinget();
        if (winget is null) return WingetOperationResult.Fail("winget not available: " + LastLocateError);

        var result = await ProcessRunner.RunAsync(winget,
            $"upgrade --exact --id {packageId} --silent --include-unknown " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity",
            timeoutMs: 30 * 60_000, outerCt: ct).ConfigureAwait(false);

        // winget upgrade exits non-zero with "No available upgrade" style output when current.
        AppPackageStatus after = await CheckAsync(packageId, ct).ConfigureAwait(false);
        bool upToDate = result.ExitCode != 0 && (result.StdOut + result.StdErr).Contains("No available upgrade", StringComparison.OrdinalIgnoreCase);
        bool success = result.ExitCode == 0 || upToDate || after.Installed;
        return new WingetOperationResult(
            Success: success,
            Detail: upToDate ? "already up to date" : (result.ExitCode == 0 ? "upgraded" : $"winget exit {result.ExitCode}: {Tail(result.StdErr, 300)}"),
            AlreadyInstalled: false,
            InstalledVersion: after.Version ?? before.Version,
            WingetExitCode: result.ExitCode,
            StdOutTail: Tail(result.StdOut, 2000),
            StdErrTail: Tail(result.StdErr, 2000));
    }

    /// <summary>winget list output: header lines then a table whose last-but-one column is the version.</summary>
    private static string? ParseWingetListVersion(string stdout, string packageId)
    {
        foreach (string line in stdout.Split('\n'))
        {
            if (line.IndexOf(packageId, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Expected columns: Name, Id, Version, Available, Source → version is 3rd from end or 2nd
            if (parts.Length >= 3)
            {
                for (int i = parts.Length - 1; i >= 1; i--)
                {
                    if (parts[i].IndexOf(packageId, StringComparison.OrdinalIgnoreCase) >= 0 && i + 1 < parts.Length)
                        return parts[i + 1];
                }
            }
        }
        return null;
    }

    internal static string Tail(string value, int maxChars) =>
        string.IsNullOrEmpty(value) ? "" :
        value.Length <= maxChars ? value.Trim() :
        value[^maxChars..].Trim();
}

public sealed record InstalledApp(string Name, string? Version, string? Publisher, string Source);

/// <summary>Reads installed applications from the documented registry uninstall keys.</summary>
public static class InstalledAppsReader
{
    private static readonly string[] UninstallPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    public static List<InstalledApp> Read(string? nameContains = null)
    {
        var apps = new List<InstalledApp>();
        if (!OperatingSystem.IsWindows()) return apps;

        var roots = new List<RegistryKey>();
        try { roots.Add(Registry.LocalMachine); } catch { /* not windows */ }
        try { roots.Add(Registry.CurrentUser); } catch { /* not windows */ }

        foreach (var root in roots)
        {
            foreach (string path in UninstallPaths)
            {
                using var parent = root.OpenSubKey(path);
                if (parent is null) continue;
                foreach (string subKeyName in parent.GetSubKeyNames())
                {
                    try
                    {
                        using var key = parent.OpenSubKey(subKeyName);
                        if (key is null) continue;
                        string? name = key.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (nameContains is not null && name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        apps.Add(new InstalledApp(
                            Name: name,
                            Version: key.GetValue("DisplayVersion") as string,
                            Publisher: key.GetValue("Publisher") as string,
                            Source: "registry"));
                    }
                    catch
                    {
                        // A broken uninstall key must not break the whole enumeration.
                    }
                }
            }
        }

        apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return apps;
    }
}
