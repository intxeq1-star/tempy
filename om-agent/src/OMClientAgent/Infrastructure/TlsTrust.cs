using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core.Models;

namespace OMClientAgent.Infrastructure;

public sealed class TlsTrust
{
    private readonly ILogger<TlsTrust> _logger;
    private readonly AgentConfigService _configService;

    public TlsTrust(ILogger<TlsTrust> logger, AgentConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    private AgentConfiguration _config => _configService.Current;

    public HttpClient CreateHttpClient()
    {
        var handler = CreateHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(_config.MachineId))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-OM-Machine", _config.MachineId);
        if (!string.IsNullOrWhiteSpace(_config.AuthKey))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-OM-Key", _config.AuthKey);
        return client;
    }

    public HttpClientHandler CreateHandler()
    {
        var handler = new HttpClientHandler();
        if (!_config.VerifyServerCertificate)
        {
            _logger.LogCritical("VerifyServerCertificate is false. Refusing to disable TLS validation.");
            throw new InvalidOperationException("TLS validation may not be disabled. Provide a trusted internal CA instead.");
        }
        handler.ServerCertificateCustomValidationCallback = ValidateInternalLanCertificate;
        return handler;
    }

    private bool ValidateInternalLanCertificate(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, System.Net.Security.SslPolicyErrors errors)
    {
        if (errors == System.Net.Security.SslPolicyErrors.None)
            return true;

        var caFile = _config.CaCertificateFilePath;
        if (!string.IsNullOrWhiteSpace(caFile) && File.Exists(caFile) && cert is not null)
        {
            try
            {
                using var ca = new X509Certificate2(caFile);
                using var custom = new X509Chain();
                custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                custom.ChainPolicy.CustomTrustStore.Add(ca);
                custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                custom.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                return custom.Build(cert);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom CA trust validation failed for '{Host}'.", request.RequestUri?.Host);
                return false;
            }
        }

        _logger.LogWarning("TLS validation failed for '{Host}': {Errors}", request.RequestUri?.Host, errors);
        return false;
    }
}
