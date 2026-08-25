using LanAgent.Core.Configuration;
using LanAgent.Core.Logging;
using LanAgent.Core.Persistence;

namespace LanAgent.Core.Identity;

/// <summary>Credentials the connection manager uses to authenticate (see PROTOCOL_CONTRACT §4).</summary>
public interface IAgentCredentials
{
    string DeviceId { get; }
    string EnrollmentKey { get; }
    bool HasToken { get; }
    string? Token { get; }
    void SaveToken(string token);
    void ClearToken();
}

/// <summary>
/// Permanent device identity: a UUID generated once at first start, persisted in the state store
/// (and mirrored to a file for operators). Survives reboots, service restarts and IP changes —
/// the IP is NEVER the identity.
/// </summary>
public sealed class DeviceIdentityService : IAgentCredentials
{
    private const string MetaDeviceId = "device_id";
    private const string MetaDeviceCreatedAt = "device_created_at";
    private const string MetaDeviceToken = "device_token";
    private const string MetaLastHandshakeVersion = "last_server_handshake";

    private readonly IStateStore _store;
    private readonly IAgentLog _log;
    private readonly string _mirrorPath;
    private readonly object _lock = new();

    public DeviceIdentityService(IStateStore store, AgentOptions options, IAgentLog log)
    {
        _store = store;
        _log = log;
        _mirrorPath = Path.Combine(options.DataDirectory, "device-id.txt");
        DeviceId = LoadOrCreate();
    }

    public string DeviceId { get; }

    public string EnrollmentKey { get; set; } = "";

    public bool HasToken => Token is not null;

    public string? Token
    {
        get
        {
            lock (_lock) { return _store.GetMeta(MetaDeviceToken); }
        }
    }

    public void SaveToken(string token)
    {
        lock (_lock)
        {
            _store.SetMeta(MetaDeviceToken, token);
            _log.Info("identity", "device token stored", new { device_id = DeviceId });
        }
    }

    public void ClearToken()
    {
        lock (_lock)
        {
            _store.SetMeta(MetaDeviceToken, "");
            _log.Warn("identity", "device token cleared; will re-register", new { device_id = DeviceId });
        }
    }

    public void RecordHandshake(string version)
    {
        _store.SetMeta(MetaLastHandshakeVersion, version);
    }

    private string LoadOrCreate()
    {
        string? id = _store.GetMeta(MetaDeviceId);
        if (!string.IsNullOrWhiteSpace(id)) return id;

        id = Util.Tx.NewUuid();
        _store.SetMeta(MetaDeviceId, id);
        _store.SetMeta(MetaDeviceCreatedAt, Util.Tx.NowIso());
        _log.Info("identity", "new device identity generated", new { device_id = id, note = "persisted; survives reboot" });
        try
        {
            File.WriteAllText(_mirrorPath, id + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _log.Warn("identity", "could not write device-id mirror file", new { path = _mirrorPath, error = ex.Message });
        }
        return id;
    }
}
