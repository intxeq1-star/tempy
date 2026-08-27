using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public sealed class AuthenticationManager
{
    private readonly AgentConfigService _configService;
    private readonly MachineIdentityProvider _identity;
    private readonly LocalDatabase _db;
    private readonly TlsTrust _tls;
    private readonly ILogger<AuthenticationManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AuthenticationManager(AgentConfigService configService, MachineIdentityProvider identity, LocalDatabase db, TlsTrust tls, ILogger<AuthenticationManager> logger)
    {
        _configService = configService;
        _identity = identity;
        _db = db;
        _tls = tls;
        _logger = logger;
    }

    public Core.Models.MachineIdentity Machine => _identity.GetOrCreate();
    public string? AuthKey => _configService.Current.AuthKey;
    public bool IsEnrolled => !string.IsNullOrWhiteSpace(_configService.Current.AuthKey);

    public RegisterRequest BuildRegisterRequest()
    {
        var m = _identity.GetOrCreate();
        return new RegisterRequest
        {
            MachineId = m.MachineId,
            ComputerName = m.ComputerName,
            AgentVersion = m.AgentVersion,
            IpAddress = GetPrimaryIp()
        };
    }

    public async Task<bool> EnsureEnrolledAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (IsEnrolled) return true;

            var stored = _db.GetSetting("AuthKey");
            if (!string.IsNullOrWhiteSpace(stored))
            {
                _configService.Update(cfg => cfg.AuthKey = stored);
                _logger.LogInformation("Resumed persisted authKey for {MachineId}.", _configService.Current.MachineId);
                return true;
            }

            return await TryRegisterAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> TryRegisterAsync(CancellationToken ct)
    {
        var machine = _identity.GetOrCreate();
        var request = BuildRegisterRequest();

        foreach (var baseUrl in OmUrls.Candidates(_configService.Current))
        {
            try
            {
                using var http = _tls.CreateHttpClient();
                using var response = await http.PostAsJsonAsync(new Uri(new Uri(baseUrl), OmApi.Register), request, OmJson.Options, ct).ConfigureAwait(false);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Register at {Base} rejected with HTTP 401.", baseUrl);
                    return false;
                }
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Register at {Base} returned {Status}.", baseUrl, response.StatusCode);
                    continue;
                }

                var reg = await response.Content.ReadFromJsonAsync<RegisterResponse>(OmJson.Options, ct).ConfigureAwait(false);
                if (reg is null)
                {
                    _logger.LogWarning("Register at {Base} returned an empty body.", baseUrl);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(reg.AuthKey))
                {
                    _configService.Update(cfg => { cfg.AuthKey = reg.AuthKey; cfg.ServerUrl = baseUrl; });
                    _db.SetSetting("AuthKey", reg.AuthKey);
                    _logger.LogInformation("Enrolled {MachineId} via {Base}; authKey granted.", machine.MachineId, baseUrl);
                    return true;
                }

                _logger.LogError("Register at {Base} returned no authKey and we have no stored key. Re-enrollment required.", baseUrl);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Enrollment attempt against {Base} failed: {Message}", baseUrl, ex.Message);
            }
        }
        return false;
    }

    private static string? GetPrimaryIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                var gateway = props.GatewayAddresses.FirstOrDefault();
                if (gateway is null) continue;
                foreach (var addr in props.UnicastAddresses)
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return addr.Address.ToString();
            }
        }
        catch { }
        return null;
    }
}
