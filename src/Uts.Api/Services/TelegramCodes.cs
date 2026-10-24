using System.Security.Cryptography;

namespace Uts.Api.Services;

public static class TelegramCodes
{
    /// <summary>Claim code for /start &lt;code&gt; binding — unambiguous alphabet,
    /// generated with a crypto RNG.</summary>
    public static string NewClaimCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I/L
        var bytes = RandomNumberGenerator.GetBytes(8);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}
