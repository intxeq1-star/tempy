using Microsoft.Win32;
using LanAgent.Core.Logging;

namespace LanAgent.Core.Management;

public sealed record BrowserPolicyState(string Browser, string Feature, string Mode, int? RawValue, bool Installed)
{
    /// <summary>Mode ∈ enabled | disabled | forced | unmanaged (PROTOCOL_CONTRACT §6).</summary>
    public static string ModeFromRaw(int? raw) => raw switch
    {
        0 => "enabled",
        1 => "disabled",
        2 => "forced",
        _ => "unmanaged"
    };
    public static int? RawFromMode(string? mode) => mode switch
    {
        "enabled" => 0,
        "disabled" => 1,
        "forced" => 2,
        "unmanaged" => null,
        _ => null
    };
}

public sealed record BrowserApplyResult(bool Success, string? Error, Dictionary<string, object?> Details);

/// <summary>Browser restriction policies via documented HKLM policy registry keys.</summary>
public interface IBrowserPolicyManager
{
    BrowserPolicyState GetChromeIncognito();
    BrowserPolicyState GetEdgeInPrivate();
    BrowserApplyResult Apply(string? chromeIncognitoMode, string? edgeInPrivateMode);
    BrowserApplyResult Remove(IEnumerable<string> browsers);
    Dictionary<string, object?> Check();
}

public sealed class BrowserPolicyManager : IBrowserPolicyManager
{
    private const string ChromePolicyKey = @"SOFTWARE\Policies\Google\Chrome";
    private const string ChromeValueName = "IncognitoModeAvailability";
    private const string EdgePolicyKey = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string EdgeValueName = "InPrivateModeAvailability";

    private readonly IAgentLog _log;

    public BrowserPolicyManager(IAgentLog log) => _log = log;

    public BrowserPolicyState GetChromeIncognito()
        => ReadPolicy("chrome", "incognito", ChromePolicyKey, ChromeValueName, IsChromeInstalled());

    public BrowserPolicyState GetEdgeInPrivate()
        => ReadPolicy("edge", "inprivate", EdgePolicyKey, EdgeValueName, IsEdgeInstalled());

    public BrowserApplyResult Apply(string? chromeIncognitoMode, string? edgeInPrivateMode)
    {
        if (!OperatingSystem.IsWindows())
            return new BrowserApplyResult(false, "browser policy requires Windows", new Dictionary<string, object?>());

        var details = new Dictionary<string, object?>();
        bool allOk = true;

        if (chromeIncognitoMode is not null)
        {
            string mode = NormalizeMode(chromeIncognitoMode);
            bool ok = WritePolicy(ChromePolicyKey, ChromeValueName, mode, details, "chrome", "incognito");
            allOk &= ok;
        }

        if (edgeInPrivateMode is not null)
        {
            string mode = NormalizeMode(edgeInPrivateMode);
            bool ok = WritePolicy(EdgePolicyKey, EdgeValueName, mode, details, "edge", "inprivate");
            allOk &= ok;
        }

        if (chromeIncognitoMode is null && edgeInPrivateMode is null)
            return new BrowserApplyResult(false, "no modes supplied", details);

        _log.Info("browser_policy", "browser policy applied", new { chrome = chromeIncognitoMode, edge = edgeInPrivateMode, ok = allOk });
        return new BrowserApplyResult(allOk, allOk ? null : "one or more policies failed", details);
    }

    public BrowserApplyResult Remove(IEnumerable<string> browsers)
    {
        if (!OperatingSystem.IsWindows())
            return new BrowserApplyResult(false, "browser policy requires Windows", new Dictionary<string, object?>());

        var details = new Dictionary<string, object?>();
        var removed = new List<string>();
        foreach (string browser in browsers)
        {
            if (browser.Equals("chrome", StringComparison.OrdinalIgnoreCase))
            {
                DeletePolicy(ChromePolicyKey, ChromeValueName, details, "chrome.incognito");
                removed.Add("chrome.incognito");
            }
            else if (browser.Equals("edge", StringComparison.OrdinalIgnoreCase))
            {
                DeletePolicy(EdgePolicyKey, EdgeValueName, details, "edge.inprivate");
                removed.Add("edge.inprivate");
            }
        }
        details["removed"] = removed;
        _log.Info("browser_policy", "browser policy removed", new { removed });
        return new BrowserApplyResult(true, null, details);
    }

    public Dictionary<string, object?> Check()
    {
        var chrome = GetChromeIncognito();
        var edge = GetEdgeInPrivate();
        return new Dictionary<string, object?>
        {
            ["chrome"] = new Dictionary<string, object?>
            {
                ["incognito"] = chrome.Mode,
                ["installed"] = chrome.Installed,
                ["raw_value"] = chrome.RawValue
            },
            ["edge"] = new Dictionary<string, object?>
            {
                ["inprivate"] = edge.Mode,
                ["installed"] = edge.Installed,
                ["raw_value"] = edge.RawValue
            }
        };
    }

    // ---------------------------------------------------------------- helpers

    private static BrowserPolicyState ReadPolicy(string browser, string feature, string keyPath, string valueName, bool installed)
    {
        if (!OperatingSystem.IsWindows())
            return new BrowserPolicyState(browser, feature, "unmanaged", null, installed);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            object? raw = key?.GetValue(valueName);
            int? rawInt = raw as int?;
            return new BrowserPolicyState(browser, feature, BrowserPolicyState.ModeFromRaw(rawInt), rawInt, installed);
        }
        catch
        {
            return new BrowserPolicyState(browser, feature, "unmanaged", null, installed);
        }
    }

    private bool WritePolicy(string keyPath, string valueName, string mode, Dictionary<string, object?> details, string browser, string feature)
    {
        try
        {
            int? desired = BrowserPolicyState.RawFromMode(mode);
            var current = ReadPolicy(browser, feature, keyPath, valueName, true);
            if (current.Mode == mode)
            {
                details[browser] = new Dictionary<string, object?> { ["policy"] = feature, ["value"] = mode, ["applied"] = true, ["note"] = "already compliant" };
                return true;
            }

            using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
            if (key is null)
            {
                details[browser] = new Dictionary<string, object?> { ["policy"] = feature, ["value"] = mode, ["applied"] = false, ["error"] = "cannot create policy key" };
                return false;
            }
            if (desired.HasValue) key.SetValue(valueName, desired.Value, RegistryValueKind.DWord);
            else key.DeleteValue(valueName, throwOnMissingValue: false);

            // Verify read-back
            var after = ReadPolicy(browser, feature, keyPath, valueName, true);
            bool ok = after.Mode == mode;
            details[browser] = new Dictionary<string, object?> { ["policy"] = feature, ["value"] = after.Mode, ["applied"] = ok };
            return ok;
        }
        catch (Exception ex)
        {
            _log.Error("browser_policy", "failed to write policy", $"{browser}.{feature}: {ex.Message}", ex);
            details[browser] = new Dictionary<string, object?> { ["policy"] = feature, ["value"] = mode, ["applied"] = false, ["error"] = ex.Message };
            return false;
        }
    }

    private void DeletePolicy(string keyPath, string valueName, Dictionary<string, object?> details, string label)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
            _log.Info("browser_policy", "policy value removed", new { policy = label });
        }
        catch (Exception ex)
        {
            _log.Error("browser_policy", "failed to remove policy", $"{label}: {ex.Message}", ex);
        }
    }

    private static string NormalizeMode(string mode)
    {
        mode = mode.Trim().ToLowerInvariant();
        return mode is "enabled" or "disabled" or "forced" or "unmanaged" ? mode : "unmanaged";
    }

    internal static bool IsChromeInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        string[] paths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
        };
        return paths.Any(File.Exists);
    }

    internal static bool IsEdgeInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        string[] paths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
        };
        return paths.Any(File.Exists);
    }
}
