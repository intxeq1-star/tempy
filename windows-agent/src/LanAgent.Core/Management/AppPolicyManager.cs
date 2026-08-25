using System.Xml.Linq;
using LanAgent.Core.Logging;

namespace LanAgent.Core.Management;

public sealed record AppPolicyRule(string Path, string Action);

public sealed record AppPolicyResult(bool Success, string Mode, int RulesApplied, string? Error, string Detail);

/// <summary>Application restrictions via documented Windows policy (AppLocker). Never process-killing loops.</summary>
public interface IAppPolicyManager
{
    Task<AppPolicyResult> ApplyAsync(string mode, List<AppPolicyRule> rules, CancellationToken ct = default);
    Task<AppPolicyResult> CheckAsync(CancellationToken ct = default);
}

public sealed class AppPolicyManager : IAppPolicyManager
{
    private const string RulePrefix = "LanAgent-";

    private readonly IAgentLog _log;

    public AppPolicyManager(IAgentLog log) => _log = log;

    public async Task<AppPolicyResult> ApplyAsync(string mode, List<AppPolicyRule> rules, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return new AppPolicyResult(false, mode, 0, "app policy requires Windows", "unsupported OS");

        mode = mode.Trim().ToLowerInvariant();
        if (mode is not ("applocker" or "none"))
            return new AppPolicyResult(false, mode, 0, $"unknown mode '{mode}'", "validation");

        // Detect AppLocker cmdlets (Enterprise/Education editions only).
        var detect = await RunPowerShellAsync(
            "$ErrorActionPreference='Stop'; if (Get-Command Set-AppLockerPolicy -ErrorAction SilentlyContinue) { 'ok' } else { 'missing' }",
            ct).ConfigureAwait(false);
        if (detect.ExitCode != 0 || !detect.StdOut.Contains("ok"))
            return new AppPolicyResult(false, mode, 0,
                "Set-AppLockerPolicy is not available on this Windows edition (requires Enterprise/Education)",
                "unsupported edition");

        string xml = BuildPolicyXml(mode == "applocker" ? rules : new List<AppPolicyRule>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"lanagent-applocker-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tempFile, xml, ct).ConfigureAwait(false);
            var apply = await RunPowerShellAsync(
                $"$ErrorActionPreference='Stop'; Set-AppLockerPolicy -XmlPolicy '{tempFile}' -ErrorAction Stop; 'applied'",
                ct).ConfigureAwait(false);
            if (apply.ExitCode != 0 || !apply.StdOut.Contains("applied"))
                return new AppPolicyResult(false, mode, 0, $"Set-AppLockerPolicy failed: {WingetClient.Tail(apply.StdErr, 400)}", apply.StdOut.Trim());

            _log.Info("app_policy", "applocker policy applied", new { mode, rules = rules.Count });
            return new AppPolicyResult(true, mode, mode == "applocker" ? rules.Count : 0, null, "applied");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    public async Task<AppPolicyResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return new AppPolicyResult(false, "unknown", 0, "app policy requires Windows", "unsupported OS");

        var detect = await RunPowerShellAsync(
            "$ErrorActionPreference='Stop'; if (Get-Command Get-AppLockerPolicy -ErrorAction SilentlyContinue) { 'ok' } else { 'missing' }",
            ct).ConfigureAwait(false);
        if (detect.ExitCode != 0 || !detect.StdOut.Contains("ok"))
            return new AppPolicyResult(false, "unknown", 0, "AppLocker cmdlets not available", "unsupported edition");

        var effective = await RunPowerShellAsync(
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-AppLockerPolicy -Effective -Xml | Out-String", ct).ConfigureAwait(false);
        if (effective.ExitCode != 0)
            return new AppPolicyResult(false, "unknown", 0, $"Get-AppLockerPolicy failed: {WingetClient.Tail(effective.StdErr, 300)}", effective.StdOut.Trim());

        try
        {
            var doc = XDocument.Parse(effective.StdOut);
            var agentRules = doc.Descendants("FilePathRule")
                .Where(r => r.Attribute("Name")?.Value.StartsWith(RulePrefix, StringComparison.Ordinal) == true)
                .Select(r => new
                {
                    Name = r.Attribute("Name")?.Value,
                    Action = r.Attribute("Action")?.Value ?? "Allow",
                    Path = r.Descendants("FilePathCondition").Attributes("Path").Select(a => a.Value).FirstOrDefault()
                })
                .ToList();

            string mode = agentRules.Count > 0 ? "applocker" : "none";
            var detail = string.Join("; ", agentRules.Select(r => $"{r.Action}:{r.Path}"));
            return new AppPolicyResult(true, mode, agentRules.Count, null, detail);
        }
        catch (Exception ex)
        {
            return new AppPolicyResult(false, "unknown", 0, $"parse failed: {ex.Message}", effective.StdOut.Trim());
        }
    }

    /// <summary>AppLocker XML: allow-by-default, deny-by-path (the standard "block specific apps" pattern).</summary>
    internal static string BuildPolicyXml(List<AppPolicyRule> rules)
    {
        var ns = XNamespace.None;
        var root = new XElement("AppLockerPolicy", new XAttribute("Version", "1"));
        var collection = new XElement("RuleCollection", new XAttribute("Type", "Exe"),
            new XAttribute("EnforcementMode", rules.Count > 0 ? "Enabled" : "NotConfigured"));

        collection.Add(new XElement("FilePathRule",
            new XAttribute("Id", Guid.NewGuid().ToString("D")),
            new XAttribute("Name", RulePrefix + "Default-Allow"),
            new XAttribute("Description", "LanAgent managed: allow everything not explicitly denied"),
            new XAttribute("UserOrGroupSid", "S-1-1-0"),
            new XAttribute("Action", "Allow"),
            new XElement("Conditions",
                new XElement("FilePathCondition", new XAttribute("Path", "*")))));

        int index = 0;
        foreach (var rule in rules)
        {
            string action = rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase) ? "Allow" : "Deny";
            collection.Add(new XElement("FilePathRule",
                new XAttribute("Id", Guid.NewGuid().ToString("D")),
                new XAttribute("Name", $"{RulePrefix}{action}-{index++:00}"),
                new XAttribute("Description", "LanAgent managed rule"),
                new XAttribute("UserOrGroupSid", "S-1-1-0"),
                new XAttribute("Action", action),
                new XElement("Conditions",
                    new XElement("FilePathCondition", new XAttribute("Path", rule.Path)))));
        }

        root.Add(collection);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static Task<ProcessResult> RunPowerShellAsync(string command, CancellationToken ct)
        => ProcessRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "'") + "\"",
            timeoutMs: 60_000,
            outerCt: ct);
}
