using System.Security.Cryptography;
using System.Text;

namespace Uts.Api.Services;

/// <summary>
/// Generates and verifies the 6-digit access codes that unlock full detail
/// on the public tracking page. Stored hashed (SHA-256, per-shipment salt).
/// </summary>
public sealed class PinService
{
    public string Generate()
        => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    public (string Salt, string Hash) Hash(string pin)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var salt = Convert.ToHexString(saltBytes);
        return (salt, Compute(pin, salt));
    }

    public bool Verify(string pin, string salt, string expectedHash)
    {
        var actual = Compute(pin, salt);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes(expectedHash));
    }

    private static string Compute(string pin, string salt)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(salt + ":" + pin));
        return Convert.ToHexString(bytes);
    }
}
