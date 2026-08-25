using System.Text.Json;
using LanAgent.Core.Configuration;
using LanAgent.Core.Management;
using LanAgent.Core.Persistence;

namespace LanAgent.Core.Commands.Handlers;

public sealed class ApplyDnsHandler : ICommandHandler
{
    private readonly IDnsManager _dns;
    private readonly AgentOptions _options;

    public ApplyDnsHandler(IDnsManager dns, AgentOptions options)
    {
        _dns = dns;
        _options = options;
    }

    public string CommandType => Protocol.CommandType.ApplyDns;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        List<string> servers = PayloadServers(context.Payload) ?? _options.Dns.Servers;
        bool includeWireless = context.PayloadBool("apply_to_wireless", _options.Dns.ApplyToWireless);
        bool resetToDhcp = context.PayloadBool("reset_to_dhcp", false);

        var outcome = await _dns.ApplyAsync(servers, includeWireless, resetToDhcp).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["adapters"] = outcome.Adapters,
            ["changed"] = outcome.Changed,
            ["servers"] = resetToDhcp ? new List<string>() : servers
        };
        return outcome.Success
            ? HandlerResult.Ok(payload)
            : HandlerResult.Fail(outcome.Error ?? "DNS apply failed", payload);
    }

    internal static List<string>? PayloadServers(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object }) return null;
        if (payload.Value.TryGetProperty("servers", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var list = arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            return list.Count > 0 ? list : null;
        }
        return null;
    }
}

public sealed class CheckDnsHandler : ICommandHandler
{
    private readonly IDnsManager _dns;
    private readonly AgentOptions _options;

    public CheckDnsHandler(IDnsManager dns, AgentOptions options)
    {
        _dns = dns;
        _options = options;
    }

    public string CommandType => Protocol.CommandType.CheckDns;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        List<string> expected = ApplyDnsHandler.PayloadServers(context.Payload) ?? _options.Dns.Servers;
        bool includeWireless = context.PayloadBool("apply_to_wireless", _options.Dns.ApplyToWireless);
        var outcome = _dns.Check(expected, includeWireless);
        return Task.FromResult(HandlerResult.Ok(new Dictionary<string, object?>
        {
            ["adapters"] = outcome.Adapters,
            ["expected"] = outcome.Expected,
            ["compliant"] = outcome.Compliant
        }));
    }
}

public sealed class ApplyBrowserPolicyHandler : ICommandHandler
{
    private readonly IBrowserPolicyManager _browser;
    private readonly IStateStore _store;

    public ApplyBrowserPolicyHandler(IBrowserPolicyManager browser, IStateStore store)
    {
        _browser = browser;
        _store = store;
    }

    public string CommandType => Protocol.CommandType.ApplyBrowserPolicy;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        string? chrome = context.PayloadString("chrome_incognito");
        string? edge = context.PayloadString("edge_inprivate");

        if (chrome is null && edge is null)
        {
            // Empty payload ⇒ apply the currently-synced desired state (PROTOCOL_CONTRACT §10).
            var policy = _store.GetPolicyState();
            if (policy.DesiredStateJson is null)
                return Task.FromResult(HandlerResult.Fail("no payload and no synced desired state"));
            try
            {
                using var doc = JsonDocument.Parse(policy.DesiredStateJson);
                if (doc.RootElement.TryGetProperty("browser_policy", out var bp))
                {
                    if (bp.TryGetProperty("chrome", out var c) && c.TryGetProperty("incognito", out var ci) && ci.ValueKind == JsonValueKind.String)
                        chrome = ci.GetString();
                    if (bp.TryGetProperty("edge", out var e) && e.TryGetProperty("inprivate", out var ei) && ei.ValueKind == JsonValueKind.String)
                        edge = ei.GetString();
                }
            }
            catch (JsonException)
            {
                return Task.FromResult(HandlerResult.Fail("stored desired state is corrupt"));
            }
            if (chrome is null && edge is null)
                return Task.FromResult(HandlerResult.Fail("desired state contains no browser policy"));
        }

        var result = _browser.Apply(chrome, edge);
        return Task.FromResult(result.Success
            ? HandlerResult.Ok(result.Details)
            : HandlerResult.Fail(result.Error ?? "browser policy apply failed", result.Details));
    }
}

public sealed class CheckBrowserPolicyHandler : ICommandHandler
{
    private readonly IBrowserPolicyManager _browser;
    public CheckBrowserPolicyHandler(IBrowserPolicyManager browser) => _browser = browser;
    public string CommandType => Protocol.CommandType.CheckBrowserPolicy;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
        => Task.FromResult(HandlerResult.Ok(_browser.Check()));
}

public sealed class RemoveBrowserPolicyHandler : ICommandHandler
{
    private readonly IBrowserPolicyManager _browser;
    public RemoveBrowserPolicyHandler(IBrowserPolicyManager browser) => _browser = browser;
    public string CommandType => Protocol.CommandType.RemoveBrowserPolicy;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        List<string> browsers = new();
        if (context.Payload is { ValueKind: JsonValueKind.Object } &&
            context.Payload.Value.TryGetProperty("browsers", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            browsers.AddRange(arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        }
        if (browsers.Count == 0) browsers.AddRange(new[] { "chrome", "edge" });

        var result = _browser.Remove(browsers);
        return Task.FromResult(result.Success
            ? HandlerResult.Ok(result.Details)
            : HandlerResult.Fail(result.Error ?? "browser policy removal failed", result.Details));
    }
}

public sealed class ApplyAppPolicyHandler : ICommandHandler
{
    private readonly IAppPolicyManager _appPolicy;

    public ApplyAppPolicyHandler(IAppPolicyManager appPolicy) => _appPolicy = appPolicy;
    public string CommandType => Protocol.CommandType.ApplyAppPolicy;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        string mode = context.PayloadString("mode") ?? "none";
        var rules = new List<AppPolicyRule>();
        if (context.Payload is { ValueKind: JsonValueKind.Object } &&
            context.Payload.Value.TryGetProperty("rules", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in arr.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object) continue;
                string? path = rule.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                string? action = rule.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                if (!string.IsNullOrWhiteSpace(path)) rules.Add(new AppPolicyRule(path!, action ?? "deny"));
            }
        }

        var result = await _appPolicy.ApplyAsync(mode, rules, context.CancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["mode"] = result.Mode,
            ["rules_applied"] = result.RulesApplied,
            ["detail"] = result.Detail
        };
        return result.Success
            ? HandlerResult.Ok(payload)
            : HandlerResult.Fail(result.Error ?? "app policy apply failed", payload);
    }
}

public sealed class CheckAppPolicyHandler : ICommandHandler
{
    private readonly IAppPolicyManager _appPolicy;
    public CheckAppPolicyHandler(IAppPolicyManager appPolicy) => _appPolicy = appPolicy;
    public string CommandType => Protocol.CommandType.CheckAppPolicy;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        var result = await _appPolicy.CheckAsync(context.CancellationToken).ConfigureAwait(false);
        return HandlerResult.Ok(new Dictionary<string, object?>
        {
            ["mode"] = result.Mode,
            ["rules"] = result.RulesApplied,
            ["detail"] = result.Detail,
            ["error"] = result.Error
        });
    }
}
