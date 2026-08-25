using System.Text.Json;
using LanAgent.Core.Logging;
using LanAgent.Core.Management;

namespace LanAgent.Core.Sync;

/// <summary>Applies a desired_state payload; implemented by <see cref="DesiredStateApplier"/> and faked in tests.</summary>
public interface IDesiredStateApplier
{
    Task<List<SyncItem>> ApplyAsync(JsonElement? desired);
}

/// <summary>
/// Applies the sections of a SYNC_PAYLOAD desired_state to the local machine (PROTOCOL_CONTRACT §6).
/// Every section is idempotent; sections fail independently; nothing outside the desired state is touched.
/// </summary>
public sealed class DesiredStateApplier : IDesiredStateApplier
{
    private readonly IDnsManager _dns;
    private readonly IBrowserPolicyManager _browser;
    private readonly IAppPolicyManager _appPolicy;
    private readonly IWingetClient _winget;
    private readonly IAgentLog _log;

    public DesiredStateApplier(
        IDnsManager dns,
        IBrowserPolicyManager browser,
        IAppPolicyManager appPolicy,
        IWingetClient winget,
        IAgentLog log)
    {
        _dns = dns;
        _browser = browser;
        _appPolicy = appPolicy;
        _winget = winget;
        _log = log;
    }

    public async Task<List<SyncItem>> ApplyAsync(JsonElement? desired)
    {
        var items = new List<SyncItem>();
        if (desired is not { ValueKind: JsonValueKind.Object })
            return items;

        JsonElement root = desired.Value;

        // ---- dns ----
        if (TrySection(root, "dns", out var dns))
        {
            items.Add(await ApplyDnsAsync(dns).ConfigureAwait(false));
        }

        // ---- browser_policy ----
        if (TrySection(root, "browser_policy", out var browser))
        {
            string? chrome = GetString(browser, "chrome", "incognito");
            string? edge = GetString(browser, "edge", "inprivate");
            if (chrome is not null || edge is not null)
            {
                var result = _browser.Apply(chrome, edge);
                string detail = chrome is null ? "" : $"chrome.incognito={chrome}; " + (edge is null ? "" : $"edge.inprivate={edge}");
                items.Add(new SyncItem("browser_policy", result.Success ? "SUCCESS" : "FAILED",
                    (result.Success ? "applied: " : "failed: ") + detail.TrimEnd(' ', ';') + (result.Error is null ? "" : $" ({result.Error})")));
            }
        }

        // ---- app_policy ----
        if (TrySection(root, "app_policy", out var appPolicy))
        {
            string mode = GetString(appPolicy, "mode") ?? "none";
            var rules = new List<AppPolicyRule>();
            if (appPolicy.TryGetProperty("rules", out var ruleArr) && ruleArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in ruleArr.EnumerateArray())
                {
                    string? path = GetString(rule, "path");
                    string? action = GetString(rule, "action");
                    if (!string.IsNullOrWhiteSpace(path)) rules.Add(new AppPolicyRule(path!, action ?? "deny"));
                }
            }
            try
            {
                var result = await _appPolicy.ApplyAsync(mode, rules).ConfigureAwait(false);
                items.Add(new SyncItem("app_policy", result.Success ? "SUCCESS" : "FAILED",
                    result.Success ? $"mode={mode} rules={result.RulesApplied}" : result.Error ?? "apply failed"));
            }
            catch (Exception ex)
            {
                items.Add(new SyncItem("app_policy", "FAILED", ex.Message));
            }
        }

        // ---- apps ----
        if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
        {
            foreach (var app in apps.EnumerateArray())
            {
                string? packageId = GetString(app, "package_id");
                string? state = GetString(app, "state");
                if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(state)) continue;

                string itemName = $"apps.{packageId}";
                try
                {
                    var outcome = state == "absent"
                        ? await _winget.UninstallAsync(packageId).ConfigureAwait(false)
                        : await _winget.InstallAsync(packageId).ConfigureAwait(false);
                    items.Add(new SyncItem(itemName, outcome.Success ? "SUCCESS" : "FAILED", outcome.Detail));
                }
                catch (Exception ex)
                {
                    items.Add(new SyncItem(itemName, "FAILED", ex.Message));
                }
            }
        }

        _log.Info("sync", "desired state applied", new { sections = items.Count, failed = items.Count(i => i.Status == "FAILED") });
        return items;
    }

    private async Task<SyncItem> ApplyDnsAsync(JsonElement dns)
    {
        try
        {
            bool enabled = GetBool(dns, "enabled", true);
            if (!enabled)
                return new SyncItem("dns", "SUCCESS", "dns management disabled by policy (no change)");

            var servers = GetStringList(dns, "servers");
            bool resetToDhcp = GetBool(dns, "reset_to_dhcp", false);
            bool includeWireless = GetBool(dns, "apply_to_wireless", false);

            if (!resetToDhcp && servers.Count == 0)
                return new SyncItem("dns", "FAILED", "policy has no dns.servers");

            var outcome = await _dns.ApplyAsync(servers, includeWireless, resetToDhcp).ConfigureAwait(false);
            string detail = string.Join(", ", outcome.Adapters.Select(a => $"{a["name"]}: {a["status"]}"));
            return new SyncItem("dns", outcome.Success ? "SUCCESS" : "FAILED",
                $"changed={outcome.Changed} [{detail}]".TrimEnd());
        }
        catch (Exception ex)
        {
            return new SyncItem("dns", "FAILED", ex.Message);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool TrySection(JsonElement root, string name, out JsonElement section)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object)
        {
            section = el;
            return true;
        }
        section = default;
        return false;
    }

    private static string? GetString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? GetString(JsonElement parent, string child, string grandChild)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(child, out var c) && c.ValueKind == JsonValueKind.Object &&
           c.TryGetProperty(grandChild, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool GetBool(JsonElement parent, string name, bool fallback)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(name, out var v) &&
           (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;

    private static List<string> GetStringList(JsonElement parent, string name)
    {
        var result = new List<string>();
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        return result;
    }
}
