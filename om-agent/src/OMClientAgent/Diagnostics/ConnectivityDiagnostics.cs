using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core;
using OMClientAgent.Infrastructure;
using OMClientAgent.Services;

namespace OMClientAgent.Diagnostics;

public sealed class ConnectivityDiagnostics
{
    private const int TcpPort = 8443;
    private readonly AgentConfigService _config;
    private readonly TlsTrust _tls;
    private readonly AuthenticationManager _auth;
    private readonly LocalDatabase _db;
    private readonly ILogger<ConnectivityDiagnostics> _logger;

    public ConnectivityDiagnostics(AgentConfigService config, TlsTrust tls, AuthenticationManager auth, LocalDatabase db, ILogger<ConnectivityDiagnostics> logger)
    {
        _config = config;
        _tls = tls;
        _auth = auth;
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DiagnosticStage>> RunAsync(CancellationToken ct)
    {
        var serverUrl = _config.Current.ServerUrl;
        var (host, port) = ResolveHostPort();
        var stages = new List<DiagnosticStage>();

        stages.Add(Stage("Network adapter", IsNetworkUp()));
        stages.Add(Stage("Server IP reachable", await Exec(2_000, tok => PingAsync(host, tok), ct).ConfigureAwait(false)));
        stages.Add(Stage($"TCP {port}", await Exec(8_000, tok => TcpCheckAsync(host, port, tok), ct).ConfigureAwait(false)));
        stages.Add(Stage("TLS", await Exec(10_000, tok => TlsCheckAsync(host, port, tok), ct).ConfigureAwait(false)));
        stages.Add(Stage("Authentication", await Exec(12_000, tok => AuthCheckAsync(tok), ct).ConfigureAwait(false)));
        stages.Add(Stage("SignalR / WebSocket", await Exec(12_000, tok => SignalRCheckAsync(tok), ct).ConfigureAwait(false)));
        stages.Add(Stage("HTTPS synchronization", await Exec(12_000, tok => SyncCheckAsync(tok), ct).ConfigureAwait(false)));
        stages.Add(Stage("Local database", DbCheck()));

        var report = new DiagnosticsReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            AgentVersion = OmProtocol.Version,
            MachineId = _config.Current.MachineId,
            ServerUrl = serverUrl,
            LastHeartbeat = _db.GetSetting("LastConnectedAtUtc"),
            LastSynchronization = _db.GetSetting("LastSyncAtUtc"),
            PendingJobs = _db.GetPendingJobs().Count,
            PolicyVersion = _db.GetSetting("LatestPolicyVersion") ?? "0",
            ConnectionState = _db.LoadConnectionState().ToString(),
            Stages = stages
        };
        Persist(report);
        return stages;
    }

    private DiagnosticStage Stage(string name, bool ok, string? detail = null)
        => new() { Stage = name, Ok = ok, Detail = detail, TimestampUtc = DateTime.UtcNow };

    private static async Task<bool> Exec(int ms, Func<CancellationToken, Task<bool>> stage, CancellationToken outer)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(ms);
            return await stage(cts.Token).ConfigureAwait(false);
        }
        catch { return false; }
    }

    private (string host, int port) ResolveHostPort()
    {
        var candidates = OmUrls.Candidates(_config.Current).ToList();
        if (candidates.Count == 0) return ("om-server.local", TcpPort);
        foreach (var baseUrl in candidates)
        {
            try
            {
                var uri = new Uri(baseUrl);
                if (System.Net.IPAddress.TryParse(uri.Host, out _))
                    return (uri.Host, uri.IsDefaultPort ? TcpPort : uri.Port);
            }
            catch { }
        }
        var first = new Uri(candidates[0]);
        return (first.Host, first.IsDefaultPort ? TcpPort : first.Port);
    }

    private bool IsNetworkUp()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return false; }
    }

    private static async Task<bool> PingAsync(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 3000).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private static async Task<bool> TcpCheckAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> TlsCheckAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, cert, chain, errors) =>
            {
                var handler = _tls.CreateHandler();
                return errors == System.Net.Security.SslPolicyErrors.None;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct).ConfigureAwait(false);
            return ssl.IsAuthenticated;
        }
        catch { return false; }
    }

    private async Task<bool> AuthCheckAsync(CancellationToken ct)
    {
        var enrolled = await _auth.EnsureEnrolledAsync(ct).ConfigureAwait(false);
        return enrolled && !string.IsNullOrWhiteSpace(_auth.AuthKey);
    }

    private async Task<bool> SignalRCheckAsync(CancellationToken ct)
    {
        try
        {
            var baseUri = new Uri(_config.Current.ServerUrl.TrimEnd('/') + "/");
            var path = _config.Current.SignalRHubPath.TrimStart('/');
            var q = $"?role=agent&machine={System.Net.WebUtility.UrlEncode(_config.Current.MachineId)}&key={System.Net.WebUtility.UrlEncode(_config.Current.AuthKey ?? string.Empty)}";
            var hubUrl = new Uri(baseUri, path).ToString() + q;
            var conn = new HubConnectionBuilder()
                .WithUrl(hubUrl, o => { o.HttpMessageHandlerFactory = _ => _tls.CreateHandler(); })
                .Build();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            await conn.StartAsync(cts.Token).ConfigureAwait(false);
            await conn.StopAsync(cts.Token).ConfigureAwait(false);
            await conn.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> SyncCheckAsync(CancellationToken ct)
    {
        try
        {
            using var http = _tls.CreateHttpClient();
            http.Timeout = TimeSpan.FromSeconds(12);
            var uri = new Uri(new Uri(_config.Current.ServerUrl.TrimEnd('/') + "/"), OmApi.Config);
            var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private bool DbCheck()
    {
        try { return _db.GetPendingJobs() != null; }
        catch { return false; }
    }

    private void Persist(DiagnosticsReport report)
    {
        try
        {
            var file = Path.Combine(_config.Current.DataDirectory, "diagnostics.json");
            File.WriteAllText(file, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static string ToText(IReadOnlyList<DiagnosticStage> stages)
    {
        var sb = new StringBuilder();
        foreach (var s in stages)
            sb.AppendLine($"{s.Stage.ToUpperInvariant()}: {(s.Ok ? "OK" : "FAILED")}");
        var firstFailing = stages.FirstOrDefault(s => !s.Ok);
        if (firstFailing is not null)
            sb.AppendLine($"\nFirst failure: {firstFailing.Stage} — {firstFailing.Detail ?? "no detail"}");
        return sb.ToString();
    }
}

public sealed class DiagnosticStage
{
    public string Stage { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string? Detail { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public sealed class DiagnosticsReport
{
    public DateTime GeneratedAtUtc { get; set; }
    public string AgentVersion { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string? LastHeartbeat { get; set; }
    public string? LastSynchronization { get; set; }
    public int PendingJobs { get; set; }
    public string PolicyVersion { get; set; } = string.Empty;
    public string ConnectionState { get; set; } = string.Empty;
    public List<DiagnosticStage> Stages { get; set; } = new();
}
