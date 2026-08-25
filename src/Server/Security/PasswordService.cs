using System.Security.Cryptography;

namespace LanManagement.Server.Security;

public interface IPasswordService
{
    string Hash(string password);
    bool Verify(string password, string encodedHash);
}

/// <summary>PBKDF2-SHA512 password hashes; plaintext passwords are never written to the database.</summary>
public sealed class Pbkdf2PasswordService : IPasswordService
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Algorithm = "SHA512";

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, HashBytes);
        return $"pbkdf2-{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encodedHash))
        {
            return false;
        }

        var parts = encodedHash.Split('$', StringSplitOptions.None);
        if (parts.Length != 4 || !string.Equals(parts[0], $"pbkdf2-{Algorithm}", StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out var iterations) || iterations < 100_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public interface IAgentTokenHasher
{
    string Hash(string token);
    bool Verify(string token, string? hash);
    bool FixedTimeEquals(string left, string right);
}

public sealed class AgentTokenHasher : IAgentTokenHasher
{
    public string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return "sha256$" + Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }

    public bool Verify(string token, string? hash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(hash) || !hash.StartsWith("sha256$", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(hash[7..]);
            var actual = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool FixedTimeEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(left ?? string.Empty));
        var rightHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(right ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
