using System.Text.Json;
using LanAgent.Core.Configuration;
using LanAgent.Core.Logging;
using LanAgent.Core.Management;
using LanAgent.Core.Persistence;
using LanAgent.Core.Protocol;
using LanAgent.Core.Sync;
using Xunit;

namespace LanAgent.Core.Tests;

public sealed class SynchronizationEngineTests : IDisposable
{
    private readonly SqliteStateStore _store;
    private readonly FakeSender _sender = new();
    private readonly FakeApplier _applier = new();
    private readonly SynchronizationEngine _engine;
    private const string DeviceId = "device-sync-1";

    public SynchronizationEngineTests()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"lanagent-sync-{Guid.NewGuid():N}.db");
        _store = new SqliteStateStore(dbPath, NullLog.Instance);
        _engine = new SynchronizationEngine(
            new AgentOptions { EnrollmentKey = "test", SyncResponseTimeoutSeconds = 5 },
            _store, _applier, _sender.SendAsync, NullLog.Instance, DeviceId);
    }

    public void Dispose() => _store.Dispose();

    /// <summary>Simulates the server answering a SYNC_REQUEST with a SYNC_PAYLOAD.</summary>
    private async Task RespondWithPayloadAsync(int policyVersion, string desiredJson = "{}")
    {
        await _sender.UntilAsync(m => (m["type"] as string) == MsgType.SYNC_REQUEST);
        var payload = Protocol.Make(MsgType.SYNC_PAYLOAD, DeviceId, new Dictionary<string, object?>
        {
            ["policy_version"] = policyVersion,
            ["issued_at"] = Tx.NowIso(),
            ["desired_state"] = JsonDocument.Parse(desiredJson).RootElement.Clone()
        });
        _engine.OnSyncPayload(Protocol.Parse(Protocol.Serialize(payload)));
    }

    private static string TypeOf(Dictionary<string, object?> msg) => msg["type"] as string ?? "";

    [Fact]
    public async Task Sync_applies_new_policy_and_reports_success()
    {
        _applier.NextOutcome = new SyncItem("dns", "SUCCESS", "configured");
        var run = _engine.SynchronizeAsync("boot");
        var respond = RespondWithPayloadAsync(12, """{"dns": {"enabled": false, "servers": []}}""");
        var outcome = await run;
        await respond;

        Assert.Equal("SUCCESS", outcome.Status);
        Assert.Equal(12, outcome.PolicyVersion);
        Assert.Equal(12, _store.GetPolicyState().PolicyVersion);

        var started = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.SYNC_STARTED);
        Assert.Equal("boot", started["reason"]);
        var request = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.SYNC_REQUEST);
        Assert.Equal(0, request["current_policy_version"]);
        var result = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.SYNC_RESULT);
        Assert.Equal("SUCCESS", result["status"]);
        Assert.Equal(12, result["policy_version"]);
        Assert.Equal(0, result["previous_policy_version"]);
    }

    [Fact]
    public async Task Same_version_reports_NO_CHANGE()
    {
        _store.SetPolicyState(12, "{}", Tx.NowIso());
        var run = _engine.SynchronizeAsync("periodic");
        var respond = RespondWithPayloadAsync(12);
        var outcome = await run;
        await respond;

        Assert.Equal("NO_CHANGE", outcome.Status);
        var result = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.SYNC_RESULT);
        Assert.Equal("NO_CHANGE", result["status"]);
    }

    [Fact]
    public async Task Partial_apply_keeps_old_version_for_retry()
    {
        _store.SetPolicyState(5, "{}", Tx.NowIso());
        _applier.NextOutcome = new SyncItem("dns", "FAILED", "netsh refused");

        var run = _engine.SynchronizeAsync("reconnect");
        var respond = RespondWithPayloadAsync(9, """{"dns": {"enabled": true, "servers": ["192.168.1.100"]}}""");
        var outcome = await run;
        await respond;

        Assert.Equal("PARTIAL", outcome.Status);
        // Version NOT advanced: periodic reconciliation will retry the failed section.
        Assert.Equal(5, _store.GetPolicyState().PolicyVersion);
        var result = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.SYNC_RESULT);
        Assert.Equal("PARTIAL", result["status"]);
        Assert.Equal(9, result["policy_version"]);
    }

    [Fact]
    public async Task Timeout_when_no_payload_arrives()
    {
        _applier.NextOutcome = null;
        var outcome = await _engine.SynchronizeAsync("boot");
        Assert.Equal("FAILED", outcome.Status);
        Assert.Contains("SYNC_PAYLOAD", outcome.Error);
        // Failure is reported to the server.
        Assert.Contains(_sender.SentSnapshot(), m => TypeOf(m) == MsgType.SYNC_RESULT && Equals(m["status"], "FAILED"));
    }
}

/// <summary>Scriptable desired-state applier (implements the engine's seam directly).</summary>
public sealed class FakeApplier : IDesiredStateApplier
{
    public SyncItem? NextOutcome;
    public JsonElement? LastDesired { get; private set; }

    public Task<List<SyncItem>> ApplyAsync(JsonElement? desired)
    {
        LastDesired = desired;
        return Task.FromResult(NextOutcome is null
            ? new List<SyncItem>()
            : new List<SyncItem> { NextOutcome });
    }
}

public sealed class FakeDns : IDnsManager
{
    public List<string> AppliedServers { get; private set; } = new();
    public bool Called { get; private set; }
    public List<AdapterDnsInfo> GetRelevantAdapters(bool includeWireless) => new();
    public Task<DnsApplyOutcome> ApplyAsync(List<string> servers, bool includeWireless, bool resetToDhcp)
    {
        Called = true;
        AppliedServers = servers;
        return Task.FromResult(new DnsApplyOutcome { Adapters = new(), Changed = 0, Success = true });
    }
    public DnsCheckOutcome Check(List<string> expectedServers, bool includeWireless)
        => new() { Adapters = new(), Expected = expectedServers, Compliant = true };
}

public sealed class FakeBrowser : IBrowserPolicyManager
{
    public string? Chrome { get; private set; }
    public string? Edge { get; private set; }
    public BrowserPolicyState GetChromeIncognito() => new("chrome", "incognito", "unmanaged", null, true);
    public BrowserPolicyState GetEdgeInPrivate() => new("edge", "inprivate", "unmanaged", null, true);
    public BrowserApplyResult Apply(string? chromeIncognitoMode, string? edgeInPrivateMode)
    {
        Chrome = chromeIncognitoMode;
        Edge = edgeInPrivateMode;
        return new BrowserApplyResult(true, null, new Dictionary<string, object?>());
    }
    public BrowserApplyResult Remove(IEnumerable<string> browsers) => new(true, null, new Dictionary<string, object?>());
    public Dictionary<string, object?> Check() => new();
}

public sealed class FakeAppPolicy : IAppPolicyManager
{
    public string? Mode { get; private set; }
    public Task<AppPolicyResult> ApplyAsync(string mode, List<AppPolicyRule> rules, CancellationToken ct = default)
    {
        Mode = mode;
        return Task.FromResult(new AppPolicyResult(true, mode, rules.Count, null, "applied"));
    }
    public Task<AppPolicyResult> CheckAsync(CancellationToken ct = default)
        => Task.FromResult(new AppPolicyResult(true, "applocker", 0, null, "checked"));
}

public sealed class FakeWinget : IWingetClient
{
    public List<string> InstallRequests { get; } = new();
    public List<string> UninstallRequests { get; } = new();
    public Task<WingetOperationResult> InstallAsync(string packageId, string scope = "machine", CancellationToken ct = default)
    {
        InstallRequests.Add(packageId);
        return Task.FromResult(new WingetOperationResult(true, "installed", false, "1.0", 0, "", ""));
    }
    public Task<WingetOperationResult> UninstallAsync(string packageId, CancellationToken ct = default)
    {
        UninstallRequests.Add(packageId);
        return Task.FromResult(new WingetOperationResult(true, "uninstalled", false, null, 0, "", ""));
    }
    public Task<WingetOperationResult> UpgradeAsync(string packageId, CancellationToken ct = default)
        => Task.FromResult(new WingetOperationResult(true, "upgraded", false, "2.0", 0, "", ""));
    public Task<AppPackageStatus> CheckAsync(string packageId, CancellationToken ct = default)
        => Task.FromResult(new AppPackageStatus(true, "1.0", "registry", "found"));
    public string? LastLocateError => null;
}

public sealed class DesiredStateApplierTests
{
    private readonly FakeDns _dns = new();
    private readonly FakeBrowser _browser = new();
    private readonly FakeAppPolicy _appPolicy = new();
    private readonly FakeWinget _winget = new();
    private DesiredStateApplier Applier => new(_dns, _browser, _appPolicy, _winget, NullLog.Instance);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Applies_all_sections()
    {
        var desired = Parse("""
        {
          "dns": { "enabled": true, "servers": ["192.168.1.100"], "apply_to_wireless": true },
          "browser_policy": { "chrome": { "incognito": "disabled" }, "edge": { "inprivate": "forced" } },
          "app_policy": { "mode": "applocker", "rules": [ { "path": "*\\\\tor*\\\\*.exe", "action": "deny" } ] },
          "apps": [ { "package_id": "Microsoft.VisualStudioCode", "state": "installed" } ]
        }
        """);
        var items = await Applier.ApplyAsync(desired);

        Assert.True(_dns.Called);
        Assert.Equal(new[] { "192.168.1.100" }, _dns.AppliedServers);
        Assert.Equal("disabled", _browser.Chrome);
        Assert.Equal("forced", _browser.Edge);
        Assert.Equal("applocker", _appPolicy.Mode);
        Assert.Contains("Microsoft.VisualStudioCode", _winget.InstallRequests);
        Assert.All(items, i => Assert.Equal("SUCCESS", i.Status));
        Assert.Contains(items, i => i.Item == "dns");
        Assert.Contains(items, i => i.Item == "app_policy");
        Assert.Contains(items, i => i.Item == "apps.Microsoft.VisualStudioCode");
    }

    [Fact]
    public async Task Disabled_dns_section_is_a_no_op_success()
    {
        var desired = Parse("""{ "dns": { "enabled": false, "servers": [] } }""");
        var items = await Applier.ApplyAsync(desired);
        Assert.False(_dns.Called);
        var dns = Assert.Single(items);
        Assert.Equal("SUCCESS", dns.Status);
    }

    [Fact]
    public async Task Null_desired_state_applies_nothing()
    {
        var items = await Applier.ApplyAsync(null);
        Assert.Empty(items);
        Assert.False(_dns.Called);
    }

    [Fact]
    public async Task Apps_absent_state_uninstalls()
    {
        var desired = Parse("""{ "apps": [ { "package_id": "Some.App", "state": "absent" } ] }""");
        await Applier.ApplyAsync(desired);
        Assert.Contains("Some.App", _winget.UninstallRequests);
        Assert.Empty(_winget.InstallRequests);
    }
}
