using System.Security.Cryptography;

namespace Uts.Api.Services;

/// <summary>
/// Generates the 6-digit access codes that unlock full detail on the public
/// tracking page. Stored as-is (they are shared openly with the recipient);
/// brute-force attempts are kept in check by rate limiting on the unlock endpoint.
/// </summary>
public sealed class PinService
{
    public string Generate()
        => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
}
