using System.IO.Compression;
using System.Security.Cryptography;
using LanAgent.Core.Configuration;
using LanAgent.Core.Logging;

namespace LanAgent.Core.Update;

/// <summary>
/// UPDATE_AGENT support (PROTOCOL_CONTRACT §10): download from the trusted management server,
/// verify SHA-256 (+ optional RSA signature when a public key is configured), stage, then swap
/// binaries via a detached helper that stops/starts the Windows service. Device identity and local
/// state live in ProgramData and survive the swap.
/// </summary>
public static class AgentUpdater
{
    public static async Task<Commands.HandlerResult> ExecuteAsync(
        Commands.CommandContext context,
        AgentOptions options,
        IAgentLog log,
        string httpBaseUrl)
    {
        string? version = context.PayloadString("version");
        string? fileName = context.PayloadString("file_name");
        string? sha256Hex = context.PayloadString("sha256");
        string? signature = context.PayloadString("signature");

        if (string.IsNullOrWhiteSpace(version)) return Commands.HandlerResult.Fail("payload.version is required");
        if (!Version.TryParse(version, out var targetVersion)) return Commands.HandlerResult.Fail($"payload.version '{version}' is not a valid version");
        if (!Version.TryParse(AgentInfo.Version, out var currentVersion)) currentVersion = new Version(0, 0);
        if (targetVersion <= currentVersion)
            return Commands.HandlerResult.Ok(new Dictionary<string, object?>
            {
                ["staged"] = false, ["version"] = version, ["verified"] = true, ["applying"] = false,
                ["note"] = $"already running {AgentInfo.Version}"
            });

        fileName = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? $"lanagent-{version}.zip" : fileName!);
        if (string.IsNullOrWhiteSpace(sha256Hex))
            return Commands.HandlerResult.Fail("payload.sha256 is required (update packages must be hash-verified)");

        try
        {
            // ---- download from the trusted management server only ----
            string stagingRoot = Path.Combine(options.DataDirectory, "updates", version);
            Directory.CreateDirectory(stagingRoot);
            string zipPath = Path.Combine(stagingRoot, fileName);
            string extractPath = Path.Combine(stagingRoot, "package");

            using var http = new HttpClient { BaseAddress = new Uri(httpBaseUrl), Timeout = TimeSpan.FromMinutes(10) };
            string url = $"/agent/update/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(fileName)}";
            byte[] package;
            try
            {
                package = await http.GetByteArrayAsync(url, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Commands.HandlerResult.Fail($"download failed from {url}: {ex.Message}");
            }

            // ---- verify ----
            string actualHash = Convert.ToHexString(SHA256.HashData(package));
            if (!string.Equals(actualHash, sha256Hex.Replace("-", "").Trim(), StringComparison.OrdinalIgnoreCase))
                return Commands.HandlerResult.Fail($"sha256 mismatch: expected {sha256Hex}, got {actualHash}");

            bool signatureVerified = false;
            if (!string.IsNullOrWhiteSpace(signature))
            {
                if (string.IsNullOrWhiteSpace(options.AgentUpdatePublicKey))
                    return Commands.HandlerResult.Fail("update is signed but no AgentUpdatePublicKey is configured — refusing");
                string? verifyError = VerifySignature(package, signature!, options.AgentUpdatePublicKey);
                if (verifyError is not null)
                    return Commands.HandlerResult.Fail($"signature verification failed: {verifyError}");
                signatureVerified = true;
            }

            // ---- stage ----
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, recursive: true);
            await File.WriteAllBytesAsync(zipPath, package, context.CancellationToken).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(zipPath, extractPath);

            string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string scriptPath = Path.Combine(stagingRoot, "apply-update.cmd");
            const string service = "LanAgent";
            string script =
                "@echo off\r\n" +
                "echo LanAgent update applying... >> \"..\\..\\update.log\"\r\n" +
                $"net stop {service}\r\n" +
                "timeout /t 3 /nobreak >nul\r\n" +
                $"robocopy \"{extractPath}\" \"{installDir}\" /MIR /R:2 /W:2 /XF appsettings.json *.log /XD logs updates\r\n" +
                $"net start {service}\r\n";
            await File.WriteAllTextAsync(scriptPath, script, context.CancellationToken).ConfigureAwait(false);

            // ---- apply (detached; this process is about to be replaced) ----
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = stagingRoot
            };
            System.Diagnostics.Process.Start(psi);

            log.Info("update", "agent update staged and applying", new
            {
                version, file = fileName, sha256 = actualHash, signature_verified = signatureVerified, install_dir = installDir
            });

            return Commands.HandlerResult.Ok(new Dictionary<string, object?>
            {
                ["staged"] = true,
                ["version"] = version,
                ["verified"] = true,
                ["signature_verified"] = signatureVerified,
                ["applying"] = true,
                ["note"] = "result reported before service restart; agent reconnects automatically after update"
            });
        }
        catch (Exception ex)
        {
            log.Error("update", "agent update failed", ex.Message, ex);
            return Commands.HandlerResult.Fail($"update failed: {ex.Message}");
        }
    }

    /// <summary>Returns null when the signature verifies, otherwise the reason it failed.</summary>
    internal static string? VerifySignature(byte[] data, string signatureBase64, string publicKeyXmlOrSpki)
    {
        try
        {
            using RSA rsa = RSA.Create();
            string key = publicKeyXmlOrSpki.Trim();
            try
            {
                rsa.FromXmlString(key);
            }
            catch (Exception)
            {
                // Not XML — try base64 SPKI.
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            }

            byte[] signature = Convert.FromBase64String(signatureBase64.Trim());
            return rsa.VerifyData(data, signature, System.Security.Cryptography.HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                ? null
                : "signature does not match package";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
